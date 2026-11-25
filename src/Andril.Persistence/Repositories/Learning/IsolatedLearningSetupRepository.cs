using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>
/// Controlled, unregistered composition of the provisional Learning contracts. The supplied
/// boundary/catalogue must be current backend authorities; IDs never authorize access.
/// Production M2 standing/session/catalogue adapters remain unavailable. Only an opted-in local
/// disposable host is supported, so this implementation cannot accidentally activate production.
/// Database locks reuse the existing M2 subject fence and Account -> Membership -> Profile order.
/// No positive authorization cache or transaction retry is used.
/// </summary>
internal sealed class IsolatedLearningSetupRepository : ILearningSetupMutationRepository
{
    private readonly DbContextOptions<LearningDbContext> options;
    private readonly ICurrentUser user;
    private readonly ILearningSetupAccessBoundary boundary;
    private readonly ILearningSkillCatalogueReader catalogues;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal enum Reconciliation { ConfirmedCommitted, ConfirmedNotCommitted, Unresolved }

    internal IsolatedLearningSetupRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        ILearningSetupAccessBoundary boundary, ILearningSkillCatalogueReader catalogues)
    {
        this.options = options; this.user = user; this.boundary = boundary; this.catalogues = catalogues;
        using var db = Open();
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database is not { } name
            || !name.StartsWith("andril_m2_test_", StringComparison.Ordinal)
            || !Guid.TryParseExact(name["andril_m2_test_".Length..], "N", out _) || settings.Pooling)
            throw new InvalidOperationException("Learning coordination is restricted to an isolated local test database.");
    }

    private LearningDbContext Open() => new(options);

    private async Task OpenFence(LearningDbContext db, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        // Acquire the EXISTING M2 subject key before creating a Serializable snapshot. Otherwise
        // a wait could leave a reconciliation snapshot unable to see a committed no-op receipt.
        // Pooling is prohibited above: disposal always releases this session lock, even on error.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({user.UserId!.Value.ToString()},0))", ct);
    }

    private async Task<LearningSetupAccessState> Authorize(LearningSetupScope scope, LearningSetupOperation operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!user.IsAuthenticated || user.UserId is null || user.UserId == Guid.Empty) return LearningSetupAccessState.Denied;
        var access = await boundary.CheckAsync(operation, ct);
        if (access.State != LearningSetupAccessState.Allowed) return access.State;
        return access.Scope is { } granted && granted.AccountId == scope.AccountId
            && granted.LearningProfileId == scope.LearningProfileId && granted.Module == scope.Module
            ? LearningSetupAccessState.Allowed : LearningSetupAccessState.Denied;
    }

    private async Task<bool> LockOwners(LearningDbContext db, LearningSetupScope scope, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
        var subject = user.UserId!.Value;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{subject.ToString()},true), set_config('andril.account',{scope.AccountId.ToString()},true), set_config('andril.module','learning',true)", ct);
        // The narrow database function locks M2 rows without granting runtime UPDATE or broad
        // identity-table SELECT. It cannot substitute for current standing/session/purpose evidence.
        return await db.Database.SqlQuery<bool>($"SELECT andril_learning.lock_learning_owners({subject},{scope.AccountId},{scope.LearningProfileId}) AS \"Value\"").SingleAsync(ct);
    }

    private Task Checkpoint(string name, CancellationToken ct) => TestCheckpoint?.Invoke(name, ct) ?? Task.CompletedTask;

    public async Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
    {
        var access = await Authorize(scope, LearningSetupOperation.ReadSetup, cancellationToken);
        if (access != LearningSetupAccessState.Allowed) return ReadFailure(access);
        try
        {
            await using var db = Open();
            await OpenFence(db, cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (!await LockOwners(db, scope, cancellationToken)) return LearningSetupReadResult.Denied();
            access = await Authorize(scope, LearningSetupOperation.ReadSetup, cancellationToken);
            if (access != LearningSetupAccessState.Allowed) return ReadFailure(access);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT andril_learning.lock_profile_revision({scope.LearningProfileId},{scope.AccountId})", cancellationToken);
            var result = await new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(scope, cancellationToken);
            await Checkpoint("read-release", cancellationToken);
            access = await Authorize(scope, LearningSetupOperation.ReadSetup, cancellationToken);
            if (access != LearningSetupAccessState.Allowed) return ReadFailure(access);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception error) when (IsolatedLearningPreferencesStore.IsConflict(error) || IsUnavailable(error))
        { return LearningSetupReadResult.Unavailable(); }
    }

    public Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision,
        string catalogueRevision, CancellationToken cancellationToken = default) =>
        ReplaceWithOperationAsync(Guid.NewGuid(), replacement, expectedStorageRevision, catalogueRevision, cancellationToken);

    public Task<LearningSetupWriteOutcome> ReplaceWithOperationAsync(Guid operationId, LearningSetupSnapshot replacement,
        string expectedStorageRevision, string catalogueRevision, CancellationToken ct = default) =>
        MutateAsync(operationId, replacement.Scope, replacement, null, expectedStorageRevision, catalogueRevision, ct);

    public Task<LearningSetupWriteOutcome> ChangeGoalAsync(Guid operationId, LearningSetupScope scope,
        LearningGoalChange change, string expectedRevision, CancellationToken cancellationToken = default) =>
        MutateAsync(operationId, scope, null, change ?? throw new ArgumentNullException(nameof(change)), expectedRevision, null, cancellationToken);

    private async Task<LearningSetupWriteOutcome> MutateAsync(Guid operationId, LearningSetupScope scope,
        LearningSetupSnapshot? replacement, LearningGoalChange? goal, string expectedStorageRevision,
        string? catalogueRevision, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A server operation identity is required.", nameof(operationId));
        var access = await Authorize(scope, LearningSetupOperation.UpdateSetup, ct);
        if (access != LearningSetupAccessState.Allowed) return WriteFailure(access);
        if (!LearningSetupRevision.TryParse(expectedStorageRevision, out var expected)) return LearningSetupWriteOutcome.Conflict;
        try
        {
            await using var db = Open();
            await OpenFence(db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await LockOwners(db, scope, ct)) return LearningSetupWriteOutcome.Denied;
            access = await Authorize(scope, LearningSetupOperation.UpdateSetup, ct);
            if (access != LearningSetupAccessState.Allowed) return WriteFailure(access);
            var store = new IsolatedLearningPreferencesStore(db);
            IsolatedLearningPreferencesStore.ReplacementState prepared;
            if (replacement is not null)
            {
                var catalogue = await catalogues.ReadCurrentAsync(ct);
                if (catalogue.State != LearningSkillCatalogueReadState.Available) return LearningSetupWriteOutcome.Unavailable;
                if (catalogue.Revision != catalogueRevision) return LearningSetupWriteOutcome.Conflict;
                prepared = await store.PrepareReplacementAsync(replacement, expectedStorageRevision, catalogue.Catalogue!, ct);
            }
            else prepared = await store.PrepareGoalAsync(scope, goal!, expectedStorageRevision, ct);
            if (prepared == IsolatedLearningPreferencesStore.ReplacementState.Conflict) return LearningSetupWriteOutcome.Conflict;
            if (prepared == IsolatedLearningPreferencesStore.ReplacementState.Unavailable) return LearningSetupWriteOutcome.Unavailable;
            await Checkpoint("prepared", ct);
            access = await Authorize(scope, LearningSetupOperation.UpdateSetup, ct);
            if (access != LearningSetupAccessState.Allowed) return WriteFailure(access);
            if (replacement is not null)
            {
                var currentCatalogue = await catalogues.ReadCurrentAsync(ct);
                if (currentCatalogue.State != LearningSkillCatalogueReadState.Available) return LearningSetupWriteOutcome.Unavailable;
                if (currentCatalogue.Revision != catalogueRevision) return LearningSetupWriteOutcome.Conflict;
            }
            var current = await store.ReadUnderTransactionAsync(scope, ct);
            if (!LearningSetupRevision.TryParse(current.StorageRevision, out var result)) return LearningSetupWriteOutcome.Unavailable;
            var intent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Kind = goal?.Kind.ToString() ?? "selections", GoalId = goal?.GoalId, Text = goal?.Text,
                Selections = replacement?.Selections.OrderBy(s => s.Id).Select(s => new { s.Id, s.SkillId, s.Kind }),
                Goals = replacement?.Goals.OrderBy(g => g.Id).Select(g => new { g.Id, g.Text })
            }))));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_mutation_receipts(operation_id,learning_profile_id,module,generation_id,expected_version,result_version,intent_hash) VALUES({operationId},{scope.LearningProfileId},'learning',{result!.GenerationId},{expected!.Version},{result.Version},{intent})", ct);
            await Checkpoint("before-commit", ct);
            // Revalidate after every potentially injectable/awaited checkpoint. No network authority
            // is configured here; production authority adapters remain a separate integration gate.
            if (replacement is not null)
            {
                var releaseCatalogue = await catalogues.ReadCurrentAsync(ct);
                if (releaseCatalogue.State != LearningSkillCatalogueReadState.Available) return LearningSetupWriteOutcome.Unavailable;
                if (releaseCatalogue.Revision != catalogueRevision) return LearningSetupWriteOutcome.Conflict;
            }
            access = await Authorize(scope, LearningSetupOperation.UpdateSetup, ct);
            if (access != LearningSetupAccessState.Allowed) return WriteFailure(access);
            return await store.CommitPreparedAsync(ct);
        }
        catch (Exception error) when (IsolatedLearningPreferencesStore.IsConflict(error))
        { return LearningSetupWriteOutcome.Conflict; }
        catch (Exception error) when (IsUnavailable(error))
        { return LearningSetupWriteOutcome.Unavailable; }
    }

    internal async Task<Reconciliation> ReconcileAsync(LearningSetupScope scope, Guid operationId, string expectedRevision, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty || !LearningSetupRevision.TryParse(expectedRevision, out var expected))
            return Reconciliation.Unresolved;
        if (await Authorize(scope, LearningSetupOperation.UpdateSetup, ct) != LearningSetupAccessState.Allowed)
            return Reconciliation.Unresolved;
        try
        {
            await using var db = Open();
            await OpenFence(db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            if (!await LockOwners(db, scope, ct)) return Reconciliation.Unresolved;
            // This waits for any in-flight mutation before drawing a non-commit conclusion.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT andril_learning.lock_profile_revision({scope.LearningProfileId},{scope.AccountId})", ct);
            var receipt = await db.Set<LearningMutationReceiptRow>().AsNoTracking().SingleOrDefaultAsync(
                r => r.OperationId == operationId && r.LearningProfileId == scope.LearningProfileId && r.Module == scope.Module, ct);
            var current = await new IsolatedLearningPreferencesStore(db).ReadUnderTransactionAsync(scope, ct);
            if (await Authorize(scope, LearningSetupOperation.UpdateSetup, ct) != LearningSetupAccessState.Allowed)
                return Reconciliation.Unresolved;
            await tx.CommitAsync(ct);
            if (receipt is not null) return receipt.GenerationId == expected!.GenerationId && receipt.ExpectedVersion == expected.Version
                ? Reconciliation.ConfirmedCommitted : Reconciliation.Unresolved;
            return current.State == LearningSetupReadState.Available && current.StorageRevision == expectedRevision
                ? Reconciliation.ConfirmedNotCommitted : Reconciliation.Unresolved;
        }
        catch (Exception error) when (IsolatedLearningPreferencesStore.IsConflict(error) || IsUnavailable(error))
        { return Reconciliation.Unresolved; }
    }

    private static bool IsUnavailable(Exception error) => error is NpgsqlException or IOException or TimeoutException;
    private static LearningSetupReadResult ReadFailure(LearningSetupAccessState state) => state == LearningSetupAccessState.Denied
        ? LearningSetupReadResult.Denied() : LearningSetupReadResult.Unavailable();
    private static LearningSetupWriteOutcome WriteFailure(LearningSetupAccessState state) => state == LearningSetupAccessState.Denied
        ? LearningSetupWriteOutcome.Denied : LearningSetupWriteOutcome.Unavailable;
}
