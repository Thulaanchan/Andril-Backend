using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Privacy;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Common;

public sealed class UserPrivacySettingsRepository(AccountsDbContext db) : IUserPrivacySettingsRepository
{
    public async Task<UserPrivacySettings> GetOrCreateAsync(Guid accountId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var defaults = UserPrivacySettings.CreateDefault(accountId, now);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // A racing GET must never overwrite settings committed by a PUT.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_privacy_settings (
                    account_id, profile_discoverability, ghost_mode_enabled, direct_messaging_policy,
                    allow_friend_requests, show_active_module, created_at_utc, updated_at_utc)
                VALUES ({accountId}, {defaults.ProfileDiscoverability}, {defaults.GhostModeEnabled},
                    {defaults.DirectMessagingPolicy}, {defaults.AllowFriendRequests}, {defaults.ShowActiveModule},
                    {defaults.CreatedAtUtc}, {defaults.UpdatedAtUtc})
                ON CONFLICT (account_id) DO NOTHING
                """, cancellationToken);
            var result = await db.PrivacySettings.AsNoTracking().SingleAsync(x => x.AccountId == accountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException) { throw new AccountStoreUnavailableException(); }
    }
    public async Task<UserPrivacySettings?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("An account is required.", nameof(accountId));

        try
        {
            return await db.PrivacySettings.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }

    public async Task<UserPrivacySettings> UpsertAsync(UserPrivacySettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_privacy_settings (
                    account_id, profile_discoverability, ghost_mode_enabled,
                    direct_messaging_policy, allow_friend_requests, show_active_module,
                    created_at_utc, updated_at_utc
                )
                VALUES (
                    {settings.AccountId}, {settings.ProfileDiscoverability}, {settings.GhostModeEnabled},
                    {settings.DirectMessagingPolicy}, {settings.AllowFriendRequests}, {settings.ShowActiveModule},
                    {settings.CreatedAtUtc}, {settings.UpdatedAtUtc}
                )
                ON CONFLICT (account_id) DO UPDATE SET
                    profile_discoverability = EXCLUDED.profile_discoverability,
                    ghost_mode_enabled = EXCLUDED.ghost_mode_enabled,
                    direct_messaging_policy = EXCLUDED.direct_messaging_policy,
                    allow_friend_requests = EXCLUDED.allow_friend_requests,
                    show_active_module = EXCLUDED.show_active_module,
                    updated_at_utc = GREATEST(account_privacy_settings.updated_at_utc, EXCLUDED.updated_at_utc)
                """, cancellationToken);

            var result = await db.PrivacySettings.AsNoTracking().SingleAsync(x => x.AccountId == settings.AccountId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            throw new AccountStoreUnavailableException();
        }
    }
}
