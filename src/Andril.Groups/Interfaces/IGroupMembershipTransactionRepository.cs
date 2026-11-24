using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;

namespace Andril.Groups.Interfaces;

/// <summary>
/// Status outcome of an atomic group membership or admission transaction.
/// </summary>
public enum GroupMembershipTransactionStatus
{
    /// <summary>Transaction committed successfully.</summary>
    Committed = 1,

    /// <summary>Target participant is already an active member; duplicate membership was safely prevented.</summary>
    AlreadyMember = 2,

    /// <summary>A join request for this participant is already recorded; duplicate submission was prevented.</summary>
    RequestAlreadyPending = 3,

    /// <summary>Target join request was not found or was already resolved.</summary>
    RequestNotFound = 4,

    /// <summary>Target participant is not an active member of the group.</summary>
    NotMember = 5,

    /// <summary>Concurrency conflict or unexpected entity revision detected.</summary>
    ConcurrencyConflict = 6,

    /// <summary>Group capacity limit reached; admission denied.</summary>
    CapacityExceeded = 7,

    /// <summary>Transaction outcome could not be determined; reconciliation required.</summary>
    Unknown = 8
}

/// <summary>
/// Result of an atomic join request submission transaction.
/// </summary>
public sealed class GroupJoinRequestSubmissionTransactionResult
{
    [JsonIgnore]
    public bool Success { get; }

    [JsonIgnore]
    public GroupMembershipTransactionStatus Status { get; }

    [JsonIgnore]
    public GroupJoinRequest? JoinRequest { get; }

    [JsonIgnore]
    public long? CommittedRevision { get; }

    public GroupJoinRequestSubmissionTransactionResult(
        bool success,
        GroupMembershipTransactionStatus status,
        GroupJoinRequest? joinRequest = null,
        long? committedRevision = null)
    {
        Success = success;
        Status = status;
        JoinRequest = joinRequest;
        CommittedRevision = committedRevision;
    }

    public static GroupJoinRequestSubmissionTransactionResult Submitted(GroupJoinRequest request, long? revision = null) =>
        new(success: true, GroupMembershipTransactionStatus.Committed, request, revision);

    public static GroupJoinRequestSubmissionTransactionResult AlreadyPending(GroupJoinRequest existing, long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.RequestAlreadyPending, existing, revision);

    public static GroupJoinRequestSubmissionTransactionResult AlreadyMember(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.AlreadyMember, null, revision);

    public static GroupJoinRequestSubmissionTransactionResult Conflict(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.ConcurrencyConflict, null, revision);

    public static GroupJoinRequestSubmissionTransactionResult UnknownOutcome() =>
        new(success: false, GroupMembershipTransactionStatus.Unknown, null, null);

    /// <inheritdoc />
    public override string ToString() => "GroupJoinRequestSubmissionTransactionResult [private]";
}

/// <summary>
/// Result of an atomic join request cancellation transaction.
/// </summary>
public sealed class GroupJoinRequestCancellationTransactionResult
{
    [JsonIgnore]
    public bool Success { get; }

    [JsonIgnore]
    public GroupMembershipTransactionStatus Status { get; }

    [JsonIgnore]
    public bool RequestCancelled { get; }

    [JsonIgnore]
    public long? CommittedRevision { get; }

    public GroupJoinRequestCancellationTransactionResult(
        bool success,
        GroupMembershipTransactionStatus status,
        bool requestCancelled,
        long? committedRevision = null)
    {
        Success = success;
        Status = status;
        RequestCancelled = requestCancelled;
        CommittedRevision = committedRevision;
    }

    public static GroupJoinRequestCancellationTransactionResult Cancelled(long? revision = null) =>
        new(success: true, GroupMembershipTransactionStatus.Committed, requestCancelled: true, revision);

    public static GroupJoinRequestCancellationTransactionResult NotFound(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.RequestNotFound, requestCancelled: false, revision);

