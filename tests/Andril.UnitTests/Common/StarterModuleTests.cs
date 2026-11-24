using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class StarterModuleTests
{
    [Theory]
    [InlineData(15, "learning", false)]
    [InlineData(15, "hobbies", false)]
    [InlineData(15, "social", false)]
    [InlineData(16, "learning", true)]
    [InlineData(16, "hobbies", true)]
    [InlineData(16, "social", false)]
    [InlineData(17, "learning", true)]
    [InlineData(17, "hobbies", true)]
    [InlineData(17, "social", false)]
    [InlineData(18, "learning", true)]
    [InlineData(18, "hobbies", true)]
    [InlineData(18, "social", true)]
    public async Task ServerEnforcesEveryModuleAgeBoundary(int age, string module, bool allowed)
    {
        var fixture = new Fixture(age);
        var status = await fixture.Service.GetAsync();
        Assert.Equal(allowed, status.Modules.Single(x => x.Module == module).AgeEligible);
        Assert.Equal(allowed, status.Modules.Single(x => x.Module == module).CanSelect);
        if (allowed)
        {
            var selected = await fixture.Service.SelectAsync(module, module == "social");
            Assert.Equal(module, selected.Membership!.Module);
            Assert.Equal("starter_selected", selected.NextStep);
            Assert.All(selected.Modules, x => Assert.False(x.CanSelect));
        }
        else
        {
            await Assert.ThrowsAsync<StarterModuleIneligibleException>(() => fixture.Service.SelectAsync(module, module == "social"));
            Assert.Null(fixture.Membership);
        }
    }

    [Theory]
    [InlineData("social", null)]
    [InlineData("social", false)]
    [InlineData("learning", true)]
    [InlineData("hobbies", true)]
    [InlineData("Social", true)]
    [InlineData("unknown", false)]
    [InlineData(null, null)]
    public async Task ExplicitSocialOptInAndExactSingleModuleAreRequired(string? module, bool? optIn)
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<StarterModuleValidationException>(() => fixture.Service.SelectAsync(module, optIn));
        Assert.Null(fixture.Membership);
    }

    [Theory]
    [InlineData("demographics")]
    [InlineData("terms")]
    [InlineData("privacy")]
    [InlineData("outdated-terms")]
    [InlineData("outdated-privacy")]
    public async Task CurrentOnboardingPrerequisitesAreMandatory(string missing)
    {
        var fixture = new Fixture();
        if (missing == "demographics") fixture.Demographics = null;
        if (missing == "terms") fixture.Policies.RemoveAll(x => x.Action == PolicyActions.TermsAgreed);
        if (missing == "privacy") fixture.Policies.RemoveAll(x => x.Action == PolicyActions.PrivacyAcknowledged);
        if (missing == "outdated-terms") fixture.Catalog = fixture.Catalog with { Terms = fixture.Catalog.Terms with { Version = "test-next" } };
        if (missing == "outdated-privacy") fixture.Catalog = fixture.Catalog with { Privacy = fixture.Catalog.Privacy with { Version = "test-next" } };
        var status = await fixture.Service.GetAsync();
        Assert.All(status.Modules, x => Assert.False(x.CanSelect));
        await Assert.ThrowsAsync<StarterModulePrerequisitesException>(() => fixture.Service.SelectAsync("learning", null));
        Assert.Null(fixture.Membership);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task RetryPreservesMembershipAndOptIn_ChangedChoiceIsRejected(string module)
    {
        var fixture = new Fixture();
        var first = await fixture.Service.SelectAsync(module, module == "social");
        var original = fixture.Membership;
        fixture.Now = fixture.Now.AddDays(1);
        var repeat = await fixture.Service.SelectAsync(module, module == "social");
        Assert.Same(original, fixture.Membership);
        Assert.Equal(first.Membership, repeat.Membership);
        Assert.Equal(module == "social" ? original!.ActivatedAtUtc : (DateTimeOffset?)null, original!.SocialOptInAtUtc);
        await Assert.ThrowsAsync<StarterModuleConflictException>(() => fixture.Service.SelectAsync(module == "learning" ? "hobbies" : "learning", false));
        Assert.Same(original, fixture.Membership);
    }

    [Fact]
    public async Task RetryRevalidatesCurrentPoliciesWithoutDuplicatingMembership()
    {
        var fixture = new Fixture();
        await fixture.Service.SelectAsync("learning", null);
        var original = fixture.Membership;
        fixture.Catalog = fixture.Catalog with { Terms = fixture.Catalog.Terms with { Version = "test-next" } };
        await Assert.ThrowsAsync<StarterModulePrerequisitesException>(() => fixture.Service.SelectAsync("learning", null));
        Assert.Same(original, fixture.Membership);
        var status = await fixture.Service.GetAsync();
        Assert.Equal("review_policies", status.NextStep);
        Assert.NotNull(status.Membership);
    }

    [Fact]
    public async Task UsesUtcBirthdayAndLockedPrerequisites_NotEarlierReads()
    {
        var fixture = new Fixture(18);
        // Local Nov 15 is still Nov 14 UTC.
        fixture.Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.FromHours(5.5));
        await Assert.ThrowsAsync<StarterModuleIneligibleException>(() => fixture.Service.SelectAsync("social", true));
        fixture.Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.True((await fixture.Service.GetAsync()).Modules.Single(x => x.Module == "social").AgeEligible);
        fixture.BeforeSelect = () => fixture.Demographics = AccountDemographics.Create(fixture.Account.Id, new(2011, 11, 15), "other", fixture.Now);
        await Assert.ThrowsAsync<StarterModuleIneligibleException>(() => fixture.Service.SelectAsync("social", true));
        Assert.Null(fixture.Membership);
    }

    [Fact]
    public async Task AuthenticationAccountAndConfigurationFailClosed()
    {
        var fixture = new Fixture();
        fixture.Authenticated = false;
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => fixture.Service.GetAsync());
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => fixture.Service.SelectAsync("learning", null));
        fixture.Authenticated = true;
        fixture.Exists = false;
        await Assert.ThrowsAsync<StarterModuleAccountMissingException>(() => fixture.Service.GetAsync());
        await Assert.ThrowsAsync<StarterModuleAccountMissingException>(() => fixture.Service.SelectAsync("learning", null));
        fixture.Exists = true;
        fixture.Configured = false;
        await Assert.ThrowsAsync<PolicyConfigurationException>(() => fixture.Service.GetAsync());
        await Assert.ThrowsAsync<PolicyConfigurationException>(() => fixture.Service.SelectAsync("learning", null));
        Assert.Null(fixture.Membership);
    }

    private sealed class Fixture : TimeProvider, ICurrentUser, IAccountRepository, IStarterModuleRepository, IPolicyCatalogProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        internal bool Authenticated = true, Exists = true, Configured = true;
        internal readonly Account Account;
        internal AccountDemographics? Demographics;
        internal readonly List<AccountPolicyRecord> Policies = new();
        internal StarterMembership? Membership;
        internal Action? BeforeSelect;
        internal PolicyCatalog Catalog = new(new("test-terms", "https://policy.test/terms", "en"),
            new("test-privacy", "https://policy.test/privacy", "en"), new("test-guidelines", "https://policy.test/guidelines", "en"));
        internal StarterModuleService Service => new(this, this, this, this, this);
        internal Fixture(int age = 18)
        {
            Account = Account.Create(Guid.NewGuid(), Now);
            Demographics = AccountDemographics.Create(Account.Id, new(2026 - age, 11, 15), "other", Now);
            Policies.Add(AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "test-terms", Now));
            Policies.Add(AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "test-privacy", Now));
        }
        public override DateTimeOffset GetUtcNow() => Now;
        public bool IsAuthenticated => Authenticated;
        public Guid? UserId => Account.SupabaseUserId;
        public PolicyCatalog GetCurrent() => Configured ? Catalog : throw new PolicyConfigurationException();
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(Exists && subject == Account.SupabaseUserId ? Account : null);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No implicit initialization.");
        public Task<StarterModuleState> ReadAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            return Task.FromResult(new StarterModuleState(Demographics, Policies, Membership));
        }
        public async Task<StarterModuleState> SelectAsync(Guid accountId, Func<StarterModuleState, StarterMembership> select,
            CancellationToken cancellationToken = default)
        {
            BeforeSelect?.Invoke();
            var state = await ReadAsync(accountId, cancellationToken);
            Membership = select(state);
            return state with { Membership = Membership };
        }
    }
}
