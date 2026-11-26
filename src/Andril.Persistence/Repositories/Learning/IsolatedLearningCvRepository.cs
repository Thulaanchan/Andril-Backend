using System.Data;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Unregistered CV-only local composition. Reuses Common module/profile gates and
/// the established Learning physical owner lock function, not LearningSetup operation grants.
/// Production CV-purpose/session/standing adapters and transaction-bound M2 callbacks remain
/// pending. Synthetic non-network Common authorities are used only in the isolated test host.</summary>
internal sealed class IsolatedLearningCvRepository : ILearningCvRepository
{
    private readonly DbContextOptions<LearningDbContext> options;
    private readonly ICurrentUser user;
    private readonly IPrivateModuleAccess access;
    private readonly IPrivateProfileService profiles;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    private sealed record Owner(Guid Subject, Guid Profile);

    internal IsolatedLearningCvRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles)
    {
        this.options = options; this.user = user; this.access = access; this.profiles = profiles;
        using var db = Open(); var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Pooling || settings.Database is not { } name
            || !name.StartsWith("andril_m2_test_", StringComparison.Ordinal) || !Guid.TryParseExact(name["andril_m2_test_".Length..], "N", out _))
            throw new InvalidOperationException("CV integration requires an isolated nonpooled local test database.");
    }
    private LearningDbContext Open() => new(options);
    private async Task<Owner> Resolve(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!user.IsAuthenticated || user.UserId is not { } subject || subject == Guid.Empty) throw new ProfileAccessDeniedException();
        await access.RequireAsync("learning", ct);
        var p = await profiles.WithAuthorizedProfileAsync("learning", p => Task.FromResult(p), ct);
        if (p.ProfileId == Guid.Empty || p.Module != "learning") throw new ProfileAccessDeniedException();
        return new(subject, p.ProfileId);
    }
    private async Task Revalidate(Owner expected, CancellationToken ct)
    { if (await Resolve(ct) != expected) throw new ProfileAccessDeniedException(); ct.ThrowIfCancellationRequested(); }

    private async Task<T> Within<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<CvOperationState, T> failure, CancellationToken ct, bool write = false)
    {
        try
        {
            var owner = await Resolve(ct); await using var db = Open(); await db.Database.OpenConnectionAsync(ct);
            // Exact M2 subject key before the snapshot; nonpooled disposal releases the fence.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({owner.Subject.ToString()},0))", ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{owner.Subject.ToString()},true),set_config('andril.module','learning',true)", ct);
            if (!await db.Database.SqlQuery<bool>($"SELECT andril_learning.lock_cv_owner({owner.Subject},{owner.Profile}) AS \"Value\"").SingleAsync(ct)) throw new ProfileAccessDeniedException();
            await Revalidate(owner, ct); var result = await action(db, owner, ct);
            if (TestCheckpoint is not null) await TestCheckpoint(write ? "before-commit" : "read-release", ct);
            await Revalidate(owner, ct); ct.ThrowIfCancellationRequested();
            try { await tx.CommitAsync(ct); }
            catch (Exception e) when (!Conflict(e) && e is System.Data.Common.DbException or IOException or TimeoutException or OperationCanceledException)
            { return failure(write ? CvOperationState.CommitUnknown : CvOperationState.Unavailable); }
            return result;
        }
        catch (Exception e) when (e is ProfileAccessDeniedException or ProfileMissingException) { return failure(CvOperationState.Denied); }
        catch (Exception e) when (Conflict(e)) { return failure(write ? CvOperationState.Conflict : CvOperationState.Unavailable); }
        catch (Exception e) when (e is ProfileContractPendingException or PhotoDependencyException or NpgsqlException or IOException or TimeoutException)
        { return failure(CvOperationState.Unavailable); }
    }

    public Task<CvReadResult> CreateAsync(CvTemplate template, CancellationToken cancellationToken = default) => Within(async (db, owner, ct) =>
    {
        var cv = new LearningCv(Guid.NewGuid(), owner.Profile, template);
        db.Add(LearningCvRow.From(cv)); await db.SaveChangesAsync(ct); await Prepared(ct);
        return CvReadResult.Available(cv);
    }, CvReadResult.Failure, cancellationToken, true);

    public Task<CvReadResult> OpenAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            var row = await Rows(db, owner).AsNoTracking().SingleOrDefaultAsync(c => c.CvId == cvId, ct);
            if (row is null) return CvReadResult.Failure(CvOperationState.NotFound);
            return Decode(row);
        }, CvReadResult.Failure, cancellationToken);
    }
    public Task<CvListResult> ListAsync(CancellationToken cancellationToken = default) => Within(async (db, owner, ct) =>
        CvListResult.Available(await Rows(db, owner).AsNoTracking().OrderBy(c => c.CvId).Select(c => c.CvId).ToArrayAsync(ct)), CvListResult.Failure, cancellationToken);

    public Task<CvReadResult> SaveAsync(Guid cvId, CvSaveChange change, CancellationToken cancellationToken = default)
    {
        RequireId(cvId); ArgumentNullException.ThrowIfNull(change);
        return Within(async (db, owner, ct) =>
        {
            var row = await db.Set<LearningCvRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_cvs WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND cv_id={cvId} FOR UPDATE").SingleOrDefaultAsync(ct);
            if (row is null) return CvReadResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row); if (decoded.Cv is null) return decoded;
            change.Apply(decoded.Cv); row.ReplaceContent(LearningCvRow.From(decoded.Cv));
            await db.SaveChangesAsync(ct); await Prepared(ct);
            return CvReadResult.Available(decoded.Cv);
        }, CvReadResult.Failure, cancellationToken, true);
    }

    public Task<CvPreviewResult> PreviewAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            var row = await Rows(db, owner).AsNoTracking().SingleOrDefaultAsync(c => c.CvId == cvId, ct);
            if (row is null) return CvPreviewResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row);
            // Re-read current persisted choice, never accept a stale caller-held CV as consent.
            // Generic text is unclassified: existing safe preview withholds it even if true.
            return decoded.Cv is null ? CvPreviewResult.Failure(decoded.State) : CvPreviewResult.Available(CvPreview.CreateOffline(decoded.Cv));
        }, CvPreviewResult.Failure, cancellationToken);
    }
    public Task<CvPdfResult> ExportPdfAsync(Guid cvId, CancellationToken cancellationToken = default)
    {
        RequireId(cvId);
        return Within(async (db, owner, ct) =>
        {
            // Keep persisted document/consent stable until the coordinated release. All normal
            // saves also use the existing subject fence; no new production lock namespace.
            var row = await db.Set<LearningCvRow>().FromSqlInterpolated($"SELECT * FROM andril_learning.learning_cvs WHERE owner_learning_profile_id={owner.Profile} AND module='learning' AND cv_id={cvId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return CvPdfResult.Failure(CvOperationState.NotFound);
            var decoded = Decode(row);
            return decoded.Cv is null ? CvPdfResult.Failure(decoded.State) : CvPdfResult.Available(CvPdfDocument.CreateOffline(decoded.Cv, ct));
        }, CvPdfResult.Failure, cancellationToken);
    }
    private Task Prepared(CancellationToken ct) => TestCheckpoint?.Invoke("prepared", ct) ?? Task.CompletedTask;
    private static IQueryable<LearningCvRow> Rows(LearningDbContext db, Owner owner) => db.Set<LearningCvRow>().Where(c => c.OwnerLearningProfileId == owner.Profile && c.Module == "learning");
    private static CvReadResult Decode(LearningCvRow row)
    {
        try { return CvReadResult.Available(row.ToDomain()); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return CvReadResult.Failure(CvOperationState.Unavailable); }
    }
    private static void RequireId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A CV reference is required.", nameof(id)); }
    private static bool Conflict(Exception e) => IsolatedLearningPreferencesStore.IsConflict(e)
        || e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_learning_cvs" } };
}
