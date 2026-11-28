using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Common.Accounts.Privacy;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class SettingsCallerBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LanguageRejectsForeignPreferenceResults(bool write)
    {
        var f = new Fixture { ForeignLanguage = true };
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => write ? f.Language.UpdateAsync("ta", "settings") : f.Language.GetAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsReuseAccountOwnershipBoundaryBeforeAnyPreferenceAccess(bool privacy)
    {
        var f = new Fixture { WrongMapping = true };
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(async () =>
        {
            if (privacy) await f.Privacy.GetMyPrivacySettingsAsync(); else await f.Language.GetAsync();
        });
        Assert.Equal(0, f.PreferenceCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubjectChangeDuringAccountLookupCannotReachSettings(bool privacy)
    {
        var f = new Fixture();
        f.OnAccount = () => f.UserId = Guid.NewGuid();
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(async () =>
        {
            if (privacy) await f.Privacy.GetMyPrivacySettingsAsync(); else await f.Language.GetAsync();
        });
        Assert.Equal(0, f.PreferenceCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubjectChangeDuringSettingsReadCannotReleaseData(bool privacy)
    {
        var f = new Fixture();
        f.OnPreference = () => f.UserId = Guid.NewGuid();
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(async () =>
        {
            if (privacy) await f.Privacy.GetMyPrivacySettingsAsync(); else await f.Language.GetAsync();
        });
    }

    [Fact]
    public async Task ChangedSubjectAfterPrivacyLookupCannotProceedToUpdate()
    {
        var f = new Fixture();
        f.OnPreference = () => f.UserId = Guid.NewGuid();
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => f.Privacy.UpdateMyPrivacySettingsAsync(new("private", true, "disabled", false, false)));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationFromDependencyCannotReleaseSettingsEvenIfRepositoryIgnoresToken(bool privacy)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.OnPreference = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (privacy) await f.Privacy.GetMyPrivacySettingsAsync(cancellation.Token); else await f.Language.GetAsync(cancellation.Token);
        });
    }

    [Fact]
    public async Task MissingWriteResultCannotBecomeSuccessfulLanguageResponse()
    {
        var f = new Fixture { NullLanguageWrite = true };
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => f.Language.UpdateAsync("ta", "settings"));
    }

    private sealed class Fixture : ICurrentUser, IAccountRepository, ILanguagePreferenceRepository, IUserPrivacySettingsRepository
    {
        private readonly Account account = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        public Guid? UserId { get; set; }
        public bool IsAuthenticated => true;
        public bool ForeignLanguage, WrongMapping, NullLanguageWrite;
        public Action? OnAccount, OnPreference;
        public int PreferenceCalls, Writes;
        private IAccountService Accounts => new AccountService(this, this, TimeProvider.System);
        public LanguagePreferenceService Language => new(this, Accounts, this, TimeProvider.System);
        public UserPrivacySettingsService Privacy => new(this, Accounts, this, TimeProvider.System);
        public Fixture() => UserId = account.SupabaseUserId;
        public async Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            OnAccount?.Invoke();
            return WrongMapping ? Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow) : account;
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(account.Id, accountId);
            PreferenceCalls++;
            await Task.Yield();
            OnPreference?.Invoke();
            return AccountLanguagePreference.Create(ForeignLanguage ? Guid.NewGuid() : accountId, "si", DateTimeOffset.UtcNow);
        }
        public async Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Writes++;
            return NullLanguageWrite ? null! : (await FindAsync(accountId, cancellationToken))!;
        }
        public async Task<UserPrivacySettings?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(account.Id, accountId);
            PreferenceCalls++;
            await Task.Yield();
            OnPreference?.Invoke();
            return UserPrivacySettings.CreateDefault(accountId, DateTimeOffset.UtcNow);
        }
        Task<UserPrivacySettings> IUserPrivacySettingsRepository.GetOrCreateAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) => ReadPrivacy(accountId, cancellationToken);
        private async Task<UserPrivacySettings> ReadPrivacy(Guid accountId, CancellationToken ct) => (await GetByAccountIdAsync(accountId, ct))!;
        public Task<UserPrivacySettings> UpsertAsync(UserPrivacySettings settings, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(settings);
        }
    }
}
