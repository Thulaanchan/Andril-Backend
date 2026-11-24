using System;
using System.Text.Json.Serialization;
using Andril.Groups.Entities;

namespace Andril.Groups.Memberships;

/// <summary>
/// Immutable representation of a private module-profile's membership identity in a module-scoped group.
/// Does not grant access permissions, manage role hierarchies, or authenticate callers.
/// </summary>
public sealed class GroupMembership : IEquatable<GroupMembership>
{
    /// <summary>
    /// Gets the unique group identifier.
    /// </summary>
    [JsonIgnore]
    public Guid GroupId { get; }

    /// <summary>
    /// Gets the validated module and kind scope of the group.
    /// </summary>
    public GroupModuleScope Scope { get; }

    /// <summary>
    /// Gets the private module-profile identifier of the member.
    /// </summary>
    [JsonIgnore]
    public Guid MemberProfileId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupMembership"/> class.
    /// </summary>
    public GroupMembership(Guid groupId, GroupModuleScope scope, Guid memberProfileId)
    {
        if (groupId == Guid.Empty)
        {
            throw new ArgumentException("Group identifier is required.", nameof(groupId));
        }

        Scope = scope ?? throw new ArgumentNullException(nameof(scope));

        if (memberProfileId == Guid.Empty)
        {
            throw new ArgumentException("Member profile identifier is required.", nameof(memberProfileId));
        }

        GroupId = groupId;
        MemberProfileId = memberProfileId;
    }

    /// <summary>
    /// Creates a validated <see cref="GroupMembership"/> instance.
    /// </summary>
    public static GroupMembership Create(Guid groupId, GroupModuleScope scope, Guid memberProfileId) =>
        new(groupId, scope, memberProfileId);

    /// <summary>
    /// Determines whether this instance and another specified <see cref="GroupMembership"/> have the same structural values.
    /// </summary>
    public bool Equals(GroupMembership? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return GroupId == other.GroupId
            && Scope.Equals(other.Scope)
            && MemberProfileId == other.MemberProfileId;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is GroupMembership other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode() =>
        HashCode.Combine(GroupId, Scope, MemberProfileId);

    /// <summary>
    /// Compares two <see cref="GroupMembership"/> instances for equality.
    /// </summary>
    public static bool operator ==(GroupMembership? left, GroupMembership? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="GroupMembership"/> instances for inequality.
    /// </summary>
    public static bool operator !=(GroupMembership? left, GroupMembership? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential identifiers.
    /// </summary>
    public override string ToString() =>
        $"GroupMembership [{Scope.Module}:{Scope.GroupKind}]";
}