    public static GroupJoinRequestCancellationTransactionResult Conflict(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.ConcurrencyConflict, requestCancelled: false, revision);

    public static GroupJoinRequestCancellationTransactionResult UnknownOutcome() =>
        new(success: false, GroupMembershipTransactionStatus.Unknown, requestCancelled: false, null);

    /// <inheritdoc />
    public override string ToString() => "GroupJoinRequestCancellationTransactionResult [private]";
}

/// <summary>
/// Result of an atomic join request admission processing transaction (approval or rejection).
/// </summary>
public sealed class GroupAdmissionTransactionResult
{
    [JsonIgnore]
    public bool Success { get; }

    [JsonIgnore]
    public GroupMembershipTransactionStatus Status { get; }

    [JsonIgnore]
    public bool MembershipCreated { get; }

    [JsonIgnore]
    public bool RequestResolved { get; }

    [JsonIgnore]
    public GroupMembership? Membership { get; }

    [JsonIgnore]
    public long? CommittedRevision { get; }

    public GroupAdmissionTransactionResult(
        bool success,
        GroupMembershipTransactionStatus status,
        bool membershipCreated,
        bool requestResolved,
        GroupMembership? membership = null,
        long? committedRevision = null)
    {
        Success = success;
        Status = status;
        MembershipCreated = membershipCreated;
        RequestResolved = requestResolved;
        Membership = membership;
        CommittedRevision = committedRevision;
    }

    public static GroupAdmissionTransactionResult Approved(GroupMembership membership, long? revision = null) =>
        new(success: true, GroupMembershipTransactionStatus.Committed, membershipCreated: true, requestResolved: true, membership, revision);

    public static GroupAdmissionTransactionResult Rejected(long? revision = null) =>
        new(success: true, GroupMembershipTransactionStatus.Committed, membershipCreated: false, requestResolved: true, null, revision);

    public static GroupAdmissionTransactionResult AlreadyMember(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.AlreadyMember, membershipCreated: false, requestResolved: false, null, revision);

    public static GroupAdmissionTransactionResult RequestNotFound(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.RequestNotFound, membershipCreated: false, requestResolved: false, null, revision);

    public static GroupAdmissionTransactionResult CapacityExceeded(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.CapacityExceeded, membershipCreated: false, requestResolved: false, null, revision);

    public static GroupAdmissionTransactionResult Conflict(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.ConcurrencyConflict, membershipCreated: false, requestResolved: false, null, revision);

    public static GroupAdmissionTransactionResult UnknownOutcome() =>
        new(success: false, GroupMembershipTransactionStatus.Unknown, membershipCreated: false, requestResolved: false, null, null);

    /// <inheritdoc />
    public override string ToString() => "GroupAdmissionTransactionResult [private]";
}

/// <summary>
/// Result of an atomic group departure transaction.
/// </summary>
public sealed class GroupDepartureTransactionResult
{
    [JsonIgnore]
    public bool Success { get; }

    [JsonIgnore]
    public GroupMembershipTransactionStatus Status { get; }

    [JsonIgnore]
    public bool MembershipRemoved { get; }

    [JsonIgnore]
    public long? CommittedRevision { get; }

    public GroupDepartureTransactionResult(
        bool success,
        GroupMembershipTransactionStatus status,
        bool membershipRemoved,
        long? committedRevision = null)
    {
        Success = success;
        Status = status;
        MembershipRemoved = membershipRemoved;
        CommittedRevision = committedRevision;
    }

    public static GroupDepartureTransactionResult Departed(long? revision = null) =>
        new(success: true, GroupMembershipTransactionStatus.Committed, membershipRemoved: true, revision);

    public static GroupDepartureTransactionResult NotMember(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.NotMember, membershipRemoved: false, revision);

    public static GroupDepartureTransactionResult Conflict(long? revision = null) =>
        new(success: false, GroupMembershipTransactionStatus.ConcurrencyConflict, membershipRemoved: false, revision);

