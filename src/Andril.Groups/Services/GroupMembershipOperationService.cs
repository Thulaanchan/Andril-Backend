using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>
/// Result of an authorized group membership or join request operation.
/// </summary>
public sealed class GroupJoinOperationResult
{
    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    [JsonIgnore]
    public bool Success { get; }

    /// <summary>
    /// Gets a value indicating whether a new membership was established.
    /// </summary>
    [JsonIgnore]
    public bool MembershipCreated { get; }

    /// <summary>
    /// Gets a value indicating whether an entity was removed (request cancelled or member left).
    /// </summary>
    [JsonIgnore]
    public bool EntityRemoved { get; }

    public GroupJoinOperationResult(bool success, bool membershipCreated = false, bool entityRemoved = false)
    {
        Success = success;
        MembershipCreated = membershipCreated;
        EntityRemoved = entityRemoved;
    }

    public static GroupJoinOperationResult RequestSubmitted() =>
        new(success: true);

    public static GroupJoinOperationResult RequestCancelled() =>
        new(success: true, entityRemoved: true);

    public static GroupJoinOperationResult Approved() =>
        new(success: true, membershipCreated: true, entityRemoved: true);

    public static GroupJoinOperationResult Rejected() =>
        new(success: true, entityRemoved: true);

    public static GroupJoinOperationResult Left() =>
        new(success: true, entityRemoved: true);

    /// <inheritdoc />
    public override string ToString() => "GroupJoinOperationResult [private]";
}

/// <summary>
/// Contract for authorized group membership and join request operations across module-scoped groups.
/// </summary>
public interface IGroupMembershipOperationService
{
    Task<GroupJoinOperationResult> SubmitJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        int callerAge = 25,
        CancellationToken cancellationToken = default);

    Task<GroupJoinOperationResult> CancelJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default);

    Task<GroupJoinOperationResult> ProcessJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string actorModule,
        Guid actorProfileId,
        bool isActorAuthorizedRole,
        Guid applicantProfileId,
        bool approve,
        int applicantAge = 25,
        CancellationToken cancellationToken = default);

    Task<GroupJoinOperationResult> LeaveGroupAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service orchestrating authorized group membership operations, join requests,
/// role-governed admissions, and voluntary exits under strict module boundary isolation.
/// </summary>
public sealed class GroupMembershipOperationService : IGroupMembershipOperationService
{
    private readonly IGroupEntryRepository _entryRepository;
    private readonly IGroupMembershipRepository _membershipRepository;
    private readonly IGroupJoinRequestRepository _joinRequestRepository;

    public GroupMembershipOperationService(
        IGroupEntryRepository entryRepository,
        IGroupMembershipRepository membershipRepository,
        IGroupJoinRequestRepository joinRequestRepository)
    {
        _entryRepository = entryRepository ?? throw new ArgumentNullException(nameof(entryRepository));
        _membershipRepository = membershipRepository ?? throw new ArgumentNullException(nameof(membershipRepository));
        _joinRequestRepository = joinRequestRepository ?? throw new ArgumentNullException(nameof(joinRequestRepository));
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> SubmitJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        int callerAge = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Authorize participation policy
        var authResult = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, callerModule, callerProfileId, callerAge);

        if (authResult.IsDenied)
        {
            throw new UnauthorizedAccessException($"Participation denied: {authResult.Status}");
        }

        // 2. Verify target group exists within module scope
        var group = await _entryRepository.GetGroupAsync(groupId, scope, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException("Target group does not exist within the specified module scope.");
        }

        // 3. Prevent duplicate membership
        if (await _membershipRepository.ExistsAsync(groupId, scope, callerProfileId, cancellationToken))
        {
            throw new InvalidOperationException("Profile is already an active member of this group.");
        }

        // 4. Prevent duplicate pending join requests
        if (await _joinRequestRepository.ExistsAsync(groupId, scope, callerProfileId, cancellationToken))
        {
            throw new InvalidOperationException("Join request is already pending for this profile.");
        }

        // 5. Persist join request
        var joinRequest = new GroupJoinRequest(groupId, scope, callerProfileId, DateTime.UtcNow);
        await _joinRequestRepository.SaveJoinRequestAsync(joinRequest, cancellationToken);

        return GroupJoinOperationResult.RequestSubmitted();
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> CancelJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var authResult = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, callerModule, callerProfileId);

        if (authResult.IsDenied)
        {
            throw new UnauthorizedAccessException($"Cancellation denied: {authResult.Status}");
        }

        var removed = await _joinRequestRepository.RemoveJoinRequestAsync(
            groupId, scope, callerProfileId, cancellationToken);

        if (!removed)
        {
            throw new InvalidOperationException("No pending join request found to cancel.");
        }

        return GroupJoinOperationResult.RequestCancelled();
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> ProcessJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string actorModule,
        Guid actorProfileId,
        bool isActorAuthorizedRole,
        Guid applicantProfileId,
        bool approve,
        int applicantAge = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Verify actor module matches group module
        if (!string.Equals(actorModule, scope.Module, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cross-module join request processing is denied.");
        }

        // 2. Verify actor role capability
        if (!isActorAuthorizedRole)
        {
            throw new UnauthorizedAccessException("Actor does not possess the required moderation or administrative role.");
        }

        // 3. Verify join request exists
        var pendingRequest = await _joinRequestRepository.GetJoinRequestAsync(
            groupId, scope, applicantProfileId, cancellationToken);

        if (pendingRequest is null)
        {
            throw new InvalidOperationException("No pending join request found for applicant.");
        }

        if (approve)
        {
            // 4. Validate applicant eligibility at time of approval
            var applicantAuth = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
                scope, groupId, scope.Module, applicantProfileId, applicantAge);

            if (applicantAuth.IsDenied)
            {
                throw new InvalidOperationException($"Applicant is ineligible for admission: {applicantAuth.Status}");
            }

            // 5. Create membership and delete join request
            var membership = new GroupMembership(groupId, scope, applicantProfileId);
            await _membershipRepository.SaveMembershipAsync(membership, cancellationToken);
            await _joinRequestRepository.RemoveJoinRequestAsync(groupId, scope, applicantProfileId, cancellationToken);

            return GroupJoinOperationResult.Approved();
        }
        else
        {
            // Reject: delete join request
            await _joinRequestRepository.RemoveJoinRequestAsync(groupId, scope, applicantProfileId, cancellationToken);
            return GroupJoinOperationResult.Rejected();
        }
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> LeaveGroupAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(callerModule, scope.Module, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cross-module leave request is denied.");
        }

        if (callerProfileId == Guid.Empty)
        {
            throw new ArgumentException("Caller profile identifier cannot be empty.", nameof(callerProfileId));
        }

        var removed = await _membershipRepository.RemoveMembershipAsync(
            groupId, scope, callerProfileId, cancellationToken);

        if (!removed)
        {
            throw new InvalidOperationException("Profile is not an active member of this group.");
        }

        return GroupJoinOperationResult.Left();
    }
}
