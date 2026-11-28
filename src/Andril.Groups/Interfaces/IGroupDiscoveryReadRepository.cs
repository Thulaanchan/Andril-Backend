using System.Text.Json.Serialization;
using Andril.Groups.Entities;

namespace Andril.Groups.Interfaces;

public enum GroupDiscoveryDecision { Unknown, Denied, Allowed }
public enum GroupDiscoveryLifecycle { Unknown, Active, Inactive, Closed }
public enum GroupDiscoveryPrivacy { Unknown, Public, Private }

/// <summary>Server-only bounded request. Identity must originate from trusted actor authorization.</summary>
public sealed record GroupDiscoveryQuery(
    [property: JsonIgnore] Guid ActorProfileId,
    [property: JsonIgnore] GroupModuleScope Scope,
    [property: JsonIgnore] string SearchText,
    [property: JsonIgnore] int MaximumCandidates)
{
    public override string ToString() => "Group discovery query [private]";
}

/// <summary>
/// Explicit decisions for this exact actor and scope, never inferred from GroupEntry.
/// MembershipRestrictions means permission to discover, not proof of membership.
/// Private groups require separate positive PrivateDiscoveryAuthorization.
/// Unknown, unavailable and undefined values grant no permission. Revision must bind
/// group content and every lifecycle/visibility/membership/block decision for release verification.
/// </summary>
public sealed record GroupDiscoveryEvidence(
    [property: JsonIgnore] GroupEntry Group,
    [property: JsonIgnore] GroupDiscoveryLifecycle Lifecycle,
    [property: JsonIgnore] GroupDiscoveryDecision Discoverability,
    [property: JsonIgnore] GroupDiscoveryPrivacy Privacy,
    [property: JsonIgnore] GroupDiscoveryDecision PrivateDiscoveryAuthorization,
    [property: JsonIgnore] GroupDiscoveryDecision Visibility,
    [property: JsonIgnore] GroupDiscoveryDecision MembershipRestrictions,
    [property: JsonIgnore] GroupDiscoveryDecision BlockingRestrictions,
    [property: JsonIgnore] string Revision,
    [property: JsonIgnore] DateTimeOffset EvidenceTimestampUtc)
{
    public override string ToString() => "Group discovery evidence [private]";
}

/// <summary>Immutable collection required; binds all rows to one exact authorized query.</summary>
public sealed record GroupDiscoverySnapshot(
    [property: JsonIgnore] Guid ActorProfileId,
    [property: JsonIgnore] GroupModuleScope Scope,
    [property: JsonIgnore] IReadOnlyList<GroupDiscoveryEvidence> Groups,
    [property: JsonIgnore] string AuthorizationRevision)
{
    public override string ToString() => "Group discovery snapshot [private]";
}

/// <summary>
/// Internal backend read/release abstraction, not a public HTTP contract or production provider.
/// Implementations authenticate/authorize the bound actor, search only the exact module/kind,
/// apply explicit technical bounds at the data source and supply immutable evidence.
/// No unscoped enumeration, default visibility, entitlement inference or membership grants.
/// Keep an authoritative transaction/fence through projection and revalidate current caller,
/// content, lifecycle, discoverability, visibility, membership and blocks before releasing its
/// result. Revocation, changed content or unknown evidence suppresses the whole callback result.
/// Exceptions/cancellation must not release partial results. A standalone preflight check
/// followed by an unfenced read is not an implementation of this contract.
/// </summary>
public interface IGroupDiscoveryReadRepository
{
    Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
        Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Recheck this exact snapshot/query within the same read/release boundary.
    /// Allowed requires current actor identity, all evidence revisions and projected content
    /// to remain authorized; denial/unknown must not be converted to an empty authorized result.
    /// The outer boundary must still fence release after this check and callback completion.
    /// </summary>
    Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query,
        GroupDiscoverySnapshot snapshot, CancellationToken cancellationToken = default);
}
