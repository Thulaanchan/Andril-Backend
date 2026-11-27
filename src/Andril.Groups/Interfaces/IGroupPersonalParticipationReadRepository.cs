using System.Text.Json.Serialization;
using Andril.Groups.Entities;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;

namespace Andril.Groups.Interfaces;

public enum GroupPersonalParticipationDecision { Unknown, Denied, Allowed }

/// <summary>Whether the authoritative read found the caller's current record.</summary>
public enum GroupPersonalParticipationPresence { Unknown, Absent, Present }

/// <summary>Trusted caller and exact target for a personal participation read.</summary>
public sealed record GroupPersonalParticipationQuery(
    [property: JsonIgnore] Guid ActorProfileId,
    [property: JsonIgnore] Guid GroupId,
    [property: JsonIgnore] GroupModuleScope Scope)
{
    public override string ToString() => "Personal Group participation query [private]";
}

/// <summary>
/// An explicit observation bound to the snapshot revision, including when absence was verified.
/// Present requires the exact current entity. Absent requires a complete authoritative read and a
/// null entity. Unknown and undefined values convey no reliable participation information.
/// </summary>
public sealed record GroupPersonalParticipationObservation<T>(
    [property: JsonIgnore] GroupPersonalParticipationPresence Presence,
    [property: JsonIgnore] T? Entity,
    [property: JsonIgnore] string ReadRevision) where T : class
{
    public override string ToString() => "Personal Group participation observation [private]";
}

/// <summary>
/// Immutable owner-bound evidence for one group in one module. Visibility, access and blocking
/// decisions must each explicitly allow release for the caller. The read revision binds those
/// decisions, the target and both observations to one consistent authoritative read.
/// </summary>
public sealed record GroupPersonalParticipationSnapshot(
    [property: JsonIgnore] Guid ActorProfileId,
    [property: JsonIgnore] Guid GroupId,
    [property: JsonIgnore] GroupModuleScope Scope,
    [property: JsonIgnore] GroupPersonalParticipationDecision Visibility,
    [property: JsonIgnore] GroupPersonalParticipationDecision Access,
    [property: JsonIgnore] GroupPersonalParticipationDecision BlockingRestrictions,
    [property: JsonIgnore] DateTimeOffset EvidenceTimestampUtc,
    [property: JsonIgnore] string ReadRevision,
    [property: JsonIgnore] GroupPersonalParticipationObservation<GroupMembership> Membership,
    [property: JsonIgnore] GroupPersonalParticipationObservation<GroupJoinRequest> OutstandingJoinRequest)
{
    public override string ToString() => "Personal Group participation snapshot [private]";
}

/// <summary>
/// Reads only the exact caller's membership and outstanding join request. Verify authenticated
/// ownership, current standing, module isolation, Social adult eligibility and current access to
/// the target. A historical request supplies no evidence of an outstanding request. Missing or
/// unavailable sources supply no evidence of absence.
/// </summary>
public interface IGroupPersonalParticipationReadRepository
{
    /// <summary>
    /// Hold an authoritative read boundary through release. Invoke the projection exactly once
    /// when a consistent authorized snapshot is available and release only its exact result.
    /// Denied, revoked, changed or unknown evidence suppresses release. Cancellation or exceptions
    /// cannot release partial results.
    /// </summary>
    Task<T?> WithAuthorizedReadAsync<T>(GroupPersonalParticipationQuery query,
        Func<GroupPersonalParticipationSnapshot, CancellationToken, Task<T?>> project,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Within the same read boundary, verify the exact query and snapshot revision against
    /// current identity, authorization decisions and both observations, including any absence.
    /// The outer boundary remains responsible for suppressing changes through release.
    /// </summary>
    Task<GroupPersonalParticipationDecision> RevalidateAsync(GroupPersonalParticipationQuery query,
        GroupPersonalParticipationSnapshot snapshot, CancellationToken cancellationToken = default);
}
