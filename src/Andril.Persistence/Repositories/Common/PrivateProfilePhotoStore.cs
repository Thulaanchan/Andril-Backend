using System.Data;
using System.Data.Common;
using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Entities;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class PrivateProfilePhotoStore(AccountsDbContext db) : IPrivateProfilePhotoStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    // Five seconds of cooperative inline work plus sixty seconds before retrying an abandoned claim.
    private static readonly TimeSpan CleanupClaimDuration = TimeSpan.FromSeconds(
        PhotoCleanupSettings.ApprovedBudgetSeconds + PhotoCleanupSettings.ApprovedRetryDelaySeconds);

    public Task<PrivateProfilePhoto?> FindRequestAsync(PhotoScope scope, Guid requestId, CancellationToken ct) =>
        SafeAsync(() => Scoped(scope).AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct));

    public Task<PrivateProfilePhoto> ReserveAsync(PhotoScope scope, Guid requestId, string originalHash,
        PhotoUploadEvidence? evidence, CancellationToken ct) => InScopeTransactionAsync(scope, async () =>
    {
        var now = await DatabaseUtcNowAsync(ct);
        var existing = await Scoped(scope).SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (existing is not null)
        {
            if (existing.OriginalContentHash != originalHash) throw new PhotoUploadConflictException();
            if (existing.State == PhotoState.PendingStorage)
            {
                existing.Reclaim(Guid.NewGuid(), now, now + LeaseDuration);
                await db.SaveChangesAsync(ct);
            }
            return existing;
        }

        if (evidence is null || evidence.OriginalHash != originalHash) throw new PhotoUploadConflictException();
        var prior = await Scoped(scope).SingleOrDefaultAsync(x => x.State == PhotoState.PendingStorage, ct);
        if (prior is not null)
        {
            prior.Abandon(now);
            await db.SaveChangesAsync(ct);
        }

        Guid id;
        do { id = Guid.NewGuid(); } while (id == requestId);
        var candidate = PrivateProfilePhoto.Create(scope, id, requestId, evidence, Guid.NewGuid(), now, now + LeaseDuration);
        db.PrivateProfilePhotos.Add(candidate);
        await db.SaveChangesAsync(ct);
        return candidate;
    }, ct);

    public Task<bool> OwnsLeaseAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) => SafeAsync(async () =>
    {
        var now = await DatabaseUtcNowAsync(ct);
        return await Scoped(scope).AnyAsync(x => x.Id == id && x.State == PhotoState.PendingStorage
            && x.LeaseToken == token && x.LeaseUntilUtc > now, ct);
    });

    public Task<PrivatePhotoStatus> FinalizeAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) => SafeAsync(async () =>
    {
        if (db.Database.CurrentTransaction is null) throw new PhotoDependencyException();
        // The caller has already locked and reauthorized this exact profile in the same transaction.
        var now = await DatabaseUtcNowAsync(ct);
        var pending = await RequiredAsync(scope, id, ct);
        if (pending.State == PhotoState.Stored) return pending.Status();
        if (!pending.OwnsLease(token, now)) throw new PhotoUploadConflictException();

        var current = await Scoped(scope).SingleOrDefaultAsync(x => x.IsCurrent, ct);
        if (current is not null)
        {
            current.MarkSuperseded(now);
            await db.SaveChangesAsync(ct);
        }
        pending.MarkStored(token, now);
        await db.SaveChangesAsync(ct);
        return pending.Status();
    });

    public Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct) =>
        InScopeTransactionAsync(scope, async () =>
        {
            var photo = await RequiredAsync(scope, id, ct);
            photo.MarkFailed(token, reason, await DatabaseUtcNowAsync(ct));
            await db.SaveChangesAsync(ct);
            return photo.Status();
        }, ct);

    public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct) => SafeAsync(async () =>
        (await Scoped(scope).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct))?.Status());

    public Task<StoredProfilePhoto?> ReadCurrentAsync(PhotoScope scope, CancellationToken ct) => SafeAsync(async () =>
        (await Scoped(scope).AsNoTracking().SingleOrDefaultAsync(x => x.IsCurrent && x.State == PhotoState.Stored, ct))?.Evidence());

    public Task<PhotoCleanupClaim?> ClaimCleanupAsync(PhotoScope scope, CancellationToken ct) =>
        InScopeTransactionAsync<PhotoCleanupClaim?>(scope, async () =>
        {
            var now = await DatabaseUtcNowAsync(ct);
            var id = await Scoped(scope).AsNoTracking()
                .Where(x => x.CleanupPending && !x.IsCurrent &&
                    (x.State == PhotoState.Failed || (x.State == PhotoState.Stored && x.SupersededAtUtc != null)) &&
                    x.NextCleanupAttemptAtUtc <= now &&
                    (x.CleanupClaimExpiresAtUtc == null || x.CleanupClaimExpiresAtUtc <= now))
                .OrderBy(x => x.NextCleanupAttemptAtUtc).ThenBy(x => x.Id)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (!id.HasValue) return null;

            var candidate = await RequiredAsync(scope, id.Value, ct);
            var claim = candidate.ClaimCleanup(Guid.NewGuid(), now, now + CleanupClaimDuration);
            await db.SaveChangesAsync(ct);
            return claim;
        }, ct);

    public Task<bool> RecordCleanupAsync(PhotoCleanupClaim claim, bool removed, TimeSpan retryDelay, CancellationToken ct) =>
        InScopeTransactionAsync(claim.Address.Scope, async () =>
        {
            var photo = await RequiredAsync(claim.Address.Scope, claim.Address.UploadId, ct);
            if (!photo.RecordCleanupAttempt(claim.Token, removed, retryDelay, await DatabaseUtcNowAsync(ct))) return false;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    private IQueryable<PrivateProfilePhoto> Scoped(PhotoScope scope)
    {
        if (!new PhotoObjectAddress(scope, scope.ProfileId).IsValid) throw new PhotoUploadInvalidException();
        return db.PrivateProfilePhotos.Where(x => x.ProfileId == scope.ProfileId && x.Module == scope.Module);
    }

    private async Task<PrivateProfilePhoto> RequiredAsync(PhotoScope scope, Guid id, CancellationToken ct)
    {
        // A reservation may still be tracked by this request. Requery the database under the lock.
        var entry = db.ChangeTracker.Entries<PrivateProfilePhoto>().SingleOrDefault(e => e.Entity.Id == id);
        if (entry is not null) entry.State = EntityState.Detached;
        return await Scoped(scope).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new ProfileMissingException();
    }

    private Task<DateTimeOffset> DatabaseUtcNowAsync(CancellationToken ct) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    private Task<T> InScopeTransactionAsync<T>(PhotoScope scope, Func<Task<T>> action, CancellationToken ct) => SafeAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var profile = await db.PrivateProfiles.FromSqlInterpolated($"""
            SELECT * FROM andril_identity.private_module_profiles
            WHERE id = {scope.ProfileId} AND module = {scope.Module} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (profile is null) throw new ProfileMissingException();
        T result = await action();
        await tx.CommitAsync(ct);
        return result;
    });

    private static async Task<T> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DbUpdateConcurrencyException) { throw new PhotoUploadConflictException(); }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException)
        { throw new PhotoDependencyException(); }
    }
}
