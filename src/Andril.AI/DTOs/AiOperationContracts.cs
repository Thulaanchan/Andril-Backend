using Andril.AI.ChatBox;
using Andril.AI.Interfaces;

namespace Andril.AI.DTOs;

// Internal proposed technical contracts, not HTTP DTOs or authorization credentials.
public sealed record AiOperationAdmissionRequest(AiModule Module, Guid ConversationId, Guid OperationId, string UserText)
{
    public override string ToString() => "AI operation request [private input omitted]";
}

public sealed record AiOperationReference(AiAuthorizedActor Actor, Guid ConversationId, Guid OperationId);
public sealed record AiGenerationAttemptReference(AiOperationReference Operation, Guid AttemptId);
public sealed record AiManualRetryRequest(AiOperationReference Operation, bool ExplicitlyRequested);

public enum AiOperationStatus { Unknown, Reserved, InProgress, PendingReconciliation, Completed, ConfirmedFailed, Cancelled }
public enum AiQuotaState { Unknown, Reserved, Committed, Released }
public enum AiDispatchState { Unknown, NotDispatched, Confirmed, Uncertain, ConclusivelyFailed }
public enum AiOperationOutcome
{
    Unknown, Admitted, Existing, ReadyForManualRetry, AuthorizedStatus,
    Denied, NotAllowlisted, DailyQuotaExhausted, AccountRateLimited, GlobalRateLimited,
    ConversationBusy, IdempotencyConflict, ReconciliationRequired, RetryBlocked,
    ConversationUnavailable, BudgetNotVerified
}

/// <summary>Content-free trusted snapshot. Revision is an opaque concurrency token, not permission.
/// QuotaDay uses admission UTC date: proposed M2-reviewable bucket choice, preserved across retries.</summary>
public sealed record AiOperationSnapshot(
    AiOperationReference Operation, AiOperationStatus Status, AiQuotaState Quota,
    AiDispatchState Dispatch, DateTimeOffset AdmittedAtUtc, DateOnly QuotaDay,
    Guid Revision, Guid? AttemptId);

public sealed record AiOperationResult(AiOperationOutcome Outcome, AiOperationSnapshot? Snapshot = null);

/// <summary>Authoritative aggregate only; no other module's private identifiers/content.
/// All counts and event sets must be complete, consistent and protected in the admission transaction.</summary>
public sealed record AiAdmissionEvidence(
    DateTimeOffset NowUtc, DateOnly QuotaDay, IReadOnlyList<Guid>? AllowlistedAccounts,
    int Reservations, int Charges, IReadOnlyList<DateTimeOffset>? AccountAdmissions,
    IReadOnlyList<DateTimeOffset>? GlobalAdmissions, bool ConversationBusy,
    bool EligibilityVerified, bool BudgetAdmissionVerified);

public enum AiAccountingEvent { Unknown, UsableCompletion, ConfirmedUnrecoverableFailure, AmbiguousFailure, UserCancellation }
public enum AiRetryDecision { Blocked, ReconciliationRequired, CapacityContractReviewRequired, Eligible }
