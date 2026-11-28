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

    [JsonIgnore]
    public GroupMembershipTransactionStatus? TransactionStatus { get; private init; }

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

    public static GroupJoinOperationResult Failed() =>
        new(success: false, membershipCreated: false, entityRemoved: false);

    public static GroupJoinOperationResult Failed(GroupMembershipTransactionStatus status) =>
        new(success: false) { TransactionStatus = status };

    /// <inheritdoc />
    public override string ToString() => "GroupJoinOperationResult [private]";
}

/// <summary>
/// Authoritative provider contract for resolving server-side evidence for group membership coordination.
/// </summary>
public interface IGroupAuthoritativeEvidenceProvider
{
    Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(
        Guid profileId,
        string module,
        CancellationToken cancellationToken = default);

    Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);

    Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(
        Guid groupId,
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task<bool> IsGroupOwnerAsync(
        Guid groupId,
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task<int> GetCurrentMemberCountAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);

    Task<int?> GetGroupCapacityAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Injected authoritative boundary providing authenticated actor identity.
/// </summary>
public interface IGroupActorContext
{
    Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(
        string expectedModule,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract for authorized group membership and join request operations across module-scoped groups.
/// </summary>
public interface IGroupMembershipOperationService
{
    [Obsolete("Offline compatibility only. Inactive in production. Use strict atomic methods.")]
    Task<GroupJoinOperationResult> SubmitJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        int callerAge = 25,
        CancellationToken cancellationToken = default);

    [Obsolete("Offline compatibility only. Inactive in production. Use strict atomic methods.")]
    Task<GroupJoinOperationResult> CancelJoinRequestAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default);

    [Obsolete("Offline compatibility only. Inactive in production. Use strict atomic methods.")]
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

    [Obsolete("Offline compatibility only. Inactive in production. Use strict atomic methods.")]
    Task<GroupJoinOperationResult> LeaveGroupAsync(
        Guid groupId,
        GroupModuleScope scope,
        string callerModule,
        Guid callerProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically submits a join request using authoritative actor context and strict policy evaluation.
    /// </summary>
    Task<GroupJoinOperationResult> SubmitJoinRequestStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically cancels a pending join request using authoritative actor context and strict policy evaluation.
    /// </summary>
    Task<GroupJoinOperationResult> CancelJoinRequestStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically processes a join request (approval or rejection) using authoritative actor privileges and atomic transaction.
    /// </summary>
    Task<GroupJoinOperationResult> ProcessJoinRequestStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid applicantProfileId,
        bool approve,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically leaves a group, preventing departure of an owner without ownership transfer.
    /// </summary>
    Task<GroupJoinOperationResult> LeaveGroupStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service orchestrating authorized group membership operations, join requests,
/// role-governed admissions, and voluntary exits under strict module boundary isolation.
/// </summary>
public sealed class GroupMembershipOperationService : IGroupMembershipOperationService
{
    private readonly IGroupEntryRepository? _entryRepository;
    private readonly IGroupMembershipRepository? _membershipRepository;
    private readonly IGroupJoinRequestRepository? _joinRequestRepository;
    private IGroupEntryRepository OfflineEntries => _entryRepository ?? throw new InvalidOperationException("Use strict Group membership operations.");
    private IGroupMembershipRepository OfflineMemberships => _membershipRepository ?? throw new InvalidOperationException("Use strict Group membership operations.");
    private IGroupJoinRequestRepository OfflineRequests => _joinRequestRepository ?? throw new InvalidOperationException("Use strict Group membership operations.");
    private readonly IGroupMembershipTransactionRepository? _transactionRepository;
    private readonly IGroupAuthoritativeEvidenceProvider? _evidenceProvider;
    private readonly IGroupActorContext? _actorContext;

    [Obsolete("Offline compatibility only. Inactive in production. Use constructor with strict transaction dependencies.")]
    public GroupMembershipOperationService(
        IGroupEntryRepository entryRepository,
        IGroupMembershipRepository membershipRepository,
        IGroupJoinRequestRepository joinRequestRepository)
        : this(entryRepository, membershipRepository, joinRequestRepository, null, null, null)
    {
    }

    public GroupMembershipOperationService(IGroupMembershipTransactionRepository transactions,
        IGroupAuthoritativeEvidenceProvider evidence, IGroupActorContext actors)
    {
        _transactionRepository = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _evidenceProvider = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _actorContext = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    public GroupMembershipOperationService(
        IGroupEntryRepository entryRepository,
        IGroupMembershipRepository membershipRepository,
        IGroupJoinRequestRepository joinRequestRepository,
        IGroupMembershipTransactionRepository? transactionRepository,
        IGroupAuthoritativeEvidenceProvider? evidenceProvider,
        IGroupActorContext? actorContext)
    {
        _entryRepository = entryRepository ?? throw new ArgumentNullException(nameof(entryRepository));
        _membershipRepository = membershipRepository ?? throw new ArgumentNullException(nameof(membershipRepository));
        _joinRequestRepository = joinRequestRepository ?? throw new ArgumentNullException(nameof(joinRequestRepository));
        _transactionRepository = transactionRepository;
        _evidenceProvider = evidenceProvider;
        _actorContext = actorContext;
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
        var group = await OfflineEntries.GetGroupAsync(groupId, scope, cancellationToken);
        if (group is null)
        {
            throw new InvalidOperationException("Target group does not exist within the specified module scope.");
        }

        // 3. Prevent duplicate membership
        if (await OfflineMemberships.ExistsAsync(groupId, scope, callerProfileId, cancellationToken))
        {
            throw new InvalidOperationException("Profile is already an active member of this group.");
        }

        // 4. Prevent duplicate pending join requests
        if (await OfflineRequests.ExistsAsync(groupId, scope, callerProfileId, cancellationToken))
        {
            throw new InvalidOperationException("Join request is already pending for this profile.");
        }

        // 5. Persist join request
        var joinRequest = new GroupJoinRequest(groupId, scope, callerProfileId, DateTime.UtcNow);
        await OfflineRequests.SaveJoinRequestAsync(joinRequest, cancellationToken);

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

        var removed = await OfflineRequests.RemoveJoinRequestAsync(
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
        var pendingRequest = await OfflineRequests.GetJoinRequestAsync(
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
            await OfflineMemberships.SaveMembershipAsync(membership, cancellationToken);
            await OfflineRequests.RemoveJoinRequestAsync(groupId, scope, applicantProfileId, cancellationToken);

            return GroupJoinOperationResult.Approved();
        }
        else
        {
            // Reject: delete join request
            await OfflineRequests.RemoveJoinRequestAsync(groupId, scope, applicantProfileId, cancellationToken);
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

        var removed = await OfflineMemberships.RemoveMembershipAsync(
            groupId, scope, callerProfileId, cancellationToken);

        if (!removed)
        {
            throw new InvalidOperationException("Profile is not an active member of this group.");
        }

        return GroupJoinOperationResult.Left();
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> SubmitJoinRequestStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (txRepo, evidenceProvider, actorContext) = EnsureStrictServicesConfigured();

        if (scope is null || groupId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Target group scope and identifier are required.");
        }

        // 1. Obtain authenticated actor identity through injected authoritative boundary
        var actorEvidence = await actorContext.GetCurrentActorAsync(scope.Module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (actorEvidence is null || actorEvidence.ProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Current authorized actor identity is required.");
        }

        // 2. Resolve authoritative group evidence
        var groupEvidence = await evidenceProvider.GetGroupEvidenceAsync(groupId, scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // 3. Evaluate strict authorization
        var authResult = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            actorEvidence,
            groupEvidence,
            scope,
            groupId,
            DateTimeOffset.UtcNow);

        if (authResult.IsDenied)
        {
            throw new UnauthorizedAccessException($"Strict participation authorization denied: {authResult.Status}");
        }

        // 4. Submit join request through atomic repository transaction
        var txResult = await txRepo.SubmitJoinRequestAtomicAsync(
            groupId,
            scope,
            actorEvidence.ProfileId,
            DateTime.UtcNow,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!txResult.Success || txResult.Status != GroupMembershipTransactionStatus.Committed)
        {
            return GroupJoinOperationResult.Failed(txResult.Status);
        }

        return GroupJoinOperationResult.RequestSubmitted();
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> CancelJoinRequestStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (txRepo, evidenceProvider, actorContext) = EnsureStrictServicesConfigured();

        if (scope is null || groupId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Target group scope and identifier are required.");
        }

        var actorEvidence = await actorContext.GetCurrentActorAsync(scope.Module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (actorEvidence is null || actorEvidence.ProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Current authorized actor identity is required.");
        }

        var groupEvidence = await evidenceProvider.GetGroupEvidenceAsync(groupId, scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var authResult = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            actorEvidence,
            groupEvidence,
            scope,
            groupId,
            DateTimeOffset.UtcNow);

        if (authResult.IsDenied)
        {
            throw new UnauthorizedAccessException($"Strict cancellation authorization denied: {authResult.Status}");
        }

        var txResult = await txRepo.CancelJoinRequestAtomicAsync(
            groupId,
            scope,
            actorEvidence.ProfileId,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!txResult.Success || txResult.Status != GroupMembershipTransactionStatus.Committed || !txResult.RequestCancelled)
        {
            return GroupJoinOperationResult.Failed();
        }

        return GroupJoinOperationResult.RequestCancelled();
    }

    /// <inheritdoc />
    public Task<GroupJoinOperationResult> ProcessJoinRequestStrictAsync(Guid groupId, GroupModuleScope scope,
        Guid applicantProfileId, bool approve, CancellationToken cancellationToken = default) =>
        ProcessJoinRequestAsync(groupId, scope, applicantProfileId, approve, null, cancellationToken);

    public Task<GroupJoinOperationResult> ProcessJoinRequestAtRevisionAsync(Guid groupId, GroupModuleScope scope,
        Guid applicantProfileId, bool approve, long expectedRevision, CancellationToken cancellationToken = default) =>
        ProcessJoinRequestAsync(groupId, scope, applicantProfileId, approve, expectedRevision, cancellationToken);

    private async Task<GroupJoinOperationResult> ProcessJoinRequestAsync(Guid groupId, GroupModuleScope scope,
        Guid applicantProfileId, bool approve, long? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (txRepo, evidenceProvider, actorContext) = EnsureStrictServicesConfigured();

        if (scope is null || groupId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Target group scope and identifier are required.");
        }

        if (applicantProfileId == Guid.Empty)
        {
            throw new ArgumentException("Applicant profile identifier cannot be empty.", nameof(applicantProfileId));
        }

        // 1. Obtain authoritative actor evidence and verify required role capability
        var actorEvidence = await actorContext.GetCurrentActorAsync(scope.Module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (actorEvidence is null || actorEvidence.ProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Current authorized actor identity is required.");
        }

        var groupEvidence = await evidenceProvider.GetGroupEvidenceAsync(groupId, scope, cancellationToken);
        var membershipEvidence = await evidenceProvider.GetMembershipEvidenceAsync(groupId, actorEvidence.ProfileId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var actorAuth = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            actorEvidence,
            groupEvidence,
            scope,
            groupId,
            DateTimeOffset.UtcNow,
            requiredCapability: GroupPrivilegedCapability.ApproveJoinRequest,
            membershipEvidence: membershipEvidence);

        if (actorAuth.IsDenied)
        {
            throw new UnauthorizedAccessException($"Privileged admission processing denied: {actorAuth.Status}");
        }

        // 2. If approving, evaluate applicant eligibility and group capacity
        if (approve)
        {
            var applicantEvidence = await evidenceProvider.GetProfileEvidenceAsync(applicantProfileId, scope.Module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var applicantAuth = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
                applicantEvidence,
                groupEvidence,
                scope,
                groupId,
                DateTimeOffset.UtcNow);

            if (applicantAuth.IsDenied)
            {
                return GroupJoinOperationResult.Failed();
            }

            var capacity = await evidenceProvider.GetGroupCapacityAsync(groupId, scope, cancellationToken);
            if (capacity.HasValue)
            {
                var currentCount = await evidenceProvider.GetCurrentMemberCountAsync(groupId, scope, cancellationToken);
                if (currentCount >= capacity.Value)
                {
                    return GroupJoinOperationResult.Failed(GroupMembershipTransactionStatus.CapacityExceeded);
                }
            }
        }

        // 3. Submit membership approval and request resolution through one atomic repository operation
        var txResult = await txRepo.ProcessAdmissionAtomicAsync(
            groupId,
            scope,
            applicantProfileId,
            approve,
            actorEvidence.ProfileId,
            DateTime.UtcNow,
            expectedRevision, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // 4. Validate confirmed transaction results and verify returned repository entity scope
        if (!txResult.Success || txResult.Status != GroupMembershipTransactionStatus.Committed)
        {
            return GroupJoinOperationResult.Failed(txResult.Status);
        }

        if (approve)
        {
            if (!txResult.MembershipCreated || !txResult.RequestResolved || txResult.Membership is null)
            {
                return GroupJoinOperationResult.Failed();
            }

            // Verify returned entity scope
            if (txResult.Membership.GroupId != groupId ||
                !txResult.Membership.Scope.Equals(scope) ||
                txResult.Membership.MemberProfileId != applicantProfileId)
            {
                return GroupJoinOperationResult.Failed();
            }

            return GroupJoinOperationResult.Approved();
        }
        else
        {
            if (!txResult.RequestResolved || txResult.MembershipCreated)
            {
                return GroupJoinOperationResult.Failed();
            }

            return GroupJoinOperationResult.Rejected();
        }
    }

    /// <inheritdoc />
    public async Task<GroupJoinOperationResult> LeaveGroupStrictAsync(
        Guid groupId,
        GroupModuleScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (txRepo, evidenceProvider, actorContext) = EnsureStrictServicesConfigured();

        if (scope is null || groupId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Target group scope and identifier are required.");
        }

        var actorEvidence = await actorContext.GetCurrentActorAsync(scope.Module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (actorEvidence is null || actorEvidence.ProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Current authorized actor identity is required.");
        }

        var groupEvidence = await evidenceProvider.GetGroupEvidenceAsync(groupId, scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var authResult = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            actorEvidence,
            groupEvidence,
            scope,
            groupId,
            DateTimeOffset.UtcNow);

        if (authResult.IsDenied)
        {
            throw new UnauthorizedAccessException($"Strict leave authorization denied: {authResult.Status}");
        }

        // Prevent unsafe owner departure when ownership-transfer policy is unavailable
        var isOwner = await evidenceProvider.IsGroupOwnerAsync(groupId, actorEvidence.ProfileId, cancellationToken);
        if (isOwner)
        {
            throw new InvalidOperationException("Group owner cannot leave group without transferring ownership; ownership-transfer policy is unavailable.");
        }

        var txResult = await txRepo.LeaveGroupAtomicAsync(
            groupId,
            scope,
            actorEvidence.ProfileId,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (!txResult.Success || txResult.Status != GroupMembershipTransactionStatus.Committed || !txResult.MembershipRemoved)
        {
            return GroupJoinOperationResult.Failed();
        }

        return GroupJoinOperationResult.Left();
    }

    private (IGroupMembershipTransactionRepository, IGroupAuthoritativeEvidenceProvider, IGroupActorContext) EnsureStrictServicesConfigured()
    {
        if (_transactionRepository is null || _evidenceProvider is null || _actorContext is null)
        {
            throw new InvalidOperationException("Strict group membership coordination services are not configured.");
        }

        return (_transactionRepository, _evidenceProvider, _actorContext);
    }
}
