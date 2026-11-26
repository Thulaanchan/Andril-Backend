namespace Andril.Learning.Discovery;

/// <summary>New TC-02 Learning-specific evidence contracts; unknown is never positive authority.</summary>
public enum LearningDiscoveryDecision { Unknown, Denied, Allowed }
public enum LearningDiscoveryLifecycle { Unknown, Active, Inactive }

/// <summary>Exact server-owned content plus its AI disclosure decision, not client-provided permission.</summary>
public sealed record LearningPartnerField(string Text, LearningDiscoveryDecision AiDisclosure)
{
    public override string ToString() => "Learning discovery field [untrusted text omitted]";
}

public sealed record LearningPartnerEvidence(Guid CandidateLearningProfileId, string Module,
    LearningDiscoveryLifecycle Lifecycle, LearningDiscoveryDecision Eligibility,
    LearningDiscoveryDecision Visibility, LearningDiscoveryDecision Blocking,
    LearningPartnerField? DisplayName, LearningPartnerField? Interests, LearningPartnerField? Skills,
    LearningPartnerField? Goals, LearningPartnerField? ProfessionalBiography)
{
    public override string ToString() => "Learning partner evidence [private bindings and fields omitted]";
}

public sealed record LearningPartnerDiscoverySnapshot(Guid AccountId, Guid LearningProfileId,
    IReadOnlyList<LearningPartnerEvidence> Candidates)
{
    public override string ToString() => "Learning partner snapshot [private content omitted]";
}

/// <summary>
/// New Learning authority/release boundary. Implementations must establish current requester and
/// candidate Learning lifecycle, eligibility, discoverability, applicable blocking and privacy.
/// Each field decision applies to the exact content, requester and AI processing purpose.
/// Content/classification and visibility changes must be coordinated before callback release.
/// No Social/Dating policy, generic profile lookup or client flag is substitute evidence.
/// No connection-request/write capability is exposed. No paging/ranking/entitlement rule is invented.
/// </summary>
public interface ILearningPartnerDiscoveryReader
{
    Task<T?> WithAuthorizedSearchAsync<T>(Guid accountId, Guid learningProfileId, string query,
        Func<LearningPartnerDiscoverySnapshot, CancellationToken, Task<T>> project,
        CancellationToken cancellationToken = default) where T : class;
}
