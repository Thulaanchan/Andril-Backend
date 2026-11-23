using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Lifecycle;

// Synthetic, operation-specific evidence for B1. Restricted/ineligible-owner policy
// and production freshness sources remain pending. Activation evidence is not used.
internal sealed record ModuleDeactivationEvidence(ModuleMembershipScope Scope, Guid ConfirmedSubject,
    ModuleAuthorizationOutcome Outcome, Guid ObservedVersion, Guid CurrentVersion, DateTimeOffset EvaluatedAtUtc);

internal static class ModuleDeactivation
{
    internal static void Validate(ModuleMembershipScope scope, Guid subject, ModuleDeactivationEvidence evidence,
        ModuleMembership? current)
    {
        if (scope.AccountId == Guid.Empty || !ModuleNames.IsValid(scope.Module) || evidence.Scope != scope
            || current is not null && current.Scope != scope)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.InvalidScope);
        if (subject == Guid.Empty || evidence.ConfirmedSubject != subject)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.UnconfirmedAccount);
        if (evidence.Outcome == ModuleAuthorizationOutcome.Denied)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.AuthorityDenied);
        if (evidence.Outcome != ModuleAuthorizationOutcome.Satisfied || evidence.CurrentVersion == Guid.Empty
            || evidence.EvaluatedAtUtc == DateTimeOffset.MinValue || evidence.EvaluatedAtUtc == DateTimeOffset.MaxValue)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.EvidenceUnavailable);
        if (evidence.ObservedVersion != evidence.CurrentVersion)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.StaleEvidence);
    }

    internal static InactiveModuleMembership Deactivate(ModuleMembershipScope scope, Guid subject,
        ModuleDeactivationEvidence evidence, ModuleMembership? current, long expectedRevision)
    {
        Validate(scope, subject, evidence, current);
        if (expectedRevision is <= 0 or long.MaxValue)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);
        if (current is null) throw new ModuleLifecycleException(ModuleLifecycleDenial.MembershipMissing);
        // Classification only. Product interpretation of an already-inactive request is pending.
        if (current is InactiveModuleMembership)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.AlreadyInactive);
        if (current is not ActiveModuleMembership || current.Revision != expectedRevision)
            throw new ModuleLifecycleException(ModuleLifecycleDenial.ConflictingTransition);
        return new(scope, checked(expectedRevision + 1));
    }
}
