
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.AI.Interfaces;

/// <summary>
/// Proposed internal recovery contract for AI-PRE-04 and AI-PRIV-01.
/// Not a user-facing endpoint or a command to restore a database.
/// Only an independently authorized maintenance integration may invoke it.
/// </summary>
public interface IAiRestoreRevocationRepository
{
    /// <summary>
    /// Reconcile restored AI data for the current trusted recovery run.
    /// The run ID is a server-side lookup key, not an access credential.
    /// Reject empty IDs, unauthorized callers and stale or unrelated runs.
    ///
    /// Keep ordinary AI access blocked during restoration and reconciliation.
    /// Verify the active recovery run and use authoritative revocation
    /// evidence that has not been rolled back with the restored database.
    /// Missing, incomplete or unverifiable evidence must fail closed.
    ///
    /// Reapply confirmed account and conversation revocations across all
    /// three modules without exposing or mixing independent profile data.
    /// Reevaluate conversation expiry using trusted current UTC time.
    /// Invalidate revoked grants, stale operations, queued writes and caches.
    /// Preserve original deletion and expiry deadlines; never extend them.
    /// Reconcile required cleanup without restoring revoked content access.
    ///
    /// Reconciliation must be durable and idempotent. Coordinate concurrent
    /// revocations with the recovery fence so none can be lost at cutover.
    /// AlreadyReconciled requires fresh verification of the current run
    /// and revocation coverage, not merely an old completion record.
    ///
    /// Propagate cancellation, authorization and storage failures.
    /// Do not return private content or log sensitive records.
    /// Neither successful outcome is permission to reopen traffic or proof
    /// of physical erasure. Separate verified release controls and normal
    /// account, module and conversation authorization remain mandatory.
    /// </summary>
    Task<AiRestoreRevocationOutcome> EnsureRevocationsReconciledAsync(
        Guid recoveryRunId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal reconciliation outcomes only. Unknown, unrecognized and
/// NotReady outcomes must never authorize access or release recovery gates.
/// </summary>
public enum AiRestoreRevocationOutcome
{
    Unknown = 0,
    Reconciled = 1,
    AlreadyReconciled = 2,
    NotReady = 3
}
