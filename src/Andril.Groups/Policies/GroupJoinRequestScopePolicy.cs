using System;
using Andril.Groups.Entities;
using Andril.Groups.JoinRequests;

namespace Andril.Groups.Policies;

/// <summary>
/// Status outcome of validating a group join request against target group scope boundaries.
/// </summary>
public enum GroupJoinRequestScopeStatus
{
    /// <summary>Join request strictly matches target group identity and module scope.</summary>
    Valid,

    /// <summary>Target group identifier does not match join request group identifier.</summary>
    GroupIdMismatch,

    /// <summary>Module scope does not match between target group and join request.</summary>
    ScopeMismatch,

    /// <summary>Module name differs (cross-module join request application rejected).</summary>
    ModuleMismatch,

    /// <summary>Group kind differs (e.g. Classroom vs. Club vs. Zone mismatch).</summary>
    GroupKindMismatch,

    /// <summary>One or more group or profile identifiers are empty or malformed.</summary>
    InvalidIdentity,

    /// <summary>Group entry, target scope, or join request reference is null.</summary>
    NullInput
}

/// <summary>
/// Immutable, privacy-safe result of group join request scope validation.
/// </summary>
public sealed class GroupJoinRequestScopeValidation : IEquatable<GroupJoinRequestScopeValidation>
{
    /// <summary>Gets the scope validation status outcome.</summary>
    public GroupJoinRequestScopeStatus Status { get; }

    /// <summary>Gets a value indicating whether the join request is strictly scope-compatible.</summary>
    public bool IsValid => Status == GroupJoinRequestScopeStatus.Valid;

    private GroupJoinRequestScopeValidation(GroupJoinRequestScopeStatus status)
    {
        Status = status;
    }

    /// <summary>Creates a successful scope validation result.</summary>
    public static GroupJoinRequestScopeValidation Success() =>
        new(GroupJoinRequestScopeStatus.Valid);

    /// <summary>Creates a failed scope validation result with the specified error status.</summary>
    public static GroupJoinRequestScopeValidation Fail(GroupJoinRequestScopeStatus status) =>
        new(status);

    public bool Equals(GroupJoinRequestScopeValidation? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status;
    }

    public override bool Equals(object? obj) =>
        obj is GroupJoinRequestScopeValidation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status);

    public override string ToString() => "GroupJoinRequestScopeValidation [private]";
}

/// <summary>
/// Enforces module scope compatibility and boundary validation between target groups and group join requests (CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This policy validates structural identity and module scope compatibility only. It does NOT:
/// <list type="bullet">
/// <item><description>Grant group membership or assign member/moderator roles.</description></item>
/// <item><description>Approve, reject, or alter join request lifecycle states.</description></item>
/// <item><description>Enforce unapproved join quotas, admission rules, or Zone approval flows.</description></item>
/// <item><description>Authenticate caller identity or verify caller profile ownership.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class GroupJoinRequestScopePolicy
{
    /// <summary>
    /// Validates that a group join request strictly matches the target group entry's identity and module scope.
    /// </summary>
    /// <param name="group">The target group entry.</param>
    /// <param name="request">The group join request to validate.</param>
    /// <returns>A deterministic <see cref="GroupJoinRequestScopeValidation"/>.</returns>
    public static GroupJoinRequestScopeValidation Validate(GroupEntry? group, GroupJoinRequest? request)
    {
        if (group is null || request is null)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.NullInput);
        }

        return Validate(group.Id, group.Scope, request);
    }

    /// <summary>
    /// Validates that a group join request strictly matches the target group ID and module scope.
    /// </summary>
    /// <param name="targetGroupId">The target group identifier.</param>
    /// <param name="targetScope">The target group module scope.</param>
    /// <param name="request">The group join request to validate.</param>
    /// <returns>A deterministic <see cref="GroupJoinRequestScopeValidation"/>.</returns>
    public static GroupJoinRequestScopeValidation Validate(
        Guid targetGroupId,
        GroupModuleScope? targetScope,
        GroupJoinRequest? request)
    {
        if (targetScope is null || request is null)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.NullInput);
        }

        if (targetGroupId == Guid.Empty ||
            request.GroupId == Guid.Empty ||
            request.RequestingProfileId == Guid.Empty)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.InvalidIdentity);
        }

        if (targetGroupId != request.GroupId)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.GroupIdMismatch);
        }

        if (request.Scope is null)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.ScopeMismatch);
        }

        if (!string.Equals(targetScope.Module, request.Scope.Module, StringComparison.OrdinalIgnoreCase))
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.ModuleMismatch);
        }

        if (targetScope.GroupKind != request.Scope.GroupKind)
        {
            return GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.GroupKindMismatch);
        }

        return GroupJoinRequestScopeValidation.Success();
    }

    /// <summary>
    /// Determines whether the specified join request is scope-compatible with the target group entry.
    /// Fails closed (returns <c>false</c>) on any mismatch or null input.
    /// </summary>
    public static bool IsCompatible(GroupEntry? group, GroupJoinRequest? request) =>
        Validate(group, request).IsValid;

    /// <summary>
    /// Determines whether the specified join request is scope-compatible with the target group ID and scope.
    /// Fails closed (returns <c>false</c>) on any mismatch or null input.
    /// </summary>
    public static bool IsCompatible(Guid targetGroupId, GroupModuleScope? targetScope, GroupJoinRequest? request) =>
        Validate(targetGroupId, targetScope, request).IsValid;
}
