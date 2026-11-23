using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Common;

public sealed class ModuleLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSubject = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EvidenceVersion = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NextEvidenceVersion = new("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData(ModuleNames.Learning)]
    [InlineData(ModuleNames.Hobbies)]
    [InlineData(ModuleNames.Social)]
    public void InitialActivationNeedsNoPrivateProfile(string module)
    {
        var evidence = EvidenceFor(module);

        var membership = ActiveModuleMembership.Activate(evidence.Scope, evidence);

        Assert.Equal(evidence.Scope, membership.Scope);
        Assert.Equal(1, membership.Revision);
    }

    [Theory]
    [InlineData(ModuleNames.Learning, 15)]
    [InlineData(ModuleNames.Hobbies, 15)]
    [InlineData(ModuleNames.Social, 17)]
    public void BothTransitionsDenyUnderageUsers(string module, int age) =>
        BothTransitionsReject(EvidenceFor(module, age), ModuleLifecycleDenial.IneligibleAge);

    [Fact]
    public void SocialEligibilityUsesTheUtcBirthday()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var localMidnight = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(5.5));

        BothTransitionsReject(evidence with { EvaluatedAtUtc = localMidnight }, ModuleLifecycleDenial.IneligibleAge);
        Assert.Equal(1, ActiveModuleMembership.Activate(evidence.Scope, evidence).Revision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void BothTransitionsRequireCurrentExplicitSocialOptIn(bool? optIn) =>
        BothTransitionsReject(EvidenceFor(ModuleNames.Social) with { SocialOptIn = optIn }, ModuleLifecycleDenial.InvalidOptIn);

    [Theory]
    [InlineData(ModuleNames.Learning)]
    [InlineData(ModuleNames.Hobbies)]
    public void SocialOptInCannotBeAppliedToAnotherModule(string module) =>
        BothTransitionsReject(EvidenceFor(module) with { SocialOptIn = true }, ModuleLifecycleDenial.InvalidOptIn);

    [Theory]
    [InlineData(PolicyActions.TermsAgreed)]
    [InlineData(PolicyActions.PrivacyAcknowledged)]
    public void BothTransitionsRequireCurrentPolicyReceipts(string action)
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var remaining = evidence.Receipts!.Where(receipt => receipt.Action != action).ToArray();
        var accountId = evidence.Scope.AccountId;

        BothTransitionsReject(evidence with { Receipts = remaining }, ModuleLifecycleDenial.MissingCurrentPolicies);
        BothTransitionsReject(evidence with
        {
            Receipts = [.. remaining, AccountPolicyRecord.Create(accountId, action, "old", Now)]
        }, ModuleLifecycleDenial.MissingCurrentPolicies);
        BothTransitionsReject(evidence with
        {
            Receipts = [.. remaining, AccountPolicyRecord.Create(accountId, action, "current", Now.AddDays(1))]
        }, ModuleLifecycleDenial.MissingCurrentPolicies);
        BothTransitionsReject(evidence with
        {
            Receipts = [.. remaining, AccountPolicyRecord.Create(Account.Create(OtherSubject, Now).Id, action, "current", Now)]
        }, ModuleLifecycleDenial.MissingCurrentPolicies);
    }

    [Fact]
    public void BothTransitionsRequireAnInitializedAccountAndSameSubjectConfirmation()
    {
        var evidence = EvidenceFor(ModuleNames.Learning);
        BothTransitionsReject(evidence with { Account = null }, ModuleLifecycleDenial.UnconfirmedAccount);
        BothTransitionsReject(evidence with { ConfirmedSubject = null }, ModuleLifecycleDenial.UnconfirmedAccount);
        BothTransitionsReject(evidence with { ConfirmedSubject = OtherSubject }, ModuleLifecycleDenial.UnconfirmedAccount);
        BothTransitionsReject(evidence with { Account = Account.Create(Subject, Now) }, ModuleLifecycleDenial.UnconfirmedAccount);
    }

    [Fact]
    public void BothTransitionsDenyAnAuthoritativeRestriction() =>
        BothTransitionsReject(EvidenceFor(ModuleNames.Social) with { RestrictionsAllowActivation = false },
            ModuleLifecycleDenial.AccountRestricted);

    [Fact]
    public void MissingSourcesAreUnavailableRatherThanConfirmedDenials()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        BothTransitionsReject(evidence with { RestrictionsAllowActivation = null },
            ModuleLifecycleDenial.EvidenceUnavailable, ModuleAuthorizationOutcome.Unavailable);
        BothTransitionsReject(evidence with { CurrentPolicies = null },
            ModuleLifecycleDenial.EvidenceUnavailable, ModuleAuthorizationOutcome.Unavailable);
        BothTransitionsReject(evidence with { Receipts = null },
            ModuleLifecycleDenial.EvidenceUnavailable, ModuleAuthorizationOutcome.Unavailable);
        var catalog = evidence.CurrentPolicies!;
        BothTransitionsReject(evidence with { CurrentPolicies = catalog with { Terms = catalog.Terms with { Version = "" } } },
            ModuleLifecycleDenial.EvidenceUnavailable, ModuleAuthorizationOutcome.Unavailable);
    }

    [Theory]
    [InlineData((int)ModuleAuthorizationOutcome.Denied)]
    [InlineData((int)ModuleAuthorizationOutcome.Unavailable)]
    [InlineData(-1)]
    public void OnlyASatisfiedAuthorityCanPermitNewTransitionsOrRetries(int authorityOutcome)
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var initial = ActiveModuleMembership.Activate(evidence.Scope, evidence);
        var inactive = new InactiveModuleMembership(evidence.Scope, 4);
        var reactivated = ActiveModuleMembership.Reactivate(evidence.Scope, evidence, inactive, 4);
        var adverse = evidence with { SourceOutcome = (ModuleAuthorizationOutcome)authorityOutcome };
        var denied = authorityOutcome == (int)ModuleAuthorizationOutcome.Denied;
        var reason = denied ? ModuleLifecycleDenial.AuthorityDenied : ModuleLifecycleDenial.EvidenceUnavailable;
        var outcome = denied ? ModuleAuthorizationOutcome.Denied : ModuleAuthorizationOutcome.Unavailable;

        BothTransitionsReject(adverse, reason, outcome);
        AssertDenied(reason, () => ActiveModuleMembership.Activate(evidence.Scope, adverse, initial), outcome);
        AssertDenied(reason, () => ActiveModuleMembership.Reactivate(evidence.Scope, adverse, reactivated, 4), outcome);
        Assert.Equal(1, initial.Revision);
        Assert.Equal(4, inactive.Revision);
        Assert.Equal(5, reactivated.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Social")]
    [InlineData(" social")]
    [InlineData("dating")]
    public void BothTransitionsRejectNoncanonicalModules(string module)
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        BothTransitionsReject(evidence with { Scope = evidence.Scope with { Module = module } },
            ModuleLifecycleDenial.InvalidScope);
    }

    [Fact]
    public void BothTransitionsRejectStaleOrMissingEvidenceVersions()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        BothTransitionsReject(evidence with { CurrentEvidenceVersion = NextEvidenceVersion }, ModuleLifecycleDenial.StaleEvidence);
        BothTransitionsReject(evidence with { ObservedEvidenceVersion = Guid.Empty }, ModuleLifecycleDenial.StaleEvidence);
        BothTransitionsReject(evidence with { CurrentEvidenceVersion = Guid.Empty },
            ModuleLifecycleDenial.EvidenceUnavailable, ModuleAuthorizationOutcome.Unavailable);
    }

    [Fact]
    public void DuplicateActivationReturnsTheSameMembershipAfterRevalidation()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var membership = ActiveModuleMembership.Activate(evidence.Scope, evidence);

        Assert.Same(membership, ActiveModuleMembership.Activate(evidence.Scope,
            evidence with { EvaluatedAtUtc = Now.AddDays(1) }, membership));
        AssertDenied(ModuleLifecycleDenial.InvalidOptIn,
            () => ActiveModuleMembership.Activate(evidence.Scope, evidence with { SocialOptIn = false }, membership));
        Assert.Equal(evidence.Scope, membership.Scope);
        Assert.Equal(1, membership.Revision);
    }

    [Fact]
    public void AdditionalActivationPreservesStarterSelectionAndExistingMembership()
    {
        var learning = EvidenceFor(ModuleNames.Learning);
        var starter = StarterMembership.Create(learning.Scope.AccountId, ModuleNames.Learning, null, Now);
        var starterMembership = ActiveModuleMembership.Activate(learning.Scope, learning);
        var hobbiesScope = learning.Scope with { Module = ModuleNames.Hobbies };

        var hobbiesMembership = ActiveModuleMembership.Activate(hobbiesScope, learning with { Scope = hobbiesScope });

        Assert.Equal(learning.Scope.AccountId, starter.AccountId);
        Assert.Equal(ModuleNames.Learning, starter.Module);
        Assert.Equal(Now, starter.ActivatedAtUtc);
        Assert.Equal(learning.Scope, starterMembership.Scope);
        Assert.Equal(hobbiesScope, hobbiesMembership.Scope);
        Assert.NotSame(starterMembership, hobbiesMembership);
    }

    [Fact]
    public void BothTransitionsRejectForeignAccountAndModuleEvidence()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var foreign = EvidenceFor(ModuleNames.Social, subject: OtherSubject);
        var hobbies = evidence with { Scope = evidence.Scope with { Module = ModuleNames.Hobbies }, SocialOptIn = null };

        foreach (var other in new[] { foreign, hobbies })
        {
            var foreignActive = ActiveModuleMembership.Activate(other.Scope, other);
            var foreignInactive = new InactiveModuleMembership(other.Scope, 2);
            AssertDenied(ModuleLifecycleDenial.InvalidScope,
                () => ActiveModuleMembership.Activate(evidence.Scope, other));
            AssertDenied(ModuleLifecycleDenial.InvalidScope,
                () => ActiveModuleMembership.Activate(evidence.Scope, evidence, foreignActive));
            AssertDenied(ModuleLifecycleDenial.InvalidScope,
                () => ActiveModuleMembership.Reactivate(evidence.Scope, other, new InactiveModuleMembership(evidence.Scope, 2), 2));
            AssertDenied(ModuleLifecycleDenial.InvalidScope,
                () => ActiveModuleMembership.Reactivate(evidence.Scope, evidence, foreignInactive, 2));
            Assert.Equal(other.Scope, foreignActive.Scope);
            Assert.Equal(2, foreignInactive.Revision);
        }

        BothTransitionsReject(evidence with { Demographics = foreign.Demographics }, ModuleLifecycleDenial.IneligibleAge);
    }

    [Fact]
    public void ReactivationUsesAnInactiveFixtureAndRevalidatesEquivalentRetries()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var inactive = new InactiveModuleMembership(evidence.Scope, 4);
        var profile = PrivateModuleProfile.Create(evidence.Scope.AccountId, evidence.Scope.Module, Now.AddDays(-1));
        profile.SetDraftFields(new("Retained Name", "Private draft", "private"));
        var originalProfile = (profile.Id, profile.AccountId, profile.Module, profile.CreatedAtUtc,
            profile.DisplayName, profile.CanonicalDisplayName, profile.Bio, profile.Visibility);

        var active = ActiveModuleMembership.Reactivate(evidence.Scope, evidence, inactive, 4);

        Assert.Equal(evidence.Scope, active.Scope);
        Assert.Equal(5, active.Revision);
        Assert.Equal(4, inactive.Revision);
        Assert.Same(active, ActiveModuleMembership.Reactivate(evidence.Scope, evidence, active, 4));
        AssertDenied(ModuleLifecycleDenial.MissingCurrentPolicies,
            () => ActiveModuleMembership.Reactivate(evidence.Scope, evidence with { Receipts = [] }, active, 4));
        AssertDenied(ModuleLifecycleDenial.InvalidOptIn,
            () => ActiveModuleMembership.Reactivate(evidence.Scope, evidence with { SocialOptIn = false }, active, 4));
        Assert.Equal(originalProfile, (profile.Id, profile.AccountId, profile.Module, profile.CreatedAtUtc,
            profile.DisplayName, profile.CanonicalDisplayName, profile.Bio, profile.Visibility));
        Assert.Equal(5, active.Revision);
    }

    [Fact]
    public void ConflictingTransitionsLeaveTheSuppliedMembershipsUnchanged()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var scope = evidence.Scope;
        var inactive = new InactiveModuleMembership(scope, 4);
        var initial = ActiveModuleMembership.Activate(scope, evidence);
        var reactivated = ActiveModuleMembership.Reactivate(scope, evidence, inactive, 4);

        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Activate(scope, evidence, inactive));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Activate(scope, evidence, reactivated));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Reactivate(scope, evidence, null, 4));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Reactivate(scope, evidence, initial, 1));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Reactivate(scope, evidence, inactive, 3));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Reactivate(scope, evidence, reactivated, 3));
        AssertDenied(ModuleLifecycleDenial.ConflictingTransition, () => ActiveModuleMembership.Reactivate(scope, evidence, inactive, long.MaxValue));

        Assert.Equal(scope, inactive.Scope);
        Assert.Equal(4, inactive.Revision);
        Assert.Equal(1, initial.Revision);
        Assert.Equal(5, reactivated.Revision);
    }

    [Fact]
    public async Task ActiveMembershipOnlyExposesMembershipDataAndProtectedHomeStillDenies()
    {
        var evidence = EvidenceFor(ModuleNames.Social);
        var active = ActiveModuleMembership.Activate(evidence.Scope, evidence);
        var reactivated = ActiveModuleMembership.Reactivate(evidence.Scope, evidence,
            new InactiveModuleMembership(evidence.Scope, 2), 2);

        // There is no profile creation or Home, discovery, matching, or communication grant in either result.
        foreach (var result in new[] { active, reactivated })
        {
            Assert.Equal(new[] { nameof(ModuleMembership.Revision), nameof(ModuleMembership.Scope) },
                result.GetType().GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        }

        IPrivateModuleAccess home = new PrivateModuleAccess(new CompletedProfileReader());
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => home.RequireAsync(active.Scope.Module));
    }

    private static ModuleActivationEvidence EvidenceFor(string module, int age = 18, Guid? subject = null)
    {
        var account = Account.Create(subject ?? Subject, Now);
        var scope = new ModuleMembershipScope(account.Id, module);
        var document = new PolicyDocument("current", "https://example.test", "en");
        return new(scope, ModuleAuthorizationOutcome.Satisfied, account, account.SupabaseUserId,
            AccountDemographics.Create(account.Id, DateOnly.FromDateTime(Now.UtcDateTime).AddYears(-age), "other", Now),
            new(document, document, document),
            [
                AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, document.Version, Now.AddDays(-1)),
                AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, document.Version, Now.AddDays(-1))
            ],
            RestrictionsAllowActivation: true, SocialOptIn: module == ModuleNames.Social,
            EvidenceVersion, EvidenceVersion, Now);
    }

    private static void BothTransitionsReject(ModuleActivationEvidence evidence, ModuleLifecycleDenial denial,
        ModuleAuthorizationOutcome outcome = ModuleAuthorizationOutcome.Denied)
    {
        var inactive = new InactiveModuleMembership(evidence.Scope, 2);
        AssertDenied(denial, () => ActiveModuleMembership.Activate(evidence.Scope, evidence), outcome);
        AssertDenied(denial, () => ActiveModuleMembership.Reactivate(evidence.Scope, evidence, inactive, 2), outcome);
        Assert.Equal(evidence.Scope, inactive.Scope);
        Assert.Equal(2, inactive.Revision);
    }

    private static void AssertDenied(ModuleLifecycleDenial denial, Func<ActiveModuleMembership> transition,
        ModuleAuthorizationOutcome outcome = ModuleAuthorizationOutcome.Denied)
    {
        var exception = Assert.Throws<ModuleLifecycleException>(() => { _ = transition(); });
        Assert.Equal(denial, exception.Denial);
        Assert.Equal(outcome, exception.Outcome);
    }

    private sealed class CompletedProfileReader : IPrivateProfileCompletionReader
    {
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProfileCompletionStatus(null, IsComplete: true));
    }
}
