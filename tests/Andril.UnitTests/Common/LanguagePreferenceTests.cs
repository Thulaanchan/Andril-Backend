using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class LanguagePreferenceTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ta")]
    [InlineData("si")]
    public async Task SupportedChoices_ArePersisted(string language)
    {
        var store = new Store();
        var service = Service(store, store.Subject);
        Assert.Equal(new(language, true), await service.UpdateAsync(language, "sync"));
        Assert.Equal(new(language, true), await service.GetAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("EN")]
    [InlineData("en-US")]
    [InlineData("fr")]
    [InlineData("en\n")]
    public async Task MissingOrUnsupportedLanguage_DoesNotWrite(string? language)
    {
        var store = new Store();
        await Assert.ThrowsAsync<LanguagePreferenceValidationException>(() => Service(store, store.Subject).UpdateAsync(language, "settings"));
        Assert.Equal(0, store.Writes);
        Assert.Throws<LanguagePreferenceValidationException>(() => AccountLanguagePreference.Create(store.Account.Id, language!, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("device")]
    [InlineData("Settings")]
    public async Task ExplicitSyncOrSettingsMode_IsRequired(string? mode)
    {
        var store = new Store();
        await Assert.ThrowsAsync<LanguagePreferenceValidationException>(() => Service(store, store.Subject).UpdateAsync("en", mode));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task FallbackIsReadOnly_FirstSyncPersists_LaterDeviceSyncCannotOverwriteSettings()
    {
        var store = new Store();
        var service = Service(store, store.Subject);
        Assert.Equal(new("en", false), await service.GetAsync());
        Assert.Equal(0, store.Writes);
        Assert.Null(store.Preference);
        Assert.Equal(new("ta", true), await service.UpdateAsync("ta", "sync"));
        Assert.Equal(new("ta", true), await service.UpdateAsync("si", "sync"));
        Assert.Equal(new("si", true), await service.UpdateAsync("si", "settings"));
        Assert.Equal(new("si", true), await service.UpdateAsync("ta", "sync"));
        Assert.Equal(new("si", true), await service.UpdateAsync("si", "settings"));
        Assert.Equal(new("si", true), await service.GetAsync());
    }

    [Fact]
    public async Task MissingAccount_IsNotAutomaticallyCreated()
    {
        var store = new Store();
        var service = Service(store, Guid.NewGuid());
        await Assert.ThrowsAsync<LanguagePreferenceAccountMissingException>(() => service.GetAsync());
        await Assert.ThrowsAsync<LanguagePreferenceAccountMissingException>(() => service.UpdateAsync("en", "sync"));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task UnauthenticatedUser_CannotReadOrWrite()
    {
        var store = new Store();
        var service = new LanguagePreferenceService(new User(store.Subject, false), new AccountService(new User(store.Subject, false), store, TimeProvider.System), store, TimeProvider.System);
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.GetAsync());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => service.UpdateAsync("ta", "sync"));
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    private static LanguagePreferenceService Service(Store store, Guid subject) =>
        new(new User(subject), new AccountService(new User(subject), store, TimeProvider.System), store, TimeProvider.System);
    private sealed record User(Guid? UserId, bool IsAuthenticated = true) : ICurrentUser;
    private sealed class Store : IAccountRepository, ILanguagePreferenceRepository
    {
        internal Guid Subject { get; } = Guid.NewGuid();
        internal Account Account { get; }
        internal AccountLanguagePreference? Preference;
        internal int Reads, Writes;
        internal Store() { Account = Account.Create(Subject, DateTimeOffset.UtcNow); }
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<Account?>(subject == Subject ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Language operations must not initialize accounts.");
        public Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            return Task.FromResult(Preference);
        }
        public Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            Writes++;
            if (Preference is null || (!initializeOnly && Preference.Language != language))
                Preference = AccountLanguagePreference.Create(accountId, language, now);
            return Task.FromResult(Preference);
        }
    }
}
