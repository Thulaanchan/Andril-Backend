
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// Internal maintenance contract for manual-deletion and inactivity-expiry
/// cleanup (AI-PRE-04). Not a user-facing repository or HTTP endpoint.
/// Implementations must enforce least-privilege worker authorization on
/// every call. Missing or unverified service permissions must deny access.
/// </summary>
public interface IAiConversationCleanupRepository
{
    /// <summary>
    /// Proposed technical upper bound, not a product quota.
    /// </summary>
    public const int MaxBatchSize = 100;

    /// <summary>
    /// Return at most limit (1..MaxBatchSize) metadata-only candidates.
    /// Use trusted server UTC time and persisted state. Select already
    /// revoked conversations whose cleanup is ready to attempt, including
    /// those not yet overdue. Never wait for the purge deadline to start.
    /// Order by earliest combined purge deadline, then conversation ID.
    /// Retry scheduling must not conceal overdue cleanup from monitoring.
    /// Invalid limits, authorization failures and storage errors must fail
    /// safely; do not report a storage failure as an empty successful batch.
    /// </summary>
    Task<IReadOnlyList<AiConversationCleanupCandidate>>
        ListPendingAsync(
            int limit,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempt idempotent purge of one exact account/module/profile scope.
    /// A candidate is only a hint, never authorization to delete data.
    /// Re-read and lock or conditionally update the persisted state inside
    /// the authorized transaction; recheck scope, revocation, trusted time,
    /// earliest purge deadline and applicable approved retention rules.
    /// Never purge an active conversation or an unrelated profile.
    /// Unknown legal/retention eligibility must defer destructive action
    /// and be escalated; access remains revoked.
    ///
    /// Remove active AI content: user/assistant messages, same-conversation
    /// summaries and attributable recommendation outputs/cached context.
    /// Invalidate pending writes and prevent resurrection. Return Purged
    /// only after all required active-content removal is verified durable.
    /// Related stores require retry-safe coordination, not false success.
    /// Keep only separately authorized non-content audit/idempotency data.
    /// Do not delete unrelated profiles, independent quota/billing records,
    /// or consent audits governed by separate approved retention rules.
    /// Propagate cancellation and failures; uncertain outcomes require
    /// safe reconciliation. Never include private content in errors/logs.
    /// </summary>
    Task<AiConversationCleanupOutcome> TryPurgeAsync(
        AiConversationCleanupCandidate candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal internal cleanup metadata. Timestamps use UTC.
/// RevokedAtUtc is the earliest of manual deletion or automatic expiry;
/// PurgeTargetUtc is 24 hours after that event. No message text is carried.
/// Implementations must validate all fields against current storage.
/// Never return this record to a mobile client or use it as a credential.
/// </summary>
public sealed record AiConversationCleanupCandidate(
    Guid ConversationId,
    Guid AccountId,
    Guid ModuleProfileId,
    AiModule Module,
    DateTimeOffset RevokedAtUtc,
    DateTimeOffset PurgeTargetUtc);

/// <summary>
/// Internal outcome, not evidence of backup/provider deletion.
/// An unspecified/default value must never count as success.
/// </summary>
public enum AiConversationCleanupOutcome
{
    Unknown = 0,
    Purged = 1,
    NotEligibleOrMissing = 2,
    DeferredByRetentionPolicy = 3
}