    public static GroupDepartureTransactionResult UnknownOutcome() =>
        new(success: false, GroupMembershipTransactionStatus.Unknown, membershipRemoved: false, null);

    /// <inheritdoc />
    public override string ToString() => "GroupDepartureTransactionResult [private]";
}

/// <summary>
/// Result of reconciling an ambiguous or unknown transaction outcome.
/// </summary>
public sealed class GroupTransactionReconciliationResult
{
    [JsonIgnore]
    public bool IsCommitted { get; }

    [JsonIgnore]
    public GroupMembershipTransactionStatus ResolvedStatus { get; }

    [JsonIgnore]
    public long? CurrentRevision { get; }

    public GroupTransactionReconciliationResult(
        bool isCommitted,
        GroupMembershipTransactionStatus resolvedStatus,
        long? currentRevision = null)
    {
        IsCommitted = isCommitted;
        ResolvedStatus = resolvedStatus;
        CurrentRevision = currentRevision;
    }

    public static GroupTransactionReconciliationResult Confirmed(GroupMembershipTransactionStatus status, long? revision = null) =>
        new(isCommitted: true, status, revision);

    public static GroupTransactionReconciliationResult NotApplied(GroupMembershipTransactionStatus status = GroupMembershipTransactionStatus.Unknown, long? revision = null) =>
        new(isCommitted: false, status, revision);

    /// <inheritdoc />
    public override string ToString() => "GroupTransactionReconciliationResult [private]";
}

/// <summary>
/// Internal backend persistence repository contract defining atomic group membership transactions,
/// join request resolutions, concurrency checks, and outcome reconciliation (CF-06, CF-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Atomic Admission Boundary:</b> Group join-request approval and membership creation execute within a single
/// atomic transaction to prevent race conditions (e.g. concurrent approvals or partial membership creation).
/// An implementation must not perform membership approval as two independent disconnected repository writes.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Scope and Identity Isolation:</b> All operations require strict correlation of <paramref name="groupId"/>,
/// <paramref name="scope"/>, and participant profile identities. Cross-module or cross-group modifications are prohibited.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Concurrency and Idempotency:</b> Operations support optional expected revisions to detect conflicting updates
/// and guarantee idempotent execution on repeat attempts.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Reconciliation of Ambiguous Outcomes:</b> Provides explicit reconciliation for network or timeout failures
/// where transaction outcome cannot be confirmed synchronously.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No Implicit Privilege:</b> Interface definition does not replace upstream authorization or verify actor permissions.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface IGroupMembershipTransactionRepository
{
    /// <summary>
    /// Atomically records a group join request, preventing duplicate pending requests or requests from existing members.
    /// </summary>
    Task<GroupJoinRequestSubmissionTransactionResult> SubmitJoinRequestAtomicAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid requestingProfileId,
        DateTime requestedAtUtc,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically cancels an existing pending join request.
    /// </summary>
    Task<GroupJoinRequestCancellationTransactionResult> CancelJoinRequestAtomicAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid requestingProfileId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically processes a join request: creates a new membership and resolves the request on approval,
    /// or resolves the request on rejection, under concurrency and capacity verification.
    /// </summary>
    Task<GroupAdmissionTransactionResult> ProcessAdmissionAtomicAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid applicantProfileId,
        bool approve,
        Guid actorProfileId,
        DateTime decisionAtUtc,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically removes an active membership from the specified group and scope.
    /// </summary>
    Task<GroupDepartureTransactionResult> LeaveGroupAtomicAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid memberProfileId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the committed state of an ambiguous or unknown transaction attempt using its correlation identifier.
    /// </summary>
    Task<GroupTransactionReconciliationResult> ReconcileTransactionOutcomeAsync(
        Guid groupId,
        GroupModuleScope scope,
        Guid operationCorrelationId,
        CancellationToken cancellationToken = default);
}
