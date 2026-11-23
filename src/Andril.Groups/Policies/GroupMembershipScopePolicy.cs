using System;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;

namespace Andril.Groups.Policies;

/// <summary>
/// Enforces module scope compatibility and boundary validation between groups and group memberships.
/// Validates structural scope compatibility only. Does not authenticate callers, authorize group access,
/// or grant member permissions.
/// </summary>
public static class GroupMembershipScopePolicy
{
    /// <summary>
    /// Determines whether the specified group membership is structurally compatible with the target group entry.
    /// Fails closed (returns <c>false</c>) if either reference is null, identifiers are empty,
    /// group IDs do not match, or module scopes are inconsistent.
    /// </summary>
    /// <param name="group">The group entry to validate against.</param>
    /// <param name="membership">The group membership to validate.</param>
    /// <returns><c>true</c> if the membership strictly and compatibly matches the group scope; otherwise, <c>false</c>.</returns>
    public static bool IsCompatible(GroupEntry? group, GroupMembership? membership)
    {
        if (group is null || membership is null)
        {
            return false;
        }

        if (group.Id == Guid.Empty || membership.GroupId == Guid.Empty || membership.MemberProfileId == Guid.Empty)
        {
            return false;
        }

        if (group.Id != membership.GroupId)
        {
            return false;
        }

        return IsCompatible(group.Scope, membership.Scope);
    }

    /// <summary>
    /// Determines whether the specified group membership is structurally compatible with the target group ID and scope.
    /// Fails closed if parameters are null, identifiers are empty, group IDs do not match, or scopes are inconsistent.
    /// </summary>
    /// <param name="groupId">The expected group identifier.</param>
    /// <param name="expectedScope">The expected group module scope.</param>
    /// <param name="membership">The group membership to validate.</param>
    /// <returns><c>true</c> if compatible; otherwise, <c>false</c>.</returns>
    public static bool IsCompatible(Guid groupId, GroupModuleScope? expectedScope, GroupMembership? membership)
    {
        if (groupId == Guid.Empty || expectedScope is null || membership is null)
        {
            return false;
        }

        if (membership.GroupId == Guid.Empty || membership.MemberProfileId == Guid.Empty)
        {
            return false;
        }

        if (groupId != membership.GroupId)
        {
            return false;
        }

        return IsCompatible(expectedScope, membership.Scope);
    }

    /// <summary>
    /// Determines whether two group module scopes are non-null, canonically compatible, and equal.
    /// Fails closed if either scope is null, not approved, or mismatched.
    /// </summary>
    /// <param name="groupScope">The group module scope.</param>
    /// <param name="membershipScope">The membership module scope.</param>
    /// <returns><c>true</c> if both scopes are valid and equal; otherwise, <c>false</c>.</returns>
    public static bool IsCompatible(GroupModuleScope? groupScope, GroupModuleScope? membershipScope)
    {
        if (groupScope is null || membershipScope is null)
        {
            return false;
        }

        if (!GroupModuleScopePolicy.IsCompatible(groupScope.Module, groupScope.GroupKind))
        {
            return false;
        }

        if (!GroupModuleScopePolicy.IsCompatible(membershipScope.Module, membershipScope.GroupKind))
        {
            return false;
        }

        return groupScope.Equals(membershipScope);
    }

    /// <summary>
    /// Validates that the specified group membership matches the target group entry, throwing an exception if invalid.
    /// </summary>
    /// <param name="group">The group entry to validate against.</param>
    /// <param name="membership">The group membership to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="group"/> or <paramref name="membership"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when group IDs do not match, identifiers are empty, or scopes are incompatible.</exception>
    public static void Validate(GroupEntry group, GroupMembership membership)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(membership);

        if (group.Id == Guid.Empty)
        {
            throw new ArgumentException("Group identifier must not be empty.", nameof(group));
        }

        if (membership.GroupId == Guid.Empty)
        {
            throw new ArgumentException("Membership group identifier must not be empty.", nameof(membership));
        }

        if (membership.MemberProfileId == Guid.Empty)
        {
            throw new ArgumentException("Membership member profile identifier must not be empty.", nameof(membership));
        }

        if (group.Id != membership.GroupId)
        {
            throw new ArgumentException(
                "Membership group identifier does not match target group identifier.",
                nameof(membership));
        }

        if (!IsCompatible(group.Scope, membership.Scope))
        {
            throw new ArgumentException(
                "Membership module scope is incompatible with target group scope.",
                nameof(membership));
        }
    }
}
