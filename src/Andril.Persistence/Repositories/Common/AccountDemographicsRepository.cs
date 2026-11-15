using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class AccountDemographicsRepository(AccountsDbContext db) : IAccountDemographicsRepository
{
    public async Task<AccountDemographics?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        try { return await db.Demographics.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken); }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }

    public async Task<AccountDemographics> SaveAsync(Guid accountId, DateOnly dateOfBirth, string gender, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidate = AccountDemographics.Create(accountId, dateOfBirth, gender, now);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_demographics AS existing
                    (account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                VALUES ({accountId}, {candidate.DateOfBirth}, {candidate.Gender}, {candidate.CreatedAtUtc}, {candidate.UpdatedAtUtc})
                ON CONFLICT (account_id) DO UPDATE SET
                    date_of_birth = EXCLUDED.date_of_birth, gender = EXCLUDED.gender,
                    updated_at_utc = GREATEST(existing.updated_at_utc, EXCLUDED.updated_at_utc)
                WHERE existing.date_of_birth IS DISTINCT FROM EXCLUDED.date_of_birth OR existing.gender IS DISTINCT FROM EXCLUDED.gender
                """, cancellationToken);
            var saved = await db.Demographics.AsNoTracking().SingleAsync(x => x.AccountId == accountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }
}
