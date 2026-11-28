using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Common.Accounts.Privacy;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AccountSettingsServicePersistenceTests
{
    [LocalPostgresFact]
    public async Task CurrentAccountSettingsPersistWithoutChangingOtherAccountsOrModuleIdentities()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var (owner, other) = await Seed(db);
        foreach (var module in new[] { "learning", "hobbies", "social" })
        {
            var profile = PrivateModuleProfile.Create(owner.Id, module, DateTimeOffset.UtcNow);
            profile.SetDraftFields(new(module + "Owner", "unchanged", "private"));
            db.PrivateProfiles.Add(profile);
        }
        await db.SaveChangesAsync();
        var before = await db.PrivateProfiles.AsNoTracking().OrderBy(x => x.Module)
            .Select(x => new { x.Id, x.AccountId, x.Module, x.DisplayName, x.Bio }).ToArrayAsync();
        var language = Language(db, owner);
        Assert.Equal(new("en", false), await language.GetAsync());
        Assert.Empty(await db.LanguagePreferences.ToListAsync());
        await new LanguagePreferenceRepository(db).WriteAsync(other.Id, "si", false, DateTimeOffset.UtcNow);
        var otherPrivacy = UserPrivacySettings.CreateDefault(other.Id, DateTimeOffset.UtcNow);
        await new UserPrivacySettingsRepository(db).UpsertAsync(otherPrivacy);
        Assert.Equal(new("ta", true), await language.UpdateAsync("ta", "settings"));
        Assert.Equal(new("ta", true), await language.UpdateAsync("en", "sync"));
        var privacy = Privacy(db, owner);
        var updated = await privacy.UpdateMyPrivacySettingsAsync(new("private", true, "disabled", false, false));
        Assert.Equal(owner.Id, updated.AccountId);
        Assert.Equal("private", updated.ProfileDiscoverability);
        await using var fresh = database.Open();
        Assert.Equal(new("ta", true), await Language(fresh, owner).GetAsync());
        Assert.Equal("si", (await new LanguagePreferenceRepository(fresh).FindAsync(other.Id))!.Language);
        Assert.Equal("public", (await new UserPrivacySettingsRepository(fresh).GetByAccountIdAsync(other.Id))!.ProfileDiscoverability);
        var after = await fresh.PrivateProfiles.AsNoTracking().OrderBy(x => x.Module)
            .Select(x => new { x.Id, x.AccountId, x.Module, x.DisplayName, x.Bio }).ToArrayAsync();
        Assert.Equal(before, after);
        Assert.Equal(owner.UpdatedAtUtc, (await fresh.Accounts.AsNoTracking().SingleAsync(x => x.Id == owner.Id)).UpdatedAtUtc);
    }

    [LocalPostgresFact]
    public async Task CancellationAfterLanguageWriteBeforeResponseRollsBackPreviousSettings()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        Account owner;
        await using (var db = database.Open())
        {
            (owner, _) = await Seed(db);
            await new LanguagePreferenceRepository(db).WriteAsync(owner.Id, "ta", false, DateTimeOffset.UtcNow);
        }
        using var cancellation = new CancellationTokenSource();
        await using (var failing = database.Open(new FailSettingsRead("account_language_preferences", 1, cancellation)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Language(failing, owner).UpdateAsync("si", "settings", cancellation.Token));
        }
        await using var fresh = database.Open();
        Assert.Equal("ta", (await new LanguagePreferenceRepository(fresh).FindAsync(owner.Id))!.Language);
        Assert.Single(await fresh.LanguagePreferences.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task PrivacyResponseReadFailureRollsBackAttemptedReplacement()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        Account owner;
        await using (var db = database.Open())
        {
            (owner, _) = await Seed(db);
            var original = UserPrivacySettings.CreateDefault(owner.Id, DateTimeOffset.UtcNow);
            original.Update("private", true, "disabled", false, false, DateTimeOffset.UtcNow);
            await new UserPrivacySettingsRepository(db).UpsertAsync(original);
        }
        await using (var failing = database.Open(new FailSettingsRead("account_privacy_settings", 2)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Privacy(failing, owner).UpdateMyPrivacySettingsAsync(new("public", false, "everyone", true, true)));
        }
        await using var fresh = database.Open();
        var preserved = (await new UserPrivacySettingsRepository(fresh).GetByAccountIdAsync(owner.Id))!;
        Assert.Equal("private", preserved.ProfileDiscoverability);
        Assert.True(preserved.GhostModeEnabled);
        Assert.Equal("disabled", preserved.DirectMessagingPolicy);
        Assert.False(preserved.AllowFriendRequests);
        Assert.False(preserved.ShowActiveModule);
    }

    private static LanguagePreferenceService Language(AccountsDbContext db, Account owner)
    {
        var user = new User(owner.SupabaseUserId);
        return new(user, new AccountService(user, new AccountRepository(db), TimeProvider.System), new LanguagePreferenceRepository(db), TimeProvider.System);
    }
    private static UserPrivacySettingsService Privacy(AccountsDbContext db, Account owner)
    {
        var user = new User(owner.SupabaseUserId);
        return new(user, new AccountService(user, new AccountRepository(db), TimeProvider.System), new UserPrivacySettingsRepository(db), TimeProvider.System);
    }
    private static async Task<(Account Owner, Account Other)> Seed(AccountsDbContext db)
    {
        var accounts = new AccountRepository(db);
        return (await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow), await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
    private sealed record User(Guid? UserId) : ICurrentUser { public bool IsAuthenticated => true; }
    private sealed class FailSettingsRead(string table, int failOnRead, CancellationTokenSource? cancellation = null) : DbCommandInterceptor
    {
        private int reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(table, StringComparison.Ordinal) && ++reads == failOnRead)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("Injected settings response-read failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
