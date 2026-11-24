using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class LifecycleProfileAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("630fd1e5-20f1-49fa-8469-b93cd6a0fa14");
    private static readonly Guid Version = new("c142dfcb-7f8a-412b-aa83-3e2b3c92c41f");

    [Fact]
    public async Task EachProfileOperationRequiresItsOwnPermissionAndHomeRemainsDenied()
    {
        var f = new Fixture("social");
        foreach (var operation in Enum.GetValues<PrivateProfileOperation>())
        {
            var profile = operation == PrivateProfileOperation.Create ? null : f.Profile;
            var evidence = f.Evidence(operation, profile);
            Assert.Equal(ModuleAuthorizationOutcome.Satisfied, f.Check(operation, profile, evidence));
            Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(operation, profile,
                evidence with { Purpose = evidence.Purpose with { Outcome = ModuleAuthorizationOutcome.Denied } }));
            Assert.Equal(ModuleAuthorizationOutcome.Unavailable, f.Check(operation, profile,
                evidence with { Purpose = evidence.Purpose with { Outcome = ModuleAuthorizationOutcome.Unavailable } }));
        }

        var home = new PrivateModuleAccess(new CompletedProfile());
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => home.RequireAsync("social"));
    }

    [Fact]
    public void ForeignScopeAndReusingPermissionForAnotherOperationOrProfileAreDenied()
    {
        var f = new Fixture("learning");
        var e = f.Evidence(PrivateProfileOperation.Read, f.Profile);
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Edit, f.Profile, e));
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Read, null, e));
        foreach (var profile in new[]
        {
            PrivateModuleProfile.Create(f.Scope.AccountId, "hobbies", Now),
            PrivateModuleProfile.Create(Guid.NewGuid(), "learning", Now),
            PrivateModuleProfile.Create(f.Scope.AccountId, "learning", Now)
        })
            Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Read, profile, e));

        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Read, f.Profile,
            e with { Membership = ActiveModuleMembership.Restore(f.Scope with { Module = "hobbies" }, 1) }));
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Authorization.Decide(f.Scope with { Module = "Learning" },
            PrivateProfileOperation.Read, f.Profile, e));
        f.Actor.UserId = Guid.NewGuid();
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Read, f.Profile, e));
        f.Actor.UserId = Subject;
        f.Actor.Confirmed = false;
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.Read, f.Profile, e));
    }

    [Fact]
    public void InactiveMissingExpiredAndSupersededEvidenceCannotAuthorize()
    {
        var f = new Fixture("hobbies");
        var e = f.Evidence(PrivateProfileOperation.WriteMedia, f.Profile);
        foreach (var membership in new ModuleMembership?[]
        {
            null, new InactiveModuleMembership(f.Scope, 2), ActiveModuleMembership.Restore(f.Scope, 3)
        })
            Assert.Equal(ModuleAuthorizationOutcome.Denied,
                f.Check(PrivateProfileOperation.WriteMedia, f.Profile, e with { Membership = membership }));

        Assert.Equal(ModuleAuthorizationOutcome.Unavailable, f.Check(PrivateProfileOperation.WriteMedia, f.Profile,
            e with { ValidUntilUtc = Now }));
        Assert.Equal(ModuleAuthorizationOutcome.Unavailable, f.Check(PrivateProfileOperation.WriteMedia, f.Profile,
            e with { Eligibility = e.Eligibility with { EvaluatedAtUtc = Now.AddSeconds(1) } }));
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.WriteMedia, f.Profile,
            e with { Eligibility = e.Eligibility with { CurrentEvidenceVersion = Guid.NewGuid() } }));
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.WriteMedia, f.Profile,
            e with { Purpose = e.Purpose with { EvidenceVersion = Guid.NewGuid() } }));
    }

    [Fact]
    public void ProfileStandingConsentAgeAndOptInAreCheckedIndependently()
    {
        var f = new Fixture("social");
        var e = f.Evidence(PrivateProfileOperation.ReadMedia, f.Profile);
        Assert.Equal(ModuleAuthorizationOutcome.Unavailable, f.Check(PrivateProfileOperation.ReadMedia, f.Profile,
            e with { AccountStandingAllowsAccess = null }));
        Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.ReadMedia, f.Profile,
            e with { AccountStandingAllowsAccess = false }));
        var policy = new PolicyDocument("next-version", "https://example.invalid", "en");
        foreach (var eligibility in new[]
        {
            e.Eligibility with { Receipts = [] },
            e.Eligibility with { CurrentPolicies = new(policy, policy, policy) },
            e.Eligibility with { SocialOptIn = false },
            e.Eligibility with { Demographics = AccountDemographics.Create(f.Scope.AccountId,
                new DateOnly(2009, 1, 1), "prefer_not_to_say", Now) }
        })
            Assert.Equal(ModuleAuthorizationOutcome.Denied, f.Check(PrivateProfileOperation.ReadMedia, f.Profile,
                e with { Eligibility = eligibility }));
        Assert.Equal(ModuleAuthorizationOutcome.Unavailable, f.Check(PrivateProfileOperation.ReadMedia, f.Profile,
            e with { Eligibility = e.Eligibility with { SourceOutcome = ModuleAuthorizationOutcome.Unavailable } }));
    }

    private sealed class Fixture
    {
        private readonly ModuleActivationEvidence eligibility;
        internal Actor Actor { get; } = new();
        internal ModuleMembershipScope Scope { get; }
        internal PrivateModuleProfile Profile { get; }
        internal LifecycleProfileAuthorization Authorization { get; }

        internal Fixture(string module)
        {
            var account = Account.Create(Subject, Now);
            Scope = new(account.Id, module);
            Profile = PrivateModuleProfile.Create(account.Id, module, Now);
            var policy = new PolicyDocument("test-version", "https://example.invalid", "en");
            eligibility = new(Scope, ModuleAuthorizationOutcome.Satisfied, account, Subject,
                AccountDemographics.Create(account.Id, new DateOnly(2000, 1, 1), "prefer_not_to_say", Now),
                new(policy, policy, policy),
                [
                    AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, policy.Version, Now),
                    AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, policy.Version, Now)
                ], null, module == "social", Version, Version, Now);
            Authorization = new(Actor, Actor, new FixedClock());
        }

        internal LifecycleProfileEvidence Evidence(PrivateProfileOperation operation, PrivateModuleProfile? profile) =>
            new(eligibility, ActiveModuleMembership.Restore(Scope, 1), true,
                new(Scope, profile?.Id, operation, 1, Version, ModuleAuthorizationOutcome.Satisfied), Now.AddMinutes(1));

        internal ModuleAuthorizationOutcome Check(PrivateProfileOperation operation, PrivateModuleProfile? profile,
            LifecycleProfileEvidence evidence) => Authorization.Decide(Scope, operation, profile, evidence);
    }

    private sealed class Actor : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated => true;
        internal bool Confirmed { get; set; } = true;
        public bool IsConfirmedFor(Guid subject) => Confirmed && subject == Subject;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CompletedProfile : IPrivateProfileCompletionReader
    {
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProfileCompletionStatus(null, true));
    }
}
