using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;

namespace Andril.AI.Services;

/// <summary>Read-only preparation result, not admission, readiness, reply or financial evidence.
/// There is intentionally no success/dispatch/delivery outcome in this partial implementation.</summary>
internal sealed record AiGenerationPreparation(AiPreparedPrompt Prompt, AiPreparedTextRequest ProviderRequest)
{
    internal bool TransactionAuthorityUnavailable => true;
    public override string ToString() => "AI generation preparation [execution blocked; private data omitted]";
}

/// <summary>Independent guarded PREPARATION only. Fresh actor/privacy checks surround async
/// measurement, enforcing the exact same backend context scope. Existing context revisions are
/// comparison bindings, not current-authority proof. No admit/reserve/claim/send/commit/settle/
/// delivery operation is exposed: M2's shared transaction, revision and dispatch contracts remain
/// unavailable. Do not sequence independently committing services to simulate that transaction.
/// Do not start contextual processing or safety assessment with a fabricated admitted attempt.
/// Unregistered and internal; never called by legacy chat or disabled lifecycle writes.</summary>
internal sealed class AiGenerationService
{
    private readonly AiPrivacyNoticeGuard _privacy;
    private readonly AiTextResponseProvider _provider;

    internal AiGenerationService(AiPrivacyNoticeGuard privacy, AiTextResponseProvider provider)
    {
        _privacy = privacy ?? throw new ArgumentNullException(nameof(privacy));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    internal async Task<AiGenerationPreparation> PrepareAsync(AiOperationAdmissionRequest request,
        AiPreAdmissionPromptContext context, Guid proposedInputId, int maxMessages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AiOperationPolicy.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(context);
        if (request.Module != context.Actor.Module || request.ConversationId != context.ConversationId)
            throw new UnauthorizedAccessException("AI generation context is unavailable.");
        await _privacy.RequireSameActorSatisfiedAsync(context.Actor, cancellationToken).ConfigureAwait(false);
        AiPreparedTextRequest? retained = null;
        var prompt = await AiPromptFactory.PrepareBeforeAdmissionAsync(context, proposedInputId,
            request.UserText, maxMessages, async (constructed, token) =>
            {
                retained = await _provider.PrepareAsync(constructed, token).ConfigureAwait(false);
                return new(retained.Request, retained.Payload.MeasurementPayload, retained.Measurement);
            }, cancellationToken).ConfigureAwait(false);
        // Access may have been revoked or the actor/profile switched during local measurement.
        await _privacy.RequireSameActorSatisfiedAsync(context.Actor, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!prompt.MatchesCurrentContext(context.Actor, request.ConversationId, context.Revision) || retained is null)
            throw new InvalidOperationException("AI generation preparation is unavailable.");
        // Matching a supplied snapshot cannot establish that its revision is current. The future
        // authoritative transaction must recheck it, reconcile operation identity, capacity, consent,
        // input safety and readiness BEFORE any dispatch. No mutation is attempted here.
        return new(prompt, retained);
    }

    public override string ToString() => "AI generation service [read-only preparation; execution blocked]";
}
