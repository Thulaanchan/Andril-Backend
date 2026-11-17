
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.AI.Interfaces;

/// <summary>
/// Proposed internal atomic completion contract for AI-OPS-01,
/// AI-OPS-04 and AI-PRE-04. Not a public API or provider callback endpoint.
/// Implementations must independently verify trusted caller permissions.
/// </summary>
public interface IAiLateReplyCommitRepository
{
    /// <summary>
    /// Complete one previously admitted AI generation operation.
    /// All identifiers and the dispatched actor are trusted server-side
    /// references, not credentials. Never construct them from provider
    /// or unverified client input. Reject invalid IDs and blank replies.
    /// The generationAttemptId identifies a particular authorized dispatch;
    /// a manual retry retains the logical operationId but a new dispatch
    /// requires a distinct server-issued attempt ID.
    ///
    /// In one protected transaction or equivalent atomic boundary:
    /// - Reauthorize the caller and resolve fresh account/profile eligibility,
    ///   including Social age/opt-in and the recovery/revocation fence.
    /// - Load the exact scoped conversation, operation, current dispatch
    ///   attempt and accepted user message. Check trusted UTC time, expiry,
    ///   deletion, supersession, cancellation and current operation status.
    /// - For a new completion, verify that this is the current eligible
    ///   attempt and apply AiLateReplyPolicy.CanAccept to trusted snapshots.
    ///   Never derive operation verification from timestamps alone.
    /// - Persist at most one assistant message for the logical operation,
    ///   mark it completed and reconcile its existing quota reservation
    ///   exactly once under the approved accounting rules.
    /// Never renew conversation expiry or insert another user message.
    ///
    /// AlreadyCommitted is allowed only after fresh scope and lifecycle
    /// checks and verification of the durable completion for the same
    /// operation, user message and dispatch attempt. Never replace content
    /// or charge quota again. Stale or mismatched attempts must be denied.
    ///
    /// Missing or unverifiable eligibility must fail closed. Do not split
    /// verification and persistence into separate unprotected operations.
    /// Propagate authorization, storage and cancellation failures; uncertain
    /// outcomes require status reconciliation rather than automatic retry.
    /// A rejected reply does not itself authorize a quota refund, and
    /// provider-cost reconciliation is separate from reply acceptance.
    /// Do not log assistant text or expose private data in errors.
    /// No outcome grants permission to deliver or stream a response;
    /// delivery must perform independent fresh authorization checks.
    /// </summary>
    Task<AiLateReplyCommitOutcome> TryCommitAsync(
        AiAuthorizedActor dispatchedActor,
        Guid conversationId,
        Guid operationId,
        Guid generationAttemptId,
        Guid acceptedUserMessageId,
        string assistantText,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal completion outcomes, not an authorization credential.
/// Unknown and unrecognized values must never count as success.
/// </summary>
public enum AiLateReplyCommitOutcome
{
    Unknown = 0,
    Committed = 1,
    AlreadyCommitted = 2,
    NotEligibleOrMissing = 3
}
