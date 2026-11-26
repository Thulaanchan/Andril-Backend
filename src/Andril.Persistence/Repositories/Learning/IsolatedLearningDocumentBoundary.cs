using System.Data;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

internal enum LearningDocumentFailure { Denied, Unavailable, Conflict, CommitUnknown }

/// <summary>Unregistered local Learning-document boundary extracted from the existing CV
/// repository. Reuses Common gates and the existing M2 subject fence and physical owner lock.
/// lock_cv_owner has a historical name: it checks Learning profile ownership/lifecycle, never
/// a document or operation grant. Production purpose/session/standing and coordinated M2
/// callbacks remain pending; this component cannot use hosted or pooled connections.</summary>
internal sealed class IsolatedLearningDocumentBoundary
{
    internal sealed record Owner(Guid Subject, Guid Profile);
    private readonly DbContextOptions<LearningDbContext> options;
    private readonly ICurrentUser user;
    private readonly IPrivateModuleAccess access;
    private readonly IPrivateProfileService profiles;

    internal IsolatedLearningDocumentBoundary(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles)
    {
        this.options = options; this.user = user; this.access = access; this.profiles = profiles;
        using var db = new LearningDbContext(options);
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Pooling || settings.Database is not { } name
            || !name.StartsWith("andril_m2_test_", StringComparison.Ordinal) || !Guid.TryParseExact(name["andril_m2_test_".Length..], "N", out _))
            throw new InvalidOperationException("Learning document integration requires an isolated nonpooled local test database.");
    }

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

    internal async Task<T> Run<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<LearningDocumentFailure, T> failure, CancellationToken ct, bool write = false,
        Func<string, CancellationToken, Task>? checkpoint = null, Func<Exception, bool>? additionalConflict = null)
    {
        bool Conflict(Exception e) => IsolatedLearningPreferencesStore.IsConflict(e) || (additionalConflict?.Invoke(e) ?? false);
        try
        {
            var owner = await Resolve(ct); await using var db = new LearningDbContext(options); await db.Database.OpenConnectionAsync(ct);
            // Existing M2 subject key before snapshot; nonpooled disposal releases the fence.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock(hashtextextended({owner.Subject.ToString()},0))", ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan=off; SET LOCAL enable_bitmapscan=off", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{owner.Subject.ToString()},true),set_config('andril.module','learning',true)", ct);
            // Function delegates Account -> Membership -> Profile locks to lock_learning_owners.
            if (!await db.Database.SqlQuery<bool>($"SELECT andril_learning.lock_cv_owner({owner.Subject},{owner.Profile}) AS \"Value\"").SingleAsync(ct)) throw new ProfileAccessDeniedException();
            await Revalidate(owner, ct); var result = await action(db, owner, ct);
            if (checkpoint is not null) await checkpoint(write ? "before-commit" : "read-release", ct);
            await Revalidate(owner, ct); ct.ThrowIfCancellationRequested();
            try { await tx.CommitAsync(ct); }
            catch (Exception e) when (!Conflict(e) && e is System.Data.Common.DbException or IOException or TimeoutException or OperationCanceledException)
            { return failure(write ? LearningDocumentFailure.CommitUnknown : LearningDocumentFailure.Unavailable); }
            return result;
        }
        catch (Exception e) when (e is ProfileAccessDeniedException or ProfileMissingException) { return failure(LearningDocumentFailure.Denied); }
        catch (Exception e) when (Conflict(e)) { return failure(write ? LearningDocumentFailure.Conflict : LearningDocumentFailure.Unavailable); }
        catch (Exception e) when (e is ProfileContractPendingException or PhotoDependencyException or NpgsqlException or IOException or TimeoutException)
        { return failure(LearningDocumentFailure.Unavailable); }
    }
}
