
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class OnboardingStatusTests
{
    [Theory]
    [InlineData("new", "initialize_account")]
    [InlineData("demographics", "complete_demographics")]
    [InlineData("underage", "age_ineligible")]
    [InlineData("terms", "review_policies")]
    [InlineData("privacy", "review_policies")]
    [InlineData("outdated", "review_policies")]
    [InlineData("starter", "select_starter")]
    [InlineData("profile", "profile_setup")]
    [InlineData("social-underage", "age_ineligible")]
    public async Task NextStepUsesCurrentBackendPrerequisites(
        string scenario, string next)
    {
        var fixture = new Fixture();
        fixture.Ready();

        if (scenario == "new")
        {
            fixture.Exists = false;
            fixture.Configured = false;
        }

        if (scenario == "demographics")
            fixture.Demographics = null;

        if (scenario == "underage")
            fixture.SetAge(15);

        if (scenario == "terms")
            fixture.Policies.RemoveAll(
                x => x.Action == PolicyActions.TermsAgreed);

        if (scenario == "privacy")
            fixture.Policies.RemoveAll(
                x => x.Action == PolicyActions.PrivacyAcknowledged);

        if (scenario == "outdated")
            fixture.Catalog = fixture.Catalog with
            {
                Terms = fixture.Catalog.Terms with
                {
                    Version = "terms-next"
                }
            };

        if (scenario == "starter")
            fixture.Membership = null;

        if (scenario == "social-underage")
        {
            fixture.Membership = StarterMembership.Create(
                fixture.Account.Id, "social", true, fixture.Now);

            fixture.SetAge(17);
        }

        var result = await fixture.Service().GetAsync();

        Assert.Equal(next, result.NextStep);
        Assert.False(result.Progress.PrivateProfileComplete);
        Assert.False(result.OnboardingComplete);
        Assert.False(result.ModuleHomeAuthorized);

        Assert.Equal(
            next == "profile_setup"
                ? "private_profile_infrastructure"
                : null,
            result.BlockingDependency);

        if (scenario == "new")
            Assert.Equal(0, fixture.StateReads);
    }

    [Fact]
    public async Task RestartResumeAndRegressionsAreDerived_NotRemembered()
    {
        var fixture = new Fixture { Exists = false };

        Assert.Equal(
            "initialize_account",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.Exists = true;

        Assert.Equal(
            "complete_demographics",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.SetAge(18);

        Assert.Equal(
            "review_policies",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.AcceptPolicies();

        Assert.Equal(
            "select_starter",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.Membership = StarterMembership.Create(
            fixture.Account.Id, "hobbies", null, fixture.Now);

        Assert.Equal(
            "profile_setup",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.Catalog = fixture.Catalog with
        {
            Privacy = fixture.Catalog.Privacy with
            {
                Version = "privacy-next"
            }
        };

        Assert.Equal(
            "review_policies",
            (await fixture.Service().GetAsync()).NextStep);

        fixture.SetAge(15);

        Assert.Equal(
            "age_ineligible",
            (await fixture.Service().GetAsync()).NextStep);
    }

    [Fact]
    public async Task MissingIdentityConfigurationAndStoreFailClosed()
    {
        var fixture = new Fixture();

        fixture.Authenticated = false;

        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(
            () => fixture.Service().GetAsync());

        fixture.Authenticated = true;
        fixture.Confirmed = false;

        await Assert.ThrowsAsync<OnboardingDependencyException>(
            () => fixture.Service().GetAsync());

        Assert.Equal(0, fixture.AccountReads);

        fixture.Confirmed = true;
        fixture.Configured = false;

        await Assert.ThrowsAsync<PolicyConfigurationException>(
            () => fixture.Service().GetAsync());

        fixture.Configured = true;
        fixture.StoreUnavailable = true;

        await Assert.ThrowsAsync<AccountStoreUnavailableException>(
            () => fixture.Service().GetAsync());
    }

    [Fact]
    public async Task ConcurrentAndRepeatedReadsNeverWriteOrInventProfileEvidence()
    {
        var fixture = new Fixture();
        fixture.Ready();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => fixture.Service().GetAsync()));

        Assert.All(
            results,
            result => Assert.Equal(results[0], result));

        Assert.Equal("profile_setup", results[0].NextStep);
        Assert.Equal("learning", results[0].SelectedModule);
    }

    // NEW TEST:
    // If prerequisites become invalid during the authoritative
    // profile check, onboarding must fail closed.
    [Theory]
    [InlineData("profile_access_denied")]
    [InlineData("starter_module_account_missing")]
    public async Task ProfileRecheckFailureFailsClosed(
        string failure)
    {
        var fixture = new Fixture();
        fixture.Ready();

        var reader = new FailingProfileReader(failure);

        await Assert.ThrowsAsync<OnboardingDependencyException>(
            () => fixture.Service(reader).GetAsync());

        Assert.True(fixture.AccountReads >= 1);
        Assert.Equal(1, fixture.StateReads);
        Assert.Equal(1, reader.ReadCalls);
    }

    // NEW TEST DOUBLE:
    // Simulates an authoritative profile dependency failure.
    private sealed class FailingProfileReader(string failure)
        : IPrivateProfileCompletionReader
    {
        public int ReadCalls { get; private set; }

        public Task<ProfileCompletionStatus> ReadAsync(
            string module,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;

            Assert.Equal("learning", module);

            if (failure == "profile_access_denied")
                throw new ProfileAccessDeniedException();

            if (failure == "starter_module_account_missing")
                throw new StarterModuleAccountMissingException();

            throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private sealed class Fixture : TimeProvider,
        ICurrentUser,
        IOnboardingIdentity,
        IAccountRepository,
        IStarterModuleRepository,
        IPolicyCatalogProvider
    {
        internal DateTimeOffset Now =
            new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        internal bool Exists = true;
        internal bool Authenticated = true;
        internal bool Confirmed = true;
        internal bool Configured = true;
        internal bool StoreUnavailable;

        internal int AccountReads;
        internal int StateReads;

        internal readonly Account Account;

        internal AccountDemographics? Demographics;
        internal List<AccountPolicyRecord> Policies = new();
        internal StarterMembership? Membership;

        internal PolicyCatalog Catalog = new(
            new("terms-v1", "https://policy.test/terms", "en"),
            new("privacy-v1", "https://policy.test/privacy", "en"),
            new("guidelines-v1", "https://policy.test/guidelines", "en"));

        internal Fixture()
        {
            Account = Account.Create(Guid.NewGuid(), Now);
        }

        // UPDATED:
        // Optional completion reader enables testing profile failures
        // while preserving every existing fixture call.
        internal OnboardingStatusService Service(
            IPrivateProfileCompletionReader? profiles = null)
            => new(
                this,
                this,
                this,
                new StarterModuleService(
                    this, this, this, this, this),
                profiles);

        internal void Ready()
        {
            SetAge(18);
            AcceptPolicies();

            Membership = StarterMembership.Create(
                Account.Id, "learning", null, Now);
        }

        internal void SetAge(int age)
            => Demographics = AccountDemographics.Create(
                Account.Id,
                new(2026 - age, 11, 15),
                "private",
                Now);

        internal void AcceptPolicies()
            => Policies =
            [
                AccountPolicyRecord.Create(
                    Account.Id,
                    PolicyActions.TermsAgreed,
                    Catalog.Terms.Version,
                    Now),

                AccountPolicyRecord.Create(
                    Account.Id,
                    PolicyActions.PrivacyAcknowledged,
                    Catalog.Privacy.Version,
                    Now)
            ];

        public override DateTimeOffset GetUtcNow() => Now;

        public bool IsAuthenticated => Authenticated;

        public Guid? UserId => Account.SupabaseUserId;

        public bool IsConfirmedFor(Guid subject)
            => Confirmed && subject == Account.SupabaseUserId;

        public PolicyCatalog GetCurrent()
            => Configured
                ? Catalog
                : throw new PolicyConfigurationException();

        public Task<Account?> FindBySubjectAsync(
            Guid subject,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref AccountReads);

            if (StoreUnavailable)
                throw new AccountStoreUnavailableException();

            return Task.FromResult<Account?>(
                Exists && subject == Account.SupabaseUserId
                    ? Account
                    : null);
        }

        public Task<Account> GetOrCreateAsync(
            Guid subject,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Status must not create accounts.");

        public Task<StarterModuleState> ReadAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);

            Interlocked.Increment(ref StateReads);

            return Task.FromResult(
                new StarterModuleState(
                    Demographics,
                    Policies.ToArray(),
                    Membership));
        }

        public Task<StarterModuleState> SelectAsync(
            Guid accountId,
            Func<StarterModuleState, StarterMembership> select,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Status must not activate modules.");
    }
}
