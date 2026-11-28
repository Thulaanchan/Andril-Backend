using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Economy.Premium;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Economy;

public sealed class PostgresEntitlementRepository(EconomyDbContext db) : IEntitlementRepository
{
    public async Task<EntitlementRecord?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(accountId));

        try
        {
            return await db.PremiumEntitlements.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<EntitlementRecord> UpsertAsync(EntitlementRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_economy.premium_entitlements (
                    account_id, tier, is_active, expires_at_utc,
                    provider, external_subscription_id, created_at_utc, updated_at_utc
                )
                VALUES (
                    {record.AccountId}, {(int)record.Tier}, {record.IsActive}, {record.ExpiresAtUtc},
                    {record.Provider}, {record.ExternalSubscriptionId}, {record.CreatedAtUtc}, {record.UpdatedAtUtc}
                )
                ON CONFLICT (account_id) DO UPDATE SET
                    tier = EXCLUDED.tier,
                    is_active = EXCLUDED.is_active,
                    expires_at_utc = EXCLUDED.expires_at_utc,
                    provider = EXCLUDED.provider,
                    external_subscription_id = EXCLUDED.external_subscription_id,
                    updated_at_utc = GREATEST(premium_entitlements.updated_at_utc, EXCLUDED.updated_at_utc)
                """, cancellationToken);

            var result = await db.PremiumEntitlements.AsNoTracking().SingleAsync(x => x.AccountId == record.AccountId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
