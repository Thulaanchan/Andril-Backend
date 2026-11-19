using Andril.AI.Policies;

namespace Andril.AI.Interfaces;

/// <summary>Protected authoritative evidence resolution, not a public verification flag.
/// Independently authenticate maintenance caller; resolve current approved category/anchor policy,
/// source record revision, accounting/recovery dependencies, durable replay strategy, deletion event
/// and narrow exception approvals from trusted stores. Validate actual approval evidence and authority,
/// not merely nonempty IDs/strings. Never default to creation/admission/settlement clocks. Missing
/// decisions return unavailable evidence. No implementation or permissive fallback is supplied.</summary>
public interface IAiOperationalRetentionEvidenceReader
{
    Task<AiOperationalRetentionEvidence?> ReadVerifiedEvidenceAsync(AiOperationalRecordReference record, CancellationToken cancellationToken = default);
}

public sealed record AiOperationalCleanupCandidate(AiOperationalRecordReference Record)
{ public override string ToString() => "Operational cleanup candidate [identifiers omitted]"; }
public enum AiOperationalCleanupOutcome { Unknown, Removed, AlreadyRemoved, Anonymized, Deferred, ReviewRequired, NotEligibleOrMissing, ReconciliationRequired }
public sealed record AiOperationalCleanupResult(AiOperationalCleanupOutcome Outcome, AiOperationalRetentionOutcome RetentionReason)
{ public override string ToString() => $"Operational cleanup ({Outcome}, {RetentionReason})"; }

/// <summary>Protected internal M2 maintenance boundary; no concrete cleanup or worker in this batch.
/// Every call independently verifies least-privilege internal caller; no user-facing metadata browser.
/// Candidates, evidence IDs and policy decisions are references, NEVER delete permissions.
/// Use trusted server UTC and bounded positive limits. Deterministic discovery order proposal:
/// review-required/deferred overdue records first, then verified expiry, category, opaque record ID.
/// Missing-anchor records remain visible for review; never conceal unresolved overdue records by
/// retry scheduling. Return minimal metadata and propagate storage/cancellation failures, not empty
/// successful batches. Monitoring is minimized and purpose-restricted; unresolved age requires
/// authoritative escalation rather than silent indefinite retention or automatic financial release.
///
/// Cleanup must reload/lock exact scope and current source revision, independently re-resolve approved
/// anchor/time, all operation/accounting dependencies, replay proof, deletion disposition and finite
/// approved exceptions under ONE protected transaction or equivalent fenced coordination. Synchronize
/// operation/message completion, manual retry, quota and billing settlement, account deletion, policy
/// changes and recovery. No unprotected check-then-delete. No reservation release, refunds, duplicate
/// dispatch/charging, restored conversation access or revived consent as a side effect of cleanup.
///
/// Replay/tombstone strategy and duration require approval; no automatic ID reuse, permanent tombstones
/// or grace period. If removing evidence cannot safely preserve replay protection, defer and escalate.
/// Erasure/anonymization/retention requires authoritative exact disposition; no automatic financial
/// retention exception or assumption pseudonymization meets erasure. Verified account deletion keeps
/// user access revoked; protected historical billing can reconcile without restoring identities.
///
/// Return Removed/Anonymized only after the approved exact outcome is durably verified. AlreadyRemoved
/// requires trusted deletion/replay evidence, never simple row absence. Unknown/uncertain outcomes are
/// not success; reconcile explicitly without automatic retries. No promise of backup/provider deletion.
/// Keep operational records content/secret-free; pseudonymous IDs and hashes are still sensitive.
///
/// This contract does NOT clean conversation content, consent audits or safety incident evidence.
/// Existing content expiry/revocation and 24-hour active-content purge remain independent. Confirmed
/// account deletion retains its existing active-content/derived-data/identifiable-consent cleanup
/// scope and target, absent validated obligation. Separate consent-audit maintenance must reuse
/// AiContextualConsentPolicy.AuditExpiresAt; access/purge/legal exceptions need M2 review. No new clock,
/// legal hold, safety evidence retention policy, worker, migration or production registration here.
/// </summary>
public interface IAiOperationalCleanupRepository
{
    public const int MaxBatchSize = IAiConversationCleanupRepository.MaxBatchSize;
    Task<IReadOnlyList<AiOperationalCleanupCandidate>> ListPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<AiOperationalCleanupResult> TryCleanupAsync(AiOperationalCleanupCandidate candidate, CancellationToken cancellationToken = default);
}
