using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class LanguagePreferenceRepository(AccountsDbContext db) : ILanguagePreferenceRepository
{
    public async Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An account is required.", nameof(accountId));
        try
        {
            return await db.LanguagePreferences.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }

    public async Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var candidate = AccountLanguagePreference.Create(accountId, language, now);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (initializeOnly)
            {
                // First authenticated device sync never replaces an existing account choice.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.account_language_preferences (account_id, language, created_at_utc, updated_at_utc)
                    VALUES ({accountId}, {language}, {candidate.CreatedAtUtc}, {candidate.UpdatedAtUtc})
                    ON CONFLICT (account_id) DO NOTHING
                    """, cancellationToken);
            }
            else
            {
                // An explicit Settings change can replace the choice. Equal writes are no-ops.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_identity.account_language_preferences AS existing (account_id, language, created_at_utc, updated_at_utc)
                    VALUES ({accountId}, {language}, {candidate.CreatedAtUtc}, {candidate.UpdatedAtUtc})
                    ON CONFLICT (account_id) DO UPDATE SET
                        language = EXCLUDED.language,
                        updated_at_utc = GREATEST(existing.updated_at_utc, EXCLUDED.updated_at_utc)
                    WHERE existing.language IS DISTINCT FROM EXCLUDED.language
                    """, cancellationToken);
            }
            var preference = await db.LanguagePreferences.AsNoTracking().SingleAsync(x => x.AccountId == accountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return preference;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }
}
