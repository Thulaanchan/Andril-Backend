using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class StarterModuleRepository(AccountsDbContext db) : IStarterModuleRepository
{
    public async Task<StarterModuleState> ReadAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        RequireAccountId(accountId);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var result = await ReadState(accountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }

    public async Task<StarterModuleState> SelectAsync(Guid accountId, Func<StarterModuleState, StarterMembership> select,
        CancellationToken cancellationToken = default)
    {
        RequireAccountId(accountId);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // Serialize selections before reading membership, including competing choices.
            var account = await db.Accounts.FromSqlInterpolated($"""
                SELECT * FROM andril_identity.accounts WHERE id = {accountId} FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (account is null) throw new StarterModuleAccountMissingException();
            // Existing Feature 04 writes acquire the same demographic row's update lock.
            // This prevents a DOB correction racing the prerequisite check and insert.
            var demographics = await db.Demographics.FromSqlInterpolated($"""
                SELECT * FROM andril_identity.account_demographics WHERE account_id = {accountId} FOR UPDATE
                """).AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var membership = await db.StarterMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
            var policies = await db.PolicyRecords.AsNoTracking().Where(x => x.AccountId == accountId).ToArrayAsync(cancellationToken);
            var state = new StarterModuleState(demographics, policies, membership);
            var candidate = select(state);
            if (candidate.AccountId != accountId) throw new StarterModuleValidationException();
            if (membership is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.account_starter_memberships
                        (account_id, module, activated_at_utc, social_opt_in_at_utc)
                    VALUES ({accountId}, {candidate.Module}, {candidate.ActivatedAtUtc}, {candidate.SocialOptInAtUtc})
                    ON CONFLICT (account_id) DO NOTHING
                    """, cancellationToken);
                membership = await db.StarterMemberships.AsNoTracking().SingleAsync(x => x.AccountId == accountId, cancellationToken);
            }
            if (membership.Module != candidate.Module) throw new StarterModuleConflictException();
            await transaction.CommitAsync(cancellationToken);
            return state with { Membership = membership };
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }

    private async Task<StarterModuleState> ReadState(Guid accountId, CancellationToken cancellationToken) => new(
        await db.Demographics.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken),
        await db.PolicyRecords.AsNoTracking().Where(x => x.AccountId == accountId).ToArrayAsync(cancellationToken),
        await db.StarterMemberships.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken));

    private static void RequireAccountId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An account is required.", nameof(id));
    }
}
