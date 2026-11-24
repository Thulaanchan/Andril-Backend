using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Persistence.Accounts;

internal sealed record ControlledProfilePermission(
    bool? StandingAllowsAccess, PrivateProfilePurposeEvidence Purpose, DateTimeOffset ValidUntilUtc);

// The implementation must read current standing, session and operation-specific policy.
// The fixture supplies controlled evidence. No production adapter is registered.
internal interface IControlledProfileAuthority
{
    Task<ControlledProfilePermission> ReadAsync(Guid subject, ModuleMembershipScope scope, Guid? profileId,
        PrivateProfileOperation operation, long revision, Guid eligibilityVersion, CancellationToken ct);
}

internal sealed class ControlledModuleProfileOperations(ControlledModuleLifecycleStore store, ICurrentUser user,
    LifecycleProfileAuthorization authorization, IControlledProfileAuthority authority, IPrivateProfilePhotoObjectStorage? objects = null)
{
    internal Task<PrivateProfileDto> CreateAsync(string module, CancellationToken ct = default) =>
        store.WithProfileAsync(user, module, PrivateProfileOperation.Create, authorization, authority, async (db, scope, profile) =>
        {
            if (profile is not null)
                return Project(profile);
            var created = PrivateModuleProfile.Create(scope.AccountId, scope.Module, DateTimeOffset.UtcNow);
            db.PrivateProfiles.Add(created);
            await db.SaveChangesAsync(ct);
            return Project(created);
        }, release: null, ct);

    internal Task<PrivateProfileDto> EditAsync(string module, ProfileEdit edit, CancellationToken ct = default)
    {
        var validated = DisplayNames.Validate(edit);
        return store.WithProfileAsync(user, module, PrivateProfileOperation.Edit, authorization, authority, async (db, _, profile) =>
        {
            profile!.SetDraftFields(validated);
            await db.SaveChangesAsync(ct);
            return Project(profile);
        }, release: null, ct);
    }

    // The supplied release runs under the fence after revalidation. It is a buffered, in-process
    // boundary. This contract does not authorize later streaming or issue signed media URLs.
    internal Task ReadAsync(string module, Func<PrivateProfileDto, CancellationToken, Task> release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        return store.WithProfileAsync(user, module, PrivateProfileOperation.Read, authorization, authority,
            (_, _, profile) => Task.FromResult(Project(profile!)), release, ct);
    }

    internal Task ReservePhotoAsync(string module, Guid requestId, PhotoUploadEvidence evidence,
        Func<Guid, Guid, CancellationToken, Task> acceptReservation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acceptReservation);
        return ReserveAsync();

        async Task ReserveAsync()
        {
            var pending = await store.WithProfileAsync(user, module, PrivateProfileOperation.WriteMedia, authorization, authority,
                (db, _, profile) => new PrivateProfilePhotoStore(db).ReserveUnderProfileLockAsync(new(profile!.Id, module),
                    requestId, evidence.OriginalHash, evidence, ct), release: null, ct);
            if (pending.LeaseToken is Guid token)
                await acceptReservation(pending.Id, token, ct);
        }
    }

    internal Task ReadPhotoMetadataAsync(string module, Guid uploadId,
        Func<PrivatePhotoStatus, CancellationToken, Task> release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        return store.WithProfileAsync(user, module, PrivateProfileOperation.ReadMedia, authorization, authority,
            async (db, _, profile) => await new PrivateProfilePhotoStore(db).ReadAsync(new(profile!.Id, module), uploadId, ct)
                ?? throw new ProfileMissingException(), release, ct);
    }

    internal Task ReadPhotoContentAsync(string module, Func<byte[], CancellationToken, Task> release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        return store.WithProfileAsync(user, module, PrivateProfileOperation.ReadMediaContent, authorization, authority,
            async (db, _, profile) =>
            {
                var expected = await new PrivateProfilePhotoStore(db).ReadCurrentAsync(new(profile!.Id, module), ct)
                    ?? throw new ProfileMissingException();
                return await ReadVerifiedBytesAsync(expected, ct);
            }, release, ct);
    }

    internal Task<PrivatePhotoStatus> FinalizePhotoAsync(string module, Guid uploadId, Guid lease, CancellationToken ct = default) =>
        store.WithProfileAsync(user, module, PrivateProfileOperation.FinalizeMedia, authorization, authority,
            async (db, _, profile) =>
            {
                var scope = new PhotoScope(profile!.Id, module);
                var photos = new PrivateProfilePhotoStore(db);
                var pending = await db.PrivateProfilePhotos.AsNoTracking().SingleOrDefaultAsync(
                    photo => photo.Id == uploadId && photo.ProfileId == scope.ProfileId && photo.Module == scope.Module, ct)
                    ?? throw new ProfileMissingException();
                if (!await photos.OwnsLeaseAsync(scope, uploadId, lease, ct))
                    throw new PhotoUploadConflictException();
                await ReadVerifiedBytesAsync(pending.Evidence(), ct);
                return await photos.FinalizeAsync(scope, uploadId, lease, ct);
            }, release: null, ct);

    private async Task<byte[]> ReadVerifiedBytesAsync(Andril.Common.DTOs.StoredProfilePhoto expected, CancellationToken ct)
    {
        if (objects?.IsConfigured != true)
            throw new PhotoDependencyException();
        var read = await objects.ReadAsync(expected, ct);
        if (read.Outcome != PhotoObjectOutcome.Success || !PhotoHashes.Matches(expected, read.Bytes))
            throw new PhotoDependencyException();
        return read.Bytes!;
    }

    private static PrivateProfileDto Project(PrivateModuleProfile profile) =>
        new(profile.Id, profile.Module, profile.CreatedAtUtc)
        { DisplayName = profile.DisplayName, Bio = profile.Bio, Visibility = profile.Visibility };
}

