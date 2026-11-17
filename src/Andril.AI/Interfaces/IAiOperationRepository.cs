using Andril.AI.DTOs;

namespace Andril.AI.Interfaces;

/// <summary>
/// Proposed M1/M2 atomic persistence boundary; no implementation or registration in this batch.
/// Independently reauthorize every call against current account/module/profile/conversation,
/// including Social adult opt-in, expiry/deletion, account revocation and recovery fences.
/// IDs, snapshots and prior successes are never credentials.
///
/// Admission must atomically enforce authoritative six-account allowlist, account-wide daily
/// reservations + charges, rolling account/global limits, conversation exclusion and uniqueness.
/// Check authorized idempotent replay before new-capacity consumption; changed immutable data
/// conflicts. Replay must not insert messages, charge, reserve or dispatch again.
/// Coordinate the accepted user-message insert and expiry update with IAiConversationRepository
/// in ONE transaction; never orchestrate separate check/insert calls. M2 must approve shared
/// transaction/context, uniqueness scope, locks and ledger schema. Admission UTC quota bucket
/// is a proposed technical choice; preserve it through retries, completion and reconciliation.
///
/// Budget admission is authoritative and must fail closed when unavailable. No result permits
/// provider dispatch. Future dispatch must freshly verify budget, privacy/access/revocation,
/// issue a distinct server attempt ID and durably fence uncertain attempts before executing.
///
/// IAiLateReplyCommitRepository remains the ONLY assistant completion + successful quota commit
/// boundary. Reconcile may commit confirmed post-dispatch cancellation or release ONLY proven
/// unrecoverable failure; use AiOperationPolicy and durable idempotent ledger transitions.
/// Cancellation, disconnect, timeout, output rejection and unknown DB outcomes alone NEVER refund.
/// Reconcile uncertain state rather than retry automatically. No raw content in status/logs.
/// </summary>
public interface IAiOperationRepository
{
    Task<AiOperationResult> AdmitAsync(AiAuthorizedActor actor, AiOperationAdmissionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Fresh scoped lookup/reconciliation, independent of remaining generation quota.
    /// Missing/inaccessible references return denial without foreign status or identity metadata.</summary>
    Task<AiOperationResult> ReconcileAsync(AiOperationReference operation, CancellationToken cancellationToken = default);

    /// <summary>Re-read and lock authoritative state. Compare revision and operation; stale
    /// reconciliation requires another reconciliation. Only explicit, conclusively failed,
    /// reservation-held operations may be prepared. Released reservation capacity reacquisition
    /// is pending M2 approval and MUST block. Repeated preparation must not overlap/dispatch;
    /// return RetryBlocked or reconciliation required after an existing preparation.
    /// Atomically transition failed to Reserved/NotDispatched, clear old attempt, retain bucket
    /// and reservation, replace revision. Never overwrite committed content or add a user message.</summary>
    Task<AiOperationResult> PrepareManualRetryAsync(AiOperationReference operation, Guid reconciledRevision,
        CancellationToken cancellationToken = default);
}
