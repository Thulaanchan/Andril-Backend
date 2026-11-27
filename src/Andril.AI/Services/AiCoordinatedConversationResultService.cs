using System.ComponentModel.DataAnnotations;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;

namespace Andril.AI.Services;

public enum AiCoordinatedResultOutcome { Unavailable, Denied, InvalidRequest, Conflict, Persisted, ReconciliationRequired }
public sealed record AiCoordinatedResult(AiCoordinatedResultOutcome Outcome)
{
    public override string ToString() => $"AI coordinated result: {Outcome}";
}

/// <summary>
/// Unregistered application orchestration over existing authority, acknowledgement and atomic
/// result commands. Requires a real dispatch boundary: the default never invokes a provider.
/// Accepts no client account/profile/consent/provider choice and returns no private response text.
/// No retries or independent post-validation assistant write. Quotas/admission remain the
/// dispatch authority's obligation; this service cannot be registered to bypass them.
/// </summary>
public sealed class AiCoordinatedConversationResultService(AiPrivacyNoticeGuard privacy,
    IAiPrivacyAcknowledgementRepository acknowledgements, IAiDispatchOperationRepository journal, IAiConversationRepository conversations,
    IAiProviderDispatchBoundary dispatch, IAiResponseProvider provider)
{
    public async Task<AiCoordinatedResult> ExecuteAsync(Guid conversationId, Guid operationId, string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new AiChatSubmissionRequest(operationId, text);
        if (conversationId == Guid.Empty || request.Validate(new ValidationContext(request)).Any()) return new(AiCoordinatedResultOutcome.InvalidRequest);
        AiAuthorizedActor actor;
        try { actor = await privacy.RequireSatisfiedAsync(AiModule.Learning, cancellationToken); }
        catch (UnauthorizedAccessException) { return new(AiCoordinatedResultOutcome.Denied); }
        catch (InvalidOperationException) { return new(AiCoordinatedResultOutcome.Unavailable); }

        if (await conversations.FindAsync(actor, conversationId, cancellationToken) is null)
            return new(AiCoordinatedResultOutcome.Denied);

        var acknowledgement = await acknowledgements.ReadAsync(actor, cancellationToken);
        if (acknowledgement.Outcome == AiAcknowledgementOutcome.Denied) return new(AiCoordinatedResultOutcome.Denied);
        if (acknowledgement.Outcome != AiAcknowledgementOutcome.Available) return new(AiCoordinatedResultOutcome.Unavailable);
        if (acknowledgement.State?.Acknowledged != true) return new(AiCoordinatedResultOutcome.Denied);
        var prepared = await journal.PrepareAsync(actor, operationId, acknowledgement.State.Revision, cancellationToken);
        if (prepared.Outcome != AiDispatchJournalOutcome.Saved) return Map(prepared.Outcome);
        var claimed = await journal.ClaimAsync(actor, operationId, cancellationToken);
        if (claimed.Outcome != AiDispatchJournalOutcome.Saved) return Map(claimed.Outcome);

        var invoked = 0; var completed = false; AiProviderResponse? response = null;
        AiProviderDispatchReceipt receipt;
        try
        {
            receipt = await dispatch.DispatchAsync(new(actor, operationId, acknowledgement.State.Revision), async ct =>
            {
                // This check supplements, never replaces, the transport's actual revocation fence.
                if (Interlocked.Increment(ref invoked) != 1) throw new InvalidOperationException("AI operation cannot be replayed.");
                await privacy.RequireSameActorSatisfiedAsync(actor, ct);
                response = await provider.GenerateAsync(new AiProviderPrompt(AiModule.Learning, text), ct);
                completed = true;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (invoked != 0) await journal.MarkIndeterminateAsync(actor, operationId, CancellationToken.None);
            else await journal.CancelAsync(actor, operationId, CancellationToken.None);
            throw;
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (invoked != 0) await journal.MarkIndeterminateAsync(actor, operationId, CancellationToken.None);
            else await journal.CancelAsync(actor, operationId, CancellationToken.None);
            return new(invoked != 0 ? AiCoordinatedResultOutcome.ReconciliationRequired : AiCoordinatedResultOutcome.Unavailable);
        }
        if (receipt.Outcome != AiProviderDispatchOutcome.Sent || invoked != 1 || !completed || string.IsNullOrWhiteSpace(response?.Text))
        {
            if (invoked != 0) await journal.MarkIndeterminateAsync(actor, operationId, CancellationToken.None);
            else await journal.CancelAsync(actor, operationId, CancellationToken.None);
            return new(invoked != 0 || receipt.Outcome is AiProviderDispatchOutcome.Sent or AiProviderDispatchOutcome.Indeterminate
                ? AiCoordinatedResultOutcome.ReconciliationRequired
                : receipt.Outcome == AiProviderDispatchOutcome.Denied ? AiCoordinatedResultOutcome.Denied : AiCoordinatedResultOutcome.Unavailable);
        }
        // CompleteConversationResultAsync performs authority checks and both writes in one DB transaction.
        // Do not return provider text or use ValidateForRelease + an independently committed append.
        var persisted = await journal.CompleteConversationResultAsync(actor, operationId, conversationId, response!.Text, cancellationToken);
        return Map(persisted.Outcome);
    }

    private static AiCoordinatedResult Map(AiDispatchJournalOutcome outcome) => new(outcome switch
    {
        AiDispatchJournalOutcome.Saved => AiCoordinatedResultOutcome.Persisted,
        AiDispatchJournalOutcome.Denied => AiCoordinatedResultOutcome.Denied,
        AiDispatchJournalOutcome.Conflict => AiCoordinatedResultOutcome.Conflict,
        AiDispatchJournalOutcome.CommitUnknown => AiCoordinatedResultOutcome.ReconciliationRequired,
        _ => AiCoordinatedResultOutcome.Unavailable
    });
}
