using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class ProfileLocationRepository(AccountsDbContext db) : IProfileLocationRepository
{
    public async Task<ProfileLocationRecord?> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Set<ProfileLocationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProfileId == profileId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<ProfileLocationRecord?> GetByAccountAndModuleAsync(Guid accountId, string module, CancellationToken cancellationToken = default)
    {
        try
        {
            var normModule = module.Trim().ToLowerInvariant();
            return await db.Set<ProfileLocationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.Module == normModule, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<ProfileLocationRecord> UpsertAsync(ProfileLocationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            // A profile submission owns the surrounding transaction; standalone writes own theirs.
            await using var tx = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken) : null;

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_profile_locations (
                    profile_id, account_id, module, city, region, created_at_utc, updated_at_utc
                )
                VALUES (
                    {record.ProfileId}, {record.AccountId}, {record.Module},
                    {record.City}, {record.Region}, {record.CreatedAtUtc}, {record.UpdatedAtUtc}
                )
                ON CONFLICT (profile_id) DO UPDATE SET
                    city = EXCLUDED.city,
                    region = EXCLUDED.region,
                    updated_at_utc = GREATEST(account_profile_locations.updated_at_utc, EXCLUDED.updated_at_utc)
                WHERE account_profile_locations.account_id = EXCLUDED.account_id
                    AND account_profile_locations.module = EXCLUDED.module
                """, cancellationToken);

            var result = await db.Set<ProfileLocationRecord>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProfileId == record.ProfileId && x.AccountId == record.AccountId
                    && x.Module == record.Module, cancellationToken) ?? throw new ProfileAccessDeniedException();

            if (tx is not null) await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<bool> DeleteByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM andril_identity.account_profile_locations WHERE profile_id = {profileId}
                """, cancellationToken);
            return count > 0;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
