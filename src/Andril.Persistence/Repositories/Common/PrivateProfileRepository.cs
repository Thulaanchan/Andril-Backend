using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Common;

public sealed class PrivateProfileRepository(AccountsDbContext db) : IPrivateProfileRepository
{
    public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate,
        CancellationToken cancellationToken = default) => Execute(accountId, module, validate, null, cancellationToken);

    public async Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate,
        TimeProvider clock, CancellationToken cancellationToken = default) =>
        await Execute(accountId, module, validate, clock, cancellationToken) ?? throw new AccountStoreUnavailableException();

    public async Task<PrivateModuleProfile> UpdateAsync(Guid accountId, string module, ProfileEdit edit,
        Action<StarterModuleState> validate, CancellationToken cancellationToken = default) =>
        await Execute(accountId, module, validate, TimeProvider.System, cancellationToken, DisplayNames.Validate(edit)) ?? throw new ProfileMissingException();

    public async Task<T> WithAuthorizedProfileAsync<T>(Guid accountId, string module, Action<StarterModuleState> validate,
        Func<PrivateModuleProfile, Task<T>> action, CancellationToken cancellationToken)
    {
        T result = default!;
        await Execute(accountId, module, validate, null, cancellationToken,
            authorizedAction: async profile => { result = await action(profile); });
        return result;
    }

    private async Task<PrivateModuleProfile?> Execute(Guid accountId, string module, Action<StarterModuleState> validate,
        TimeProvider? clock, CancellationToken ct, ProfileEdit? edit = null, Func<PrivateModuleProfile, Task>? authorizedAction = null)
    {
        if (accountId == Guid.Empty || !StarterModules.IsValid(module)) throw new ProfileValidationException();
        try
        {
            bool lockAccount = clock is not null || authorizedAction is not null;
            await using var tx = await db.Database.BeginTransactionAsync(
                lockAccount ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead, ct);
            if (lockAccount)
            {
                // Serialize account writers while allowing canonical entitlement foreign-key checks.
                var account = await db.Accounts.FromSqlInterpolated($"SELECT * FROM andril_identity.accounts WHERE id = {accountId} FOR NO KEY UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
                if (account is null) throw new StarterModuleAccountMissingException();
            }
            var demographics = !lockAccount
                ? await db.Demographics.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, ct)
                : await db.Demographics.FromSqlInterpolated($"SELECT * FROM andril_identity.account_demographics WHERE account_id = {accountId} FOR UPDATE")
                    .AsNoTracking().SingleOrDefaultAsync(ct);
            var membership = await db.StarterMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
            // Enforce membership here too, independently of the service callback.
            if (membership?.Module != module) throw new ProfileAccessDeniedException();
            var policies = await db.PolicyRecords.AsNoTracking().Where(x => x.AccountId == accountId).ToArrayAsync(ct);
            validate(new(demographics, policies, membership));
            // Keep profile edits serialized while allowing foreign-key checks from a second profile's transaction.
            var profile = authorizedAction is null
                ? await db.PrivateProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId && x.Module == module, ct)
                : await db.PrivateProfiles.FromSqlInterpolated($"""
                    SELECT * FROM andril_identity.private_module_profiles
                    WHERE account_id = {accountId} AND module = {module} FOR NO KEY UPDATE
                    """).AsNoTracking().SingleOrDefaultAsync(ct);
            if (authorizedAction is not null)
                await authorizedAction(profile ?? throw new ProfileMissingException());
            if (edit is not null)
            {
                if (profile is null) throw new ProfileMissingException();
                var canonical = DisplayNames.Canonical(edit.DisplayName);
                if (await db.PrivateProfiles.AnyAsync(x => x.Id != profile.Id && x.CanonicalDisplayName == canonical
                    && (x.AccountId == accountId || x.Module == module), ct)) throw new ProfileNameConflictException();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE andril_identity.private_module_profiles SET display_name = {edit.DisplayName}, bio = {edit.Bio}, visibility = {edit.Visibility}
                    WHERE id = {profile.Id} AND account_id = {accountId} AND module = {module}
                    """, ct);
                profile = await db.PrivateProfiles.AsNoTracking().SingleAsync(x => x.Id == profile.Id, ct);
            }
            if (profile is null && clock is not null && edit is null)
            {
                var candidate = PrivateModuleProfile.Create(accountId, module, clock.GetUtcNow());
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.private_module_profiles (id, account_id, module, created_at_utc)
                    VALUES ({candidate.Id}, {accountId}, {module}, {candidate.CreatedAtUtc})
                    ON CONFLICT (account_id, module) DO NOTHING
                    """, ct);
                profile = await db.PrivateProfiles.AsNoTracking().SingleAsync(x => x.AccountId == accountId && x.Module == module, ct);
            }
            await tx.CommitAsync(ct);
            return profile;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName is "uq_profiles_module_name" or "uq_profiles_owner_name") { throw new ProfileNameConflictException(); }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }
}
