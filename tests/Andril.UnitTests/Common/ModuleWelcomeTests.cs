using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Common;

public sealed class ModuleWelcomeTests
{
    [Fact]
    public async Task PS_T01_LearningWelcome_EligibleUser_Succeeds()
    {
        var (service, account) = CreateService(isSocialAgeEligible: true, isSocialOptIn: false);

        var status = await service.GetWelcomeStatusAsync(ModuleNames.Learning);

        Assert.Equal(ModuleNames.Learning, status.Module);
        Assert.True(status.IsEligible);
        Assert.True(status.CanProceedToProfileSetup);
        Assert.Equal("Start Your Learning Journey", status.Title);
    }

    [Fact]
    public async Task PS_T02_HobbiesWelcome_EligibleUser_Succeeds()
    {
        var (service, account) = CreateService(isSocialAgeEligible: true, isSocialOptIn: false);

        var status = await service.GetWelcomeStatusAsync(ModuleNames.Hobbies);

        Assert.Equal(ModuleNames.Hobbies, status.Module);
        Assert.True(status.IsEligible);
        Assert.True(status.CanProceedToProfileSetup);
        Assert.Equal("Explore Your Hobbies", status.Title);
    }

    [Fact]
    public async Task PS_T03_SocialWelcome_EligibleWithOptIn_Succeeds()
    {
        var (service, account) = CreateService(isSocialAgeEligible: true, isSocialOptIn: true);

        var status = await service.GetWelcomeStatusAsync(ModuleNames.Social);

        Assert.Equal(ModuleNames.Social, status.Module);
        Assert.True(status.IsEligible);
        Assert.True(status.RequiresSocialOptIn);
        Assert.True(status.SocialOptInConfirmed);
        Assert.True(status.CanProceedToProfileSetup);
    }

    [Fact]
    public async Task PS_T04_SocialWelcome_Underage_Denied()
    {
        var (service, account) = CreateService(isSocialAgeEligible: false, isSocialOptIn: false);

        var status = await service.GetWelcomeStatusAsync(ModuleNames.Social);

        Assert.False(status.IsEligible);
        Assert.False(status.CanProceedToProfileSetup);
        Assert.NotNull(status.IneligibilityReason);

        await Assert.ThrowsAsync<ModuleWelcomeIneligibleException>(() =>
            service.GetStartedAsync(ModuleNames.Social, new(true)));
    }

    [Fact]
    public async Task PS_T05_SocialWelcome_WithoutOptIn_Denied()
    {
        var (service, account) = CreateService(isSocialAgeEligible: true, isSocialOptIn: false);

        await Assert.ThrowsAsync<SocialOptInRequiredException>(() =>
            service.GetStartedAsync(ModuleNames.Social, new(false)));
    }

    [Fact]
    public async Task PS_T06_UnauthenticatedAccess_ThrowsAuthenticationRequiredException()
    {
        var userId = Guid.NewGuid();
        var account = Account.Create(userId, DateTimeOffset.UtcNow);
        var unauthUser = new FakeUser(null, false);
        var identity = new FakeIdentity(true);
        var accounts = new FakeAccountRepo(account);
        var starter = new FakeStarterService(true, true);
        var profiles = new FakeProfileRepo();

        var service = new ModuleWelcomeService(unauthUser, identity, accounts, starter, profiles, TimeProvider.System);

        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() =>
            service.GetWelcomeStatusAsync(ModuleNames.Learning));
    }

    private static (ModuleWelcomeService Service, Account Account) CreateService(bool isSocialAgeEligible, bool isSocialOptIn)
    {
        var userId = Guid.NewGuid();
        var account = Account.Create(userId, DateTimeOffset.UtcNow);
        var user = new FakeUser(userId, true);
        var identity = new FakeIdentity(true);
        var accounts = new FakeAccountRepo(account);
        var starter = new FakeStarterService(isSocialAgeEligible, isSocialOptIn);
        var profiles = new FakeProfileRepo();

        var service = new ModuleWelcomeService(user, identity, accounts, starter, profiles, TimeProvider.System);
        return (service, account);
    }

    private sealed record FakeUser(Guid? UserId, bool IsAuthenticated) : ICurrentUser;

    private sealed class FakeIdentity(bool confirmed) : IOnboardingIdentity
    {
        public bool IsConfirmedFor(Guid subject) => confirmed;
    }

    private sealed class FakeAccountRepo(Account account) : IAccountRepository
    {
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(subject == account.SupabaseUserId ? account : null);

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);
    }

    private sealed class FakeStarterService(bool socialAgeEligible, bool socialOptIn) : IStarterModuleService
    {
        public Task<StarterModuleStatus> GetAsync(CancellationToken cancellationToken = default)
        {
            var eligibilities = new List<StarterModuleEligibility>
            {
                new(ModuleNames.Learning, true, false, true),
                new(ModuleNames.Hobbies, true, false, true),
                new(ModuleNames.Social, socialAgeEligible, true, socialAgeEligible)
            };
            var membership = socialOptIn
                ? new StarterMembershipDto(ModuleNames.Social, DateTimeOffset.UtcNow, true)
                : null;

            return Task.FromResult(new StarterModuleStatus(
                true, true, eligibilities, membership, "profile_setup"));
        }

        public Task<StarterModuleStatus> SelectAsync(string? module, bool? optIn, CancellationToken cancellationToken = default) =>
            GetAsync(cancellationToken);
    }

    private sealed class FakeProfileRepo : IPrivateProfileRepository
    {
        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default) =>
            Task.FromResult<PrivateModuleProfile?>(null);

        public Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default) =>
            Task.FromResult(PrivateModuleProfile.Create(accountId, module, clock.GetUtcNow()));
    }
}
