using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Providers.OpenAi;

/// <summary>Private preparation only, never an execution capability. Retains the exact sealed
/// serializer snapshot and bound measurement; do not log or serialize application content.</summary>
internal sealed record AiPreparedTextRequest(AiTokenCountRequest Request, AiProviderRequestCandidate Candidate,
    OpenAiPreparedResponsesRequest Payload, AiTokenCountResult Measurement)
{
    public override string ToString() => "AI text preparation [private content and bindings omitted]";
}

internal enum AiTextExecutionBlock
{
    InvalidPreparation, InvalidAttempt, ReconciliationRequired, TerminalOperation, DispatchAuthorityUnavailable
}

/// <summary>Restricted adapter's independent preparation stage. Deliberately not
/// IAiResponseProvider: the legacy module/text interface cannot authorize real execution.
/// No transport/credential dependency or send entry point exists until an approved authoritative
/// dispatch claim contract is available. Do not add caller booleans or treat snapshots/hashes as
/// permission. Existing transport/parser/mapper retain all offline outcome handling; duplicating
/// them here or sending to make a synthetic success test would bypass the missing authority.</summary>
internal sealed class AiTextResponseProvider
{
    private readonly IAiTokenCounter _counter;
    internal AiTextResponseProvider(IAiTokenCounter counter) =>
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));

    internal static AiProviderRequestCandidate Candidate(AiTokenCountRequest request) =>
        AiProviderRequestCandidate.FromMeasuredRequest(request,
            new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false));

    internal async Task<AiPreparedTextRequest> PrepareAsync(AiConstructedPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prompt);
        var request = AiTokenCountRequest.FromPrompt(OpenAiResponsesRequestSerializer.ApprovedModel,
            OpenAiResponsesRequestSerializer.SerializationRevision, prompt);
        var candidate = Candidate(request);
        var payload = OpenAiResponsesRequestSerializer.Prepare(request, candidate);
        AiTokenCountResult measurement;
        try
        {
            measurement = await AiTokenCountContract.CountVerifiedAsync(_counter, request,
                payload.MeasurementPayload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A counter may throw a content-bearing diagnostic. Do not propagate its body/inner
            // exception, retry, or accept a fallback estimate. Caller cancellation still wins.
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Trusted AI token measurement is unavailable.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (AiProviderRequestPolicy.Validate(request, candidate, measurement, payload) !=
            AiProviderRequestValidationOutcome.ValidatedCandidate)
            throw new InvalidOperationException("AI structured preparation could not be verified.");
        return new(request, candidate, payload, measurement);
    }

    /// <summary>Negative readiness check, never dispatch authorization. No outcome permits a
    /// send. In particular a caller-created confirmed snapshot cannot replace a durable claim.</summary>
    internal static AiTextExecutionBlock CheckExecutionPrerequisites(AiPreparedTextRequest? prepared,
        AiGenerationAttemptReference? attempt, AiOperationSnapshot? operation)
    {
        if (prepared is null || AiProviderRequestPolicy.Validate(prepared.Request, prepared.Candidate,
            prepared.Measurement, prepared.Payload) != AiProviderRequestValidationOutcome.ValidatedCandidate)
            return AiTextExecutionBlock.InvalidPreparation;
        if (!AiBudgetPolicy.ValidAttempt(attempt) || !AiOperationPolicy.ValidSnapshot(operation) ||
            operation!.Operation != attempt!.Operation || operation.AttemptId != attempt.AttemptId)
            return AiTextExecutionBlock.InvalidAttempt;
        if (operation.Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled or AiOperationStatus.ConfirmedFailed)
            return AiTextExecutionBlock.TerminalOperation;
        if (operation.Status == AiOperationStatus.PendingReconciliation)
            return AiTextExecutionBlock.ReconciliationRequired;
        return AiTextExecutionBlock.DispatchAuthorityUnavailable;
    }

    public override string ToString() => "AI text adapter [execution blocked]";
}
