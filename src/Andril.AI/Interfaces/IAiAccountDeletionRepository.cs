
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.AI.Interfaces;

/// <summary>
/// Proposed internal integration contract for AI-PRIV-01.
/// Only an authorized Shared Core integration or maintenance worker may
/// invoke it. This is not an account-deletion confirmation endpoint.
/// </summary>
public interface IAiAccountDeletionRepository
{
    /// <summary>
    /// Reconcile AI cleanup for an account deletion already confirmed by M2.
    /// Both identifiers are internal server-side lookup keys, not credentials.
    /// Verify worker permissions and the authoritative account-deletion event.
    /// Reject empty IDs and mismatched, missing or unconfirmed events safely.
    ///
    /// Immediate access revocation belongs to the authoritative account
    /// deletion transaction; every AI read, write, dispatch and late delivery
    /// must enforce that revocation independently of this cleanup scheduling.
    ///
    /// Durably reconcile cleanup across Learning, Hobbies and Social for
    /// this exact account, including inactive profiles and orphaned AI data.
    /// Invalidate outstanding AI and contextual recommendation operations.
    /// Include messages, summaries, derived recommendations, caches, queued
    /// work and identifiable consent-audit metadata in the cleanup scope.
    ///
    /// Target active-data purge within 24 hours of the original confirmed
    /// deletion timestamp, without extending any earlier applicable deadline.
    /// Derive timestamps from trusted persisted state, not the caller.
    /// Preserve validated retention exceptions and escalate unknown eligibility;
    /// never treat an exception as successful physical erasure.
    ///
    /// Scheduling must be durable and idempotent across retries. Never
    /// recreate accessible records or reset the original deletion deadline.
    /// Propagate authorization, cancellation and storage failures. Do not
    /// expose private content, other accounts or provider/backup guarantees.
    /// </summary>
    Task<AiAccountDeletionScheduleOutcome> EnsureCleanupScheduledAsync(
        Guid accountId,
        Guid deletionEventId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Scheduling outcomes only. None establishes physical purge completion.
/// Unknown or unrecognized outcomes must fail closed.
/// </summary>
public enum AiAccountDeletionScheduleOutcome
{
    Unknown = 0,
    Scheduled = 1,
    AlreadyRecorded = 2,
    NotConfirmedOrMissing = 3
}
