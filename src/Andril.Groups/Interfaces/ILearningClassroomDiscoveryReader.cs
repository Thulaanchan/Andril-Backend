using Andril.Groups.Entities;

namespace Andril.Groups.Interfaces;

/// <summary>New cross-member TD-01 evidence contracts, not previously implemented source predicates.</summary>
public enum ClassroomDiscoveryDecision { Unknown, Denied, Allowed }
public enum ClassroomDiscoveryLifecycle { Unknown, Active, Inactive, Closed }

/// <summary>
/// Server-only evidence for one exact requester. GroupEntry supplies only ID, scope and Name.
/// No client DTO may construct authoritative evidence. Unknown decisions fail closed.
/// NameAiDisclosure is separate from ordinary discovery authorization.
/// </summary>
public sealed record LearningClassroomDiscoveryEvidence(
    GroupEntry Group,
    ClassroomDiscoveryLifecycle Lifecycle,
    ClassroomDiscoveryDecision Discoverability,
    bool IsPrivate,
    ClassroomDiscoveryDecision PrivateDiscoveryAuthorization,
    ClassroomDiscoveryDecision Visibility,
    ClassroomDiscoveryDecision MembershipRestrictions,
    ClassroomDiscoveryDecision BlockingRestrictions,
    ClassroomDiscoveryDecision NameAiDisclosure)
{
    public override string ToString() => "Classroom discovery evidence [private fields omitted]";
}

public sealed record LearningClassroomDiscoverySnapshot(Guid AccountId, Guid LearningProfileId,
    IReadOnlyList<LearningClassroomDiscoveryEvidence> Classrooms)
{
    public override string ToString() => "Classroom discovery snapshot [private bindings omitted]";
}

/// <summary>
/// M5-owned authoritative search/read-release boundary. Implementations must establish current
/// Learning owner eligibility, lifecycle, visibility, membership and block evidence and search
/// only permitted Learning Classrooms. They must revalidate that evidence before releasing the
/// callback result. Unknown/unavailable evidence returns null or throws an availability error.
/// Existing generic group lookup is NOT an implementation of this contract.
/// This interface grants no membership/write authority and defines no eligibility business rule.
/// </summary>
public interface ILearningClassroomDiscoveryReader
{
    Task<T?> WithAuthorizedSearchAsync<T>(Guid accountId, Guid learningProfileId, string query,
        Func<LearningClassroomDiscoverySnapshot, CancellationToken, Task<T>> project,
        CancellationToken cancellationToken = default) where T : class;
}
