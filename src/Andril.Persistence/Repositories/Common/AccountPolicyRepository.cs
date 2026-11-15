using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class AccountPolicyRepository(AccountsDbContext db) : IAccountPolicyRepository
{
    public async Task<IReadOnlyList<AccountPolicyRecord>> ReadAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        try
        {
            return await db.PolicyRecords.AsNoTracking().Where(x => x.AccountId == accountId)
                .OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Action).ThenBy(x => x.Version).ToArrayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }

    public async Task AppendAsync(Guid accountId, IReadOnlyList<PolicyActionSubmission> actions, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Stable ordering prevents competing duplicate requests from taking locks in opposite order.
        var candidates = actions.Select(x => AccountPolicyRecord.Create(accountId, x.Action, x.Version, now))
            .OrderBy(x => x.Action, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            foreach (var record in candidates)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.account_policy_records (account_id, action, version, recorded_at_utc)
                    VALUES ({record.AccountId}, {record.Action}, {record.Version}, {record.RecordedAtUtc})
                    ON CONFLICT (account_id, action, version) DO NOTHING
                    """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }
}
