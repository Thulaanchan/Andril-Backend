using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class PrivateProfileTests
{
    [Theory]
    [InlineData("learning", 15, false)]
    [InlineData("learning", 16, true)]
    [InlineData("hobbies", 16, true)]
    [InlineData("social", 17, false)]
    [InlineData("social", 18, true)]
    public async Task CreationChecksAgeAndIsIdempotent(string module, int age, bool allowed)
    {
        var f = new Fixture(module, age);
        if (!allowed)
        {
            await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Service.CreateAsync(module));
            Assert.Null(f.Profile);
            return;
        }
        var first = await f.Service.CreateAsync(module);
        Assert.Equal(first, await f.Service.CreateAsync(module));
        Assert.Equal(first, await f.Service.GetAsync(module));
        Assert.False(first.IsComplete);
        Assert.Equal("approved_profile_contract", first.BlockingDependency);
    }

    [Theory]
    [InlineData("Admin", "new bio", "private")]
    [InlineData("Valid Name", "invalid\0bio", "private")]
    [InlineData("Valid Name", "new bio", "public")]
    public void InvalidDraftNeverChangesEntity(string name, string bio, string visibility)
    {
        var profile = PrivateModuleProfile.Create(Guid.NewGuid(), "learning", DateTimeOffset.UtcNow);
        const string originalBio = "  தமிழ் 👩‍💻\r\nසිංහල  ";
        profile.SetDraftFields(new("  Alex_2000  ", originalBio, "private"));
        Assert.Throws<ProfileValidationException>(() => profile.SetDraftFields(new(name, bio, visibility)));
        Assert.Equal("Alex_2000", profile.DisplayName);
        Assert.Equal("alex_2000", profile.CanonicalDisplayName);
        Assert.Equal(originalBio, profile.Bio);
        Assert.Equal("private", profile.Visibility);
    }

    [Fact]
    public async Task LegacyGenderDoesNotChangeSocialAgeChecksOrEnableHome()
    {
        var f = new Fixture("social", 18);
        var demographics = f.State.Demographics!;
        typeof(AccountDemographics).GetProperty(nameof(AccountDemographics.Gender))!
            .SetValue(demographics, "prefer not to say");
        var draft = await f.Service.CreateAsync("social");
        Assert.False(draft.IsComplete);
        await Assert.ThrowsAsync<ProfileContractPendingException>(() =>
            new PrivateModuleAccess(new PrivateProfileCompletionReader(f.Service)).RequireAsync("social"));
        Assert.Equal("prefer not to say", demographics.Gender);
    }

    [Fact]
    public void ThreeModuleIdentityRootsHaveIndependentRandomIds()
    {
        var account = Guid.NewGuid();
        var profiles = new[] { "learning", "hobbies", "social" }
            .Select(module => PrivateModuleProfile.Create(account, module, DateTimeOffset.UtcNow)).ToArray();
        Assert.Equal(3, profiles.Select(x => x.Id).Distinct().Count());
        Assert.All(profiles, x => Assert.NotEqual(account, x.Id));
        Assert.Throws<ProfileValidationException>(() => PrivateModuleProfile.Create(account, "dating", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("demographics")]
    [InlineData("policies")]
    [InlineData("outdated")]
    [InlineData("other-account")]
    [InlineData("other-module")]
    public async Task MissingPrerequisitesAndTamperedRepositoryEvidenceAreDenied(string fault)
    {
        var f = new Fixture();
        if (fault == "membership") f.State = f.State with { Membership = null };
        if (fault == "demographics") f.State = f.State with { Demographics = null };
        if (fault == "policies") f.State = f.State with { Policies = [] };
        if (fault == "outdated") f.Version = "v2";
        if (fault == "other-account") f.Profile = PrivateModuleProfile.Create(Guid.NewGuid(), "learning", f.GetUtcNow());
        if (fault == "other-module") f.Profile = PrivateModuleProfile.Create(f.Account.Id, "hobbies", f.GetUtcNow());
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Service.CreateAsync("learning"));
    }

    [Fact]
    public async Task AuthenticationIdentityAndCrossModuleChecksPrecedeWrites()
    {
        var f = new Fixture();
        f.Authenticated = false;
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => f.Service.CreateAsync("learning"));
        f.Authenticated = true; f.Confirmed = false;
        await Assert.ThrowsAsync<OnboardingDependencyException>(() => f.Service.CreateAsync("learning"));
        f.Confirmed = true;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => f.Service.CreateAsync("hobbies"));
        Assert.Null(f.Profile);
    }

    [Fact]
    public async Task MissingAndExistingShellsNeverCompleteOnboardingOrAuthorizeModuleHome()
    {
        var f = new Fixture();
        var reader = new PrivateProfileCompletionReader(f.Service);
        var onboarding = new OnboardingStatusService(f, f, f, f, reader);
        Assert.Equal("private_profile_missing", (await onboarding.GetAsync()).BlockingDependency);
        await f.Service.CreateAsync("learning");
        var status = await onboarding.GetAsync();
        Assert.Equal("profile_setup", status.NextStep);
        Assert.Equal("approved_profile_contract", status.BlockingDependency);
        Assert.False(status.Progress.PrivateProfileComplete);
        Assert.False(status.OnboardingComplete);
        Assert.False(status.ModuleHomeAuthorized);
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => new PrivateModuleAccess(reader).RequireAsync("learning"));
        f.State = f.State with { Policies = [] };
        await Assert.ThrowsAsync<OnboardingDependencyException>(() => onboarding.GetAsync());
    }

    [Fact]
    public async Task MissingConfigurationAndStorageFailClosed()
    {
        var f = new Fixture { Configured = false };
        await Assert.ThrowsAsync<PolicyConfigurationException>(() => f.Service.CreateAsync("learning"));
        f.Configured = true; f.FailStore = true;
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => new PrivateProfileCompletionReader(f.Service).ReadAsync("learning"));
    }

    private sealed class Fixture : TimeProvider, ICurrentUser, IOnboardingIdentity, IAccountRepository,
        IPrivateProfileRepository, IPolicyCatalogProvider, IStarterModuleService
    {
        internal readonly Account Account = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        internal StarterModuleState State;
        internal PrivateModuleProfile? Profile;
        internal bool Authenticated = true, Confirmed = true, Configured = true, FailStore;
        internal string Version = "v1";
        internal PrivateProfileService Service => new(this, this, this, this, this, this);
        internal Fixture(string module = "learning", int age = 18)
        {
            State = new(AccountDemographics.Create(Account.Id, new(2026 - age, 1, 1), "other", GetUtcNow()),
                [AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "v1", GetUtcNow()),
                 AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "v1", GetUtcNow())],
                StarterMembership.Create(Account.Id, module, module == "social", GetUtcNow()));
        }
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        public Guid? UserId => Account.SupabaseUserId;
        public bool IsAuthenticated => Authenticated;
        public bool IsConfirmedFor(Guid subject) => Confirmed && subject == Account.SupabaseUserId;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(subject == Account.SupabaseUserId ? Account : null);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public PolicyCatalog GetCurrent() => Configured ? new(new(Version, "https://example.test/terms", "en"),
            new(Version, "https://example.test/privacy", "en"), new(Version, "https://example.test/community", "en")) : throw new PolicyConfigurationException();
        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            if (FailStore) throw new AccountStoreUnavailableException();
            validate(State);
            return Task.FromResult(Profile);
        }
        public async Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default)
        {
            await ReadAsync(accountId, module, validate, cancellationToken);
            return Profile ??= PrivateModuleProfile.Create(accountId, module, clock.GetUtcNow());
        }
        // Deliberately stale first read: the profile boundary must independently revalidate.
        public Task<StarterModuleStatus> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StarterModuleStatus(true, true,
            [new("learning", true, false, false), new("hobbies", true, false, false), new("social", true, true, false)],
            new("learning", GetUtcNow(), false), "starter_selected"));
        public Task<StarterModuleStatus> SelectAsync(string? module, bool? socialOptIn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
