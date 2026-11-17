using Andril.AI.DTOs;
using Andril.AI.Policies;

namespace Andril.AI.Interfaces;

// Evidence IDs are server lookup references, never proof of user intent or system authority.
public abstract record AiConsentRequest(AiConsentReference Reference, Guid RequestId);
public sealed record AiConsentOfferRequest(AiConsentReference Reference, Guid RequestId, Guid? ExplicitNewUserRequestEvidenceId = null) : AiConsentRequest(Reference, RequestId);
public sealed record AiConsentDecisionRequest(AiConsentReference Reference, Guid RequestId, Guid OfferRevision,
    AiConsentDecision Decision, Guid DecisionEvidenceId) : AiConsentRequest(Reference, RequestId);
public sealed record AiConsentStartRequest(AiConsentReference Reference, Guid RequestId, Guid ConsentRevision, Guid AttemptId) : AiConsentRequest(Reference, RequestId);
public sealed record AiConsentWithdrawalRequest(AiConsentReference Reference, Guid RequestId) : AiConsentRequest(Reference, RequestId);
public sealed record AiConsentPendingReference(AiConsentReference Reference, Guid ConsentRevision, Guid AttemptId);
public sealed record AiConsentSystemInvalidation(Guid EventId, AiConsentReference Reference, AiConsentRevocationReason Reason);
public enum AiConsentSystemOutcome { Unknown, Invalidated, AlreadyInvalidated, Denied, PolicyUnavailable, CompletedWithdrawalPolicyPending }
public sealed record AiConsentSystemResult(AiConsentSystemInvalidation Event, AiConsentSystemOutcome Outcome);

/// <summary>M1/M2 authoritative transactional contract; no implementation, schema or registration.
/// Independently authenticate and authorize every call and recheck account/profile/module/conversation,
/// selected conversation, Social 18+ opt-in, current server session AND module epochs, eligibility,
/// expiry/deletion, recovery fence, purpose/scope/version and applicable policy. Equality alone is
/// insufficient: logout/login and switch-away/back rotate epochs and atomically invalidate pending
/// work. Lifecycle snapshots are hints, never credentials; re-read under the mutation/check lock.
///
/// Offer uses approved understandable purpose/scope descriptions and explicit Allow/Not now choices.
/// Do not inspect context to create recommendations before permission. Resolve explicit-new-request
/// evidence independently from trusted user interaction, never a model boolean. Atomically enforce
/// conversation-scoped suppression after Not now; only a different operation from a verified NEW user
/// request may obtain a new offer. New request is NOT consent. Suppression expires with conversation.
/// Decision evidence must prove exact notice/revision, scope, current user/session and actual Allow
/// or Not now interaction. Silence, saved history, privacy notice, audit rows or prior grants do not.
/// Idempotent same request/payload reconciles without refreshing timestamps; conflicts fail closed.
/// Refusal/expired/revoked operations cannot be turned into grants by duplicate decisions.
///
/// Start atomically consumes one unused grant before grant+5 minutes (exclusive deadline), binds ONE
/// server-issued attempt and coordinates operation/quota/budget admission in a common M2-approved
/// transaction. No check-then-update. Duplicate start returns AlreadyRecorded/ReconciliationRequired,
/// NEVER Started again and never dispatches, reserves/charges twice or revives revoked permission.
/// Started operations do not expire just because the unused deadline passes; all live restrictions
/// still apply. Retry/attempt replacement requires a separately approved integration contract.
/// Pending checks verify exact consent revision/attempt and live session/lifecycle; IAiLateReplyCommitRepository
/// must integrate consent revocation atomically at completion, followed by fresh delivery checks.
///
/// Withdraw/status have no generation quota/budget gate. Invalidate pending consent/derived work,
/// coordinate best-effort cancellation, fence late persistence/delivery; never promise provider rollback.
/// Completed recommendation withdrawal AI-CTX-05 is PENDING: no new deletion/retention policy.
/// Each minimized audit event is atomic with its decision, independently expires at original event+14
/// days, denies ordinary access at expiry and is purged idempotently under reviewed policy. No refresh
/// on messages/status/replay. No raw text, prompts, summaries, outputs, embeddings or inferred traits.
/// Audit is NOT runtime permission; no audit API here. Identifier set/access auditing, account-deletion
/// exceptions/holds and physical cleanup timing require privacy review, not the content purge target.
/// No new tools, unrelated conversation/Diary/person-to-person data, background mining or recommendations.
/// </summary>
public interface IAiContextualConsentRepository
{
    Task<AiConsentLifecycle?> ReadLifecycleAsync(AiOperationReference operation, CancellationToken cancellationToken = default);
    Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default);
    Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default);
    Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default);

    /// <summary>PROTECTED SYSTEM boundary: independently authorize internal caller and verify exact
    /// event scope/reason/epoch in authoritative lifecycle event storage. No current end-user session
    /// required. A caller-provided ID/reason alone grants nothing. Persist revocation and work fences
    /// durably/idempotently, including after account deletion/logout. No permissive adapter supplied.</summary>
    Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default);
}
