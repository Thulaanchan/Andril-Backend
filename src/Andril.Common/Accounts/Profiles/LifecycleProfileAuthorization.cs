using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Shared.Authentication;

namespace Andril.Common.Accounts.Profiles;

internal enum PrivateProfileOperation { Create, Read, Edit, ReadMedia, WriteMedia }

internal sealed record PrivateProfilePurposeEvidence(
    ModuleMembershipScope Scope, Guid? ProfileId, PrivateProfileOperation Operation,
    long MembershipRevision, Guid EvidenceVersion, ModuleAuthorizationOutcome Outcome);

internal sealed record LifecycleProfileEvidence(
    ModuleActivationEvidence Eligibility, ModuleMembership? Membership, bool? AccountStandingAllowsAccess,
    PrivateProfilePurposeEvidence Purpose, DateTimeOffset ValidUntilUtc);

// The caller must supply a trusted snapshot scoped to the operation, including the current evidence
// version, the operation's privacy and revocation decision, and an authority-issued expiry.
// A satisfied decision is for this use only. It is not a capability to cache or pass to Home.
internal sealed class LifecycleProfileAuthorization(ICurrentUser user, IOnboardingIdentity identity, TimeProvider clock)
{
    internal ModuleAuthorizationOutcome Decide(ModuleMembershipScope scope, PrivateProfileOperation operation,
        PrivateModuleProfile? profile, LifecycleProfileEvidence evidence)
    {
        if (!user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty ||
            !identity.IsConfirmedFor(subject) || !Enum.IsDefined(operation) ||
            evidence.Eligibility.Scope != scope || evidence.Purpose.Scope != scope ||
            evidence.Purpose.Operation != operation)
            return ModuleAuthorizationOutcome.Denied;

        if (evidence.Eligibility.SourceOutcome == ModuleAuthorizationOutcome.Satisfied &&
            evidence.Eligibility.ConfirmedSubject != subject)
            return ModuleAuthorizationOutcome.Denied;

        var now = clock.GetUtcNow();
        if (evidence.Eligibility.EvaluatedAtUtc == DateTimeOffset.MinValue ||
            evidence.Eligibility.EvaluatedAtUtc > now || evidence.ValidUntilUtc <= now ||
            evidence.ValidUntilUtc == DateTimeOffset.MaxValue)
            return ModuleAuthorizationOutcome.Unavailable;

        try
        {
            ActiveModuleMembership.ValidatePrerequisites(scope, evidence.Eligibility, evidence.Membership,
                evidence.AccountStandingAllowsAccess);
        }
        catch (ModuleLifecycleException error)
        {
            return error.Outcome;
        }

        if (evidence.Membership is not ActiveModuleMembership { Revision: > 0 } membership ||
            evidence.Purpose.MembershipRevision != membership.Revision ||
            evidence.Purpose.EvidenceVersion != evidence.Eligibility.CurrentEvidenceVersion ||
            (profile is null ? operation != PrivateProfileOperation.Create :
                profile.AccountId != scope.AccountId || profile.Module != scope.Module || profile.Id == Guid.Empty) ||
            evidence.Purpose.ProfileId != profile?.Id)
            return ModuleAuthorizationOutcome.Denied;

        // Undefined outcomes fail closed even if a caller constructs malformed evidence.
        return evidence.Purpose.Outcome switch
        {
            ModuleAuthorizationOutcome.Satisfied => ModuleAuthorizationOutcome.Satisfied,
            ModuleAuthorizationOutcome.Denied => ModuleAuthorizationOutcome.Denied,
            _ => ModuleAuthorizationOutcome.Unavailable
        };
    }
}
