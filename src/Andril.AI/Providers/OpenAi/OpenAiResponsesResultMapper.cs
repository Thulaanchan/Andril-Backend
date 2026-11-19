using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Providers.OpenAi;

internal enum OpenAiMappedResultKind
{
    InvalidRequestBinding, CompletedTextCandidate, Refused, Incomplete, UnsupportedOutput,
    InvalidResponse, InvalidPreparedRequest, AuthenticationRejected, AccessRejected, RequestRejected,
    RateLimited, Timeout, CancelledBeforeInvocation, CancelledDuringExecution, ProviderFailure,
    TransportFailure, UncertainExecution
}

/// <summary>Private observations and candidate text only, NEVER a delivery/persistence/dispatch
/// capability or financial evidence. LocalBinding is supplied by local preparation, not echoed
/// by the provider. ResponseId/model/usage/correlation remain unauthenticated observations.
/// TextPolicyOutcome records the existing unverified response policy, not safety/consent approval.
/// Usage includes reasoning in output already; never add it again, clamp it or infer prices.
/// No credential/error-body fields. Do not serialize/log this object; safe ToString only.</summary>
internal sealed record OpenAiMappedResult(OpenAiMappedResultKind Kind,
    AiProviderPayloadBinding? LocalBinding = null,
    OpenAiTransportOutcome? TransportOutcome = null,
    OpenAiTransportPhase? TransportPhase = null,
    bool HttpInvocationStarted = false, int? HttpStatusCode = null,
    string? ProviderRequestId = null, string? ResponseId = null, string? ReturnedModel = null,
    OpenAiResponseParseOutcome? ParseOutcome = null,
    AiTextCompletionState Completion = AiTextCompletionState.Unknown,
    string? CandidateText = null, string? Refusal = null,
    OpenAiIncompleteReason IncompleteReason = OpenAiIncompleteReason.None,
    OpenAiObservedUsage? Usage = null,
    OpenAiUsageObservationState UsageState = OpenAiUsageObservationState.Missing,
    AiTextValidationOutcome TextPolicyOutcome = AiTextValidationOutcome.Invalid)
{
    public override string ToString() => $"OpenAI mapped result ({Kind}) [private details omitted]";
}

