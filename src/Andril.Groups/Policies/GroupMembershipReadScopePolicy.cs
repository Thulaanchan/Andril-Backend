using System;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;

namespace Andril.Groups.Policies;

/// <summary>
/// Status outcome of validating a retrieved group membership record against requested group, scope, and member profile boundaries.
/// </summary>
public enum GroupMembershipReadScopeStatus
{
    /// <summary>Returned group membership record strictly belongs to the requested group, module scope, and member profile.</summary>
    Valid,

    /// <summary>No group membership was returned (valid absent membership state / non-member).</summary>
    NullResult,

    /// <summary>Returned membership group identifier does not match the requested group identifier.</summary>
    GroupIdMismatch,

    /// <summary>Returned membership module scope does not match the requested group module scope.</summary>
    ScopeMismatch,

    /// <summary>Returned membership profile identifier does not match the requested member profile identifier.</summary>
    MemberProfileIdMismatch,

    /// <summary>One or more requested group or member profile identifiers are empty or malformed.</summary>
    InvalidIdentity,

    /// <summary>Group entry or requested module scope is null.</summary>
    NullInput
}

/// <summary>
/// Immutable, privacy-safe result of group membership read scope validation.
/// </summary>
public sealed class GroupMembershipReadScopeValidation : IEquatable<GroupMembershipReadScopeValidation>
{
    /// <summary>Gets the scope validation status outcome.</summary>
    public GroupMembershipReadScopeStatus Status { get; }

    /// <summary>
    /// Gets a value indicating whether the read result is scope-valid (either a matching membership or a valid null result).
    /// </summary>
    public bool IsValid => Status == GroupMembershipReadScopeStatus.Valid || Status == GroupMembershipReadScopeStatus.NullResult;

    /// <summary>
    /// Gets a value indicating whether an active membership was verified within scope.
    /// </summary>
    public bool HasMembership => Status == GroupMembershipReadScopeStatus.Valid;

    private GroupMembershipReadScopeValidation(GroupMembershipReadScopeStatus status)
    {
        Status = status;
    }

    /// <summary>Creates a successful scope validation result where a matching membership is present.</summary>
    public static GroupMembershipReadScopeValidation Success() =>
        new(GroupMembershipReadScopeStatus.Valid);

    /// <summary>Creates a successful scope validation result where no membership was found (null result).</summary>
    public static GroupMembershipReadScopeValidation None() =>
        new(GroupMembershipReadScopeStatus.NullResult);

    /// <summary>Creates a failed scope validation result with the specified error status.</summary>
    public static GroupMembershipReadScopeValidation Fail(GroupMembershipReadScopeStatus status) =>
        new(status);

    public bool Equals(GroupMembershipReadScopeValidation? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status;
    }

    public override bool Equals(object? obj) =>
        obj is GroupMembershipReadScopeValidation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status);

    public override string ToString() => "GroupMembershipReadScopeValidation [private]";
}

/// <summary>
/// Enforces boundary verification and scope compatibility for retrieved group memberships (CF-07).
/// Validates that returned membership records strictly match requested group identity, module scope, and profile identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This policy performs pure result-shape and boundary validation. It does NOT:
/// <list type="bullet">
/// <item><description>Grant group access permissions or assign moderator/admin roles.</description></item>
/// <item><description>Authenticate caller session identity or verify caller profile ownership.</description></item>
/// <item><description>Bypass member admission criteria or zone moderation controls.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class GroupMembershipReadScopePolicy
{
    /// <summary>
    /// Validates that a retrieved group membership strictly matches the requested group identifier, module scope, and member profile identifier.
    /// </summary>
    /// <param name="requestedGroupId">The requested group identifier.</param>
    /// <param name="requestedScope">The requested group module scope.</param>
    /// <param name="requestedMemberProfileId">The requested member profile identifier.</param>
    /// <param name="membership">The retrieved group membership, or <see langword="null"/> if not found.</param>
    /// <returns>A deterministic <see cref="GroupMembershipReadScopeValidation"/>.</returns>
    public static GroupMembershipReadScopeValidation Validate(
        Guid requestedGroupId,
        GroupModuleScope? requestedScope,
        Guid requestedMemberProfileId,
        GroupMembership? membership)
    {
        if (requestedGroupId == Guid.Empty || requestedMemberProfileId == Guid.Empty)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.InvalidIdentity);
        }

        if (requestedScope is null)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.NullInput);
        }

        if (membership is null)
        {
            return GroupMembershipReadScopeValidation.None();
        }

        if (membership.GroupId == Guid.Empty || membership.MemberProfileId == Guid.Empty)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.InvalidIdentity);
        }

        if (membership.GroupId != requestedGroupId)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.GroupIdMismatch);
        }

        if (membership.MemberProfileId != requestedMemberProfileId)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.MemberProfileIdMismatch);
        }

        if (membership.Scope is null || !GroupMembershipScopePolicy.IsCompatible(requestedScope, membership.Scope))
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.ScopeMismatch);
        }

        return GroupMembershipReadScopeValidation.Success();
    }

    /// <summary>
    /// Validates that a retrieved group membership strictly matches the target group entry and member profile identifier.
    /// </summary>
    /// <param name="requestedGroup">The target group entry.</param>
    /// <param name="requestedMemberProfileId">The requested member profile identifier.</param>
    /// <param name="membership">The retrieved group membership, or <see langword="null"/> if not found.</param>
    /// <returns>A deterministic <see cref="GroupMembershipReadScopeValidation"/>.</returns>
    public static GroupMembershipReadScopeValidation Validate(
        GroupEntry? requestedGroup,
        Guid requestedMemberProfileId,
        GroupMembership? membership)
    {
        if (requestedGroup is null)
        {
            return GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.NullInput);
        }

        return Validate(requestedGroup.Id, requestedGroup.Scope, requestedMemberProfileId, membership);
    }

    /// <summary>
    /// Determines whether a retrieved group membership is scope-compatible with the requested group identifier, scope, and member profile identifier.
    /// </summary>
    public static bool IsCompatible(
        Guid requestedGroupId,
        GroupModuleScope? requestedScope,
        Guid requestedMemberProfileId,
        GroupMembership? membership) =>
        Validate(requestedGroupId, requestedScope, requestedMemberProfileId, membership).IsValid;

    /// <summary>
    /// Determines whether a retrieved group membership is scope-compatible with the requested group entry and member profile identifier.
    /// </summary>
    public static bool IsCompatible(
        GroupEntry? requestedGroup,
        Guid requestedMemberProfileId,
        GroupMembership? membership) =>
        Validate(requestedGroup, requestedMemberProfileId, membership).IsValid;
}
