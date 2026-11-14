using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class AccountRepository(AccountsDbContext db) : IAccountRepository
{
    public async Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
    {
        ValidateSubject(subject);
        try
        {
            return await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.SupabaseUserId == subject, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateSubject(subject);
        var candidate = Account.Create(subject, now);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // Unique constraint serializes competing inserts. DO NOTHING leaves the winner's
            // ID/timestamps unchanged. A separate SELECT gets a fresh READ COMMITTED snapshot.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.accounts (id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({candidate.Id}, {subject}, {candidate.CreatedAtUtc}, {candidate.UpdatedAtUtc})
                ON CONFLICT (supabase_user_id) DO NOTHING
                """, cancellationToken);
            var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.SupabaseUserId == subject, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return account;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
        // Cancellation or any failure before commit disposes/rolls back the transaction.
        // If a commit succeeds but its response is lost, retry resolves to the same account.
    }

    private static void ValidateSubject(Guid subject)
    {
        if (subject == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(subject));
    }
}