/// <summary>Pure mapping of existing components; no HTTP, parsing, tokenization, database,
/// admission, retry, financial mutation or user-facing delivery. Cannot establish transport or
/// counter authenticity, attempt identity, actor access, safety or consent. The caller must retain
/// trusted LOCAL execution context; matching hashes do not prove which request was dispatched.
/// Only the existing serializer/measurement policy validates local bindings. No alias mapping,
/// provider-echoed fingerprints, new digest convention or fabricated verified usage is introduced.</summary>
internal static class OpenAiResponsesResultMapper
{
    internal static OpenAiMappedResult Map(AiTokenCountRequest? request,
        AiProviderRequestCandidate? candidate, AiTokenCountResult? measurement,
        OpenAiPreparedResponsesRequest? prepared, OpenAiTransportResult? transport)
    {
        var parsed = transport?.Response;
        // Preserve usage even when response acceptance/binding fails: rejection is not zero cost.
        var result = new OpenAiMappedResult(OpenAiMappedResultKind.InvalidRequestBinding,
            TransportOutcome: transport?.Outcome, TransportPhase: transport?.Phase,
            HttpInvocationStarted: transport?.HttpInvocationStarted ?? false,
            HttpStatusCode: transport?.HttpStatusCode, ProviderRequestId: transport?.ProviderRequestId,
            ResponseId: parsed?.ResponseId, ReturnedModel: parsed?.Model, ParseOutcome: parsed?.Outcome,
            Completion: parsed?.Completion ?? AiTextCompletionState.Unknown,
            IncompleteReason: parsed?.IncompleteReason ?? OpenAiIncompleteReason.None,
            Usage: parsed?.Usage, UsageState: parsed?.UsageState ?? OpenAiUsageObservationState.Missing);
        if (AiProviderRequestPolicy.Validate(request, candidate, measurement, prepared) !=
            AiProviderRequestValidationOutcome.ValidatedCandidate) return result;
        result = result with { LocalBinding = prepared!.MeasurementPayload.Binding };
        if (transport is null || !Enum.IsDefined(transport.Outcome) || !Enum.IsDefined(transport.Phase))
            return result with { Kind = OpenAiMappedResultKind.InvalidResponse };

        // Transport failures retain their own meaning; never promote attached text to a candidate.
        if (transport.Outcome is not (OpenAiTransportOutcome.ResponseReceived or OpenAiTransportOutcome.InvalidResponse))
            return result with { Kind = transport.Outcome switch
            {
                OpenAiTransportOutcome.InvalidPreparedRequest => OpenAiMappedResultKind.InvalidPreparedRequest,
                OpenAiTransportOutcome.CancelledBeforeInvocation => OpenAiMappedResultKind.CancelledBeforeInvocation,
                OpenAiTransportOutcome.AuthenticationRejected => OpenAiMappedResultKind.AuthenticationRejected,
                OpenAiTransportOutcome.AccessRejected => OpenAiMappedResultKind.AccessRejected,
                OpenAiTransportOutcome.RequestRejected => OpenAiMappedResultKind.RequestRejected,
                OpenAiTransportOutcome.RateLimited => OpenAiMappedResultKind.RateLimited,
                OpenAiTransportOutcome.ProviderUnavailable => OpenAiMappedResultKind.ProviderFailure,
                OpenAiTransportOutcome.TransportFailure => OpenAiMappedResultKind.TransportFailure,
                OpenAiTransportOutcome.Timeout => OpenAiMappedResultKind.Timeout,
                OpenAiTransportOutcome.CancelledDuringExecution => OpenAiMappedResultKind.CancelledDuringExecution,
                _ => OpenAiMappedResultKind.UncertainExecution
            } };

        if (!transport.HttpInvocationStarted || transport.HttpStatusCode != 200 || parsed is null ||
            !Enum.IsDefined(parsed.Outcome) || !Enum.IsDefined(parsed.Completion) || !Enum.IsDefined(parsed.UsageState))
            return result with { Kind = OpenAiMappedResultKind.InvalidResponse };
        if (parsed.Outcome == OpenAiResponseParseOutcome.UnsupportedOutput)
            return result with { Kind = OpenAiMappedResultKind.UnsupportedOutput };
        if (transport.Outcome != OpenAiTransportOutcome.ResponseReceived ||
            transport.Phase != OpenAiTransportPhase.Complete ||
            parsed.Outcome is not (OpenAiResponseParseOutcome.Parsed or OpenAiResponseParseOutcome.Unresolved) ||
            parsed.Model != request!.Model || parsed.UsageState == OpenAiUsageObservationState.Invalid)
            return result with { Kind = OpenAiMappedResultKind.InvalidResponse };

        // Existing adapter supplies only unverified usage. Completed text is intentionally INVALID
        // for final policy acceptance, while refusal/partial/failure states retain policy vocabulary.
        var policy = parsed.ValidateUnverifiedText(new(request.Model, request.Fingerprint, false));
        result = result with { TextPolicyOutcome = policy.Outcome };
        return parsed.Completion switch
        {
            AiTextCompletionState.Completed when parsed.Outcome == OpenAiResponseParseOutcome.Parsed &&
                !string.IsNullOrWhiteSpace(parsed.AssistantText) && parsed.Refusal is null =>
                result with { Kind = OpenAiMappedResultKind.CompletedTextCandidate, CandidateText = parsed.AssistantText },
            AiTextCompletionState.Refused => result with { Kind = OpenAiMappedResultKind.Refused, Refusal = parsed.Refusal },
            AiTextCompletionState.Incomplete => result with { Kind = OpenAiMappedResultKind.Incomplete },
            AiTextCompletionState.Failed => result with { Kind = OpenAiMappedResultKind.ProviderFailure },
            AiTextCompletionState.Cancelled => result with { Kind = OpenAiMappedResultKind.CancelledDuringExecution },
            AiTextCompletionState.Uncertain => result with { Kind = OpenAiMappedResultKind.UncertainExecution },
            _ => result with { Kind = OpenAiMappedResultKind.InvalidResponse }
        };
    }
}
