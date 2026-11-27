using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

public enum AiDispatchOperationState { Prepared = 1, Claimed = 2, Cancelled = 3, Indeterminate = 4, Completed = 5 }
public enum AiDispatchJournalOutcome { Unavailable, Saved, Available, Denied, Conflict, CommitUnknown }

public sealed record AiDispatchOperationSnapshot(Guid OperationId, AiDispatchOperationState State,
    AiAcknowledgementRevision AcknowledgementRevision)
{
    public override string ToString() => $"AI operation state: {State} [private references omitted]";
}

public sealed record AiDispatchJournalResult(AiDispatchJournalOutcome Outcome, AiDispatchOperationSnapshot? Operation = null)
{
    public override string ToString() => $"AI operation outcome: {Outcome}";
}

/// <summary>
/// New TD-04 server-owned operation journal, not a provider dispatcher or transport permission.
/// Claims are single-use durable markers. They MUST NOT be treated as closing the claim-to-send race.
/// External dispatch remains disabled until a transport fence and its revocation ordering are implemented.
/// This contract stores no prompt, response, tool data, credentials or provider choice.
/// </summary>
public interface IAiDispatchOperationRepository
{
    Task<AiDispatchJournalResult> PrepareAsync(AiAuthorizedActor actor, Guid operationId,
        AiAcknowledgementRevision reviewedAcknowledgement, CancellationToken cancellationToken = default);
    Task<AiDispatchJournalResult> ClaimAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default);
    Task<AiDispatchJournalResult> ValidateForReleaseAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default);
    Task<AiDispatchJournalResult> CancelAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default);
    Task<AiDispatchJournalResult> MarkIndeterminateAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default);
    Task<AiDispatchJournalResult> ReconcileAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Atomically save one assistant result for the existing same-operation user message and
    /// transition Claimed to Completed. This is not provider dispatch authority. No reply data
    /// is returned; ambiguous commit requires owner-scoped reconciliation without replay.
    /// </summary>
    Task<AiDispatchJournalResult> CompleteConversationResultAsync(AiAuthorizedActor actor, Guid operationId,
        Guid conversationId, string assistantText, CancellationToken cancellationToken = default);
}
