using Andril.Common.Accounts;
using Andril.Common.Accounts.Privacy;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class PrivacySettingsServiceTests
{
    [Fact]
    public async Task GetUsesAtomicCreationAndNeverReplacesRestrictiveSettings()
    {
        var fixture = new Fixture();
        fixture.Settings.Update("private", true, "disabled", false, false, DateTimeOffset.UtcNow);
        var result = await fixture.Service.GetMyPrivacySettingsAsync();
        Assert.Equal("private", result.ProfileDiscoverability);
        Assert.True(result.GhostModeEnabled);
        Assert.Equal("disabled", result.DirectMessagingPolicy);
        Assert.Equal(1, fixture.CreateCalls);
        Assert.Equal(0, fixture.UpsertCalls);
        Assert.Equal(0, fixture.ReadCalls);
    }

    [Fact]
    public async Task UpdatePreservesFullReplacementContractAndRejectsInvalidValuesBeforeWrite()
    {
        var fixture = new Fixture();
        var result = await fixture.Service.UpdateMyPrivacySettingsAsync(new("private", true, "disabled", false, false));
        Assert.Equal(fixture.Account.Id, result.AccountId);
        Assert.Equal("private", result.ProfileDiscoverability);
        Assert.Equal(1, fixture.UpsertCalls);
        await Assert.ThrowsAsync<PrivacyValidationException>(() => fixture.Service.UpdateMyPrivacySettingsAsync(new("unknown", false, "everyone", true, true)));
        Assert.Equal(1, fixture.UpsertCalls);
        Assert.Equal("private", fixture.Settings.ProfileDiscoverability);
    }

    [Fact]
    public async Task ForeignRepositorySettingsCannotBeDisclosedOrUpdated()
    {
        var fixture = new Fixture();
        fixture.Settings = UserPrivacySettings.CreateDefault(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => fixture.Service.GetMyPrivacySettingsAsync());
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => fixture.Service.UpdateMyPrivacySettingsAsync(new("private", true, "disabled", false, false)));
        Assert.Equal(0, fixture.UpsertCalls);
    }

    [Fact]
    public async Task WrongSubjectMappingCannotReachPrivacyRepository()
    {
        var fixture = new Fixture { WrongAccount = true };
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => fixture.Service.GetMyPrivacySettingsAsync());
        Assert.Equal(0, fixture.CreateCalls);
        Assert.Equal(0, fixture.ReadCalls);
    }

    private sealed class Fixture : ICurrentUser, IAccountRepository, IUserPrivacySettingsRepository
    {
        public Account Account { get; } = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        public UserPrivacySettings Settings;
        public int CreateCalls, ReadCalls, UpsertCalls;
        public bool WrongAccount;
        public Guid? UserId => Account.SupabaseUserId;
        public bool IsAuthenticated => true;
        public UserPrivacySettingsService Service => new(this, this, this, TimeProvider.System);
        public Fixture() => Settings = UserPrivacySettings.CreateDefault(Account.Id, DateTimeOffset.UtcNow);
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(WrongAccount ? Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow) : Account);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        Task<UserPrivacySettings> IUserPrivacySettingsRepository.GetOrCreateAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Assert.Equal(Account.Id, accountId);
            CreateCalls++;
            return Task.FromResult(Settings);
        }
        public Task<UserPrivacySettings?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            ReadCalls++;
            return Task.FromResult<UserPrivacySettings?>(Settings);
        }
        public Task<UserPrivacySettings> UpsertAsync(UserPrivacySettings settings, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, settings.AccountId);
            UpsertCalls++;
            return Task.FromResult(Settings = settings);
        }
    }
}
