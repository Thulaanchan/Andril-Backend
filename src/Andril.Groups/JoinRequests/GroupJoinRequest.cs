using System;
using System.Text.Json.Serialization;
using Andril.Groups.Entities;

namespace Andril.Groups.JoinRequests;

/// <summary>
/// Immutable representation of a private module-profile's join request identity in a module-scoped group.
/// Does not grant access permissions, manage request lifecycle approval/rejection states, or authenticate callers.
/// No implicit membership grant.
/// </summary>
public sealed class GroupJoinRequest : IEquatable<GroupJoinRequest>
{
    /// <summary>
    /// Gets the unique group identifier.
    /// </summary>
    [JsonIgnore]
    public Guid GroupId { get; }

    /// <summary>
    /// Gets the validated module and kind scope of the target group.
    /// </summary>
    public GroupModuleScope Scope { get; }

    /// <summary>
    /// Gets the private module-profile identifier of the requesting profile.
    /// </summary>
    [JsonIgnore]
    public Guid RequestingProfileId { get; }

    /// <summary>
    /// Gets the UTC timestamp at which the join request was recorded.
    /// </summary>
    [JsonIgnore]
    public DateTime RequestedAtUtc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupJoinRequest"/> class with an explicit UTC timestamp.
    /// </summary>
    public GroupJoinRequest(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, DateTime requestedAtUtc)
    {
        if (groupId == Guid.Empty)
        {
            throw new ArgumentException("Group identifier is required.", nameof(groupId));
        }

        Scope = scope ?? throw new ArgumentNullException(nameof(scope));

        if (requestingProfileId == Guid.Empty)
        {
            throw new ArgumentException("Requesting profile identifier is required.", nameof(requestingProfileId));
        }

        if (requestedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Requested timestamp must have UTC DateTimeKind.", nameof(requestedAtUtc));
        }

        if (requestedAtUtc == default || requestedAtUtc == DateTime.MinValue || requestedAtUtc == DateTime.MaxValue)
        {
            throw new ArgumentException("Requested timestamp must be a valid UTC timestamp.", nameof(requestedAtUtc));
        }

        GroupId = groupId;
        RequestingProfileId = requestingProfileId;
        RequestedAtUtc = requestedAtUtc;
    }

    /// <summary>
    /// Creates a validated <see cref="GroupJoinRequest"/> instance with an explicit UTC timestamp.
    /// </summary>
    public static GroupJoinRequest Create(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, DateTime requestedAtUtc) =>
        new(groupId, scope, requestingProfileId, requestedAtUtc);

    /// <summary>
    /// Creates a validated <see cref="GroupJoinRequest"/> instance stamped with current UTC time.
    /// </summary>
    public static GroupJoinRequest Create(Guid groupId, GroupModuleScope scope, Guid requestingProfileId) =>
        new(groupId, scope, requestingProfileId, DateTime.UtcNow);

    /// <summary>
    /// Determines whether this instance and another specified <see cref="GroupJoinRequest"/> have the same structural values.
    /// </summary>
    public bool Equals(GroupJoinRequest? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return GroupId == other.GroupId
            && Scope.Equals(other.Scope)
            && RequestingProfileId == other.RequestingProfileId
            && RequestedAtUtc == other.RequestedAtUtc;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is GroupJoinRequest other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode() =>
        HashCode.Combine(GroupId, Scope, RequestingProfileId, RequestedAtUtc);

    /// <summary>
    /// Compares two <see cref="GroupJoinRequest"/> instances for equality.
    /// </summary>
    public static bool operator ==(GroupJoinRequest? left, GroupJoinRequest? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="GroupJoinRequest"/> instances for inequality.
    /// </summary>
    public static bool operator !=(GroupJoinRequest? left, GroupJoinRequest? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential identifiers.
    /// </summary>
    public override string ToString() =>
        $"GroupJoinRequest [{Scope.Module}:{Scope.GroupKind}]";
}