internal sealed partial class ControlledModuleLifecycleStore
{
    internal async Task<T> WithProfileAsync<T>(ICurrentUser user, string module, PrivateProfileOperation operation,
        LifecycleProfileAuthorization authorization, IControlledProfileAuthority purposeAuthority,
        Func<AccountsDbContext, ModuleMembershipScope, PrivateModuleProfile?, Task<T>> access,
        Func<T, CancellationToken, Task>? release, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty || !ModuleNames.IsValid(module))
            throw new ProfileAccessDeniedException();

        await using var db = Open();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await SetSubject(db, subject, module, ct);
            await LockSubjectAsync(db, subject, ct);

            var (eligibility, membership) = await ReadMembershipAsync(db, subject, module, ct);
            var scope = eligibility.Scope;
            // This projection resolves only the server-owned identity. No private fields or media
            // are loaded before lifecycle and purpose authorization.
            var profileId = await db.PrivateProfiles.Where(x => x.AccountId == scope.AccountId && x.Module == module)
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
            await ValidateAsync(eligibility, membership, profileId);

            var profile = profileId is null ? null
                : await db.PrivateProfiles.FromSqlInterpolated($"""
                    SELECT * FROM andril_identity.private_module_profiles
                    WHERE id = {profileId.Value} AND account_id = {scope.AccountId} AND module = {module}
                    FOR UPDATE
                    """).SingleOrDefaultAsync(ct);
            if (profileId != profile?.Id)
                throw new ProfileAccessDeniedException();

            var result = await access(db, scope, profile);
            var (fresh, freshMembership) = await ReadMembershipAsync(db, subject, module, ct);
            if (fresh.Scope != scope || freshMembership?.Membership.Revision != membership?.Membership.Revision)
                throw new ProfileAccessDeniedException();
            await ValidateAsync(fresh, freshMembership, profileId);
            ct.ThrowIfCancellationRequested();
            if (release is not null)
                await release(result, ct);
            await tx.CommitAsync(ct);
            return result;

            async Task ValidateAsync(ModuleActivationEvidence evidence, StoredModuleMembership? row, Guid? ownedId)
            {
                if (evidence.SourceOutcome == ModuleAuthorizationOutcome.Unavailable)
                    throw new AccountStoreUnavailableException();
                if (row?.Membership is not ActiveModuleMembership active)
                    throw new ProfileAccessDeniedException();
                var permission = await purposeAuthority.ReadAsync(subject, scope, ownedId, operation,
                    active.Revision, evidence.CurrentEvidenceVersion, ct);
                ct.ThrowIfCancellationRequested();
                var outcome = authorization.DecideOwnedProfile(scope, operation, ownedId,
                    new(evidence, active, permission.StandingAllowsAccess, permission.Purpose, permission.ValidUntilUtc));
                if (outcome == ModuleAuthorizationOutcome.Unavailable)
                    throw new AccountStoreUnavailableException();
                if (outcome != ModuleAuthorizationOutcome.Satisfied)
                    throw new ProfileAccessDeniedException();
            }
        }
        catch (ModuleLifecycleException error) when (error.Outcome == ModuleAuthorizationOutcome.Denied)
        {
            throw new ProfileAccessDeniedException();
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "uq_profiles_module_name" or "uq_profiles_owner_name" })
        {
            throw new ProfileNameConflictException();
        }
        catch (Exception error) when (error is DbException or DbUpdateException or TimeoutException or IOException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
