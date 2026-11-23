using System.Runtime.CompilerServices;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

[assembly: InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.Common.Accounts.Lifecycle;

internal readonly record struct ModuleMembershipScope(Guid AccountId, string Module);

// Each version represents the whole authorization snapshot for this scope.
// A trusted caller supplies current evidence. This model does not fetch or synchronize it.
internal sealed record ModuleActivationEvidence(
    ModuleMembershipScope Scope,
    Account? Account,
    Guid? ConfirmedSubject,
    AccountDemographics? Demographics,
    PolicyCatalog CurrentPolicies,
    IReadOnlyList<AccountPolicyRecord> Receipts,
    bool? RestrictionsAllowActivation,
    bool? SocialOptIn,
    Guid ObservedEvidenceVersion,
    Guid CurrentEvidenceVersion,
    DateTimeOffset EvaluatedAtUtc);

internal abstract class ModuleMembership(ModuleMembershipScope scope, long revision)
{
    public ModuleMembershipScope Scope { get; } = scope;
    public long Revision { get; } = revision;
}

// Supplied by an offline fixture. The domain has no transition that produces this state.
internal sealed class InactiveModuleMembership(ModuleMembershipScope scope, long revision)
    : ModuleMembership(scope, revision);

internal sealed class ActiveModuleMembership : ModuleMembership
{
    private ActiveModuleMembership(ModuleMembershipScope scope, long revision) : base(scope, revision) { }

    internal static ActiveModuleMembership Activate(ModuleMembershipScope scope, ModuleActivationEvidence evidence,
        ModuleMembership? current = null)
    {
        Validate(scope, evidence, current);
        return current switch
        {
            null => new(scope, 1),
            ActiveModuleMembership { Revision: 1 } active => active,
            _ => throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition)
        };
    }

    internal static ActiveModuleMembership Reactivate(ModuleMembershipScope scope, ModuleActivationEvidence evidence,
        ModuleMembership? current, long expectedRevision)
    {
        Validate(scope, evidence, current);
        if (expectedRevision is <= 0 or long.MaxValue)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);

        return current switch
        {
            InactiveModuleMembership inactive when inactive.Revision == expectedRevision => new(scope, expectedRevision + 1),
            ActiveModuleMembership active when active.Revision == expectedRevision + 1 => active,
            _ => throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition)
        };
    }

    private static void Validate(ModuleMembershipScope scope, ModuleActivationEvidence evidence, ModuleMembership? current)
    {
        if (scope.AccountId == Guid.Empty || !ModuleNames.IsValid(scope.Module)
            || evidence.Scope != scope || (current is not null && current.Scope != scope))
            throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidScope);

        if (evidence.ObservedEvidenceVersion == Guid.Empty || evidence.ObservedEvidenceVersion != evidence.CurrentEvidenceVersion)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.StaleEvidence);

        if (evidence.Account is not { } account || account.Id != scope.AccountId
            || account.SupabaseUserId == Guid.Empty || account.SupabaseUserId != evidence.ConfirmedSubject)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.UnconfirmedAccount);

        if (evidence.RestrictionsAllowActivation != true)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.AccountRestricted);

        if (scope.Module == ModuleNames.Social ? evidence.SocialOptIn != true : evidence.SocialOptIn == true)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidOptIn);

        if (evidence.Demographics is not { } demographics || demographics.AccountId != scope.AccountId)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.IneligibleAge);

        ModuleAgeEligibility eligibility;
        try
        {
            eligibility = AgeEligibilityPolicy.Evaluate(demographics.DateOfBirth,
                DateOnly.FromDateTime(evidence.EvaluatedAtUtc.UtcDateTime));
        }
        catch (EligibilityValidationException)
        {
            throw new ModuleLifecycleException(ModuleLifecycleDenial.IneligibleAge);
        }

        var ageEligible = scope.Module switch
        {
            ModuleNames.Learning => eligibility.Learning,
            ModuleNames.Hobbies => eligibility.Hobbies,
            ModuleNames.Social => eligibility.Social,
            _ => false
        };
        if (!ageEligible)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.IneligibleAge);

        if (!HasReceipt(PolicyActions.TermsAgreed, evidence.CurrentPolicies.Terms.Version)
            || !HasReceipt(PolicyActions.PrivacyAcknowledged, evidence.CurrentPolicies.Privacy.Version))
            throw new ModuleLifecycleException(ModuleLifecycleDenial.MissingCurrentPolicies);

        bool HasReceipt(string action, string version) => AccountPolicyRecord.ValidVersion(version)
            && evidence.Receipts.Any(receipt => receipt.AccountId == scope.AccountId && receipt.Action == action
                && receipt.Version == version && receipt.RecordedAtUtc <= evidence.EvaluatedAtUtc);
    }
}

internal enum ModuleLifecycleDenial
{
    InvalidScope,
    StaleEvidence,
    UnconfirmedAccount,
    AccountRestricted,
    InvalidOptIn,
    IneligibleAge,
    MissingCurrentPolicies,
    ConflictingTransition
}

internal sealed class ModuleLifecycleException(ModuleLifecycleDenial denial) : Exception
{
    public ModuleLifecycleDenial Denial { get; } = denial;
}
