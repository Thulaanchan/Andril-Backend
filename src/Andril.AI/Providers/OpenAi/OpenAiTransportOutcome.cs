namespace Andril.AI.Providers.OpenAi;

internal enum OpenAiTransportOutcome
{
    InvalidPreparedRequest, CancelledBeforeInvocation, ResponseReceived, AuthenticationRejected,
    AccessRejected, RequestRejected, RateLimited, ProviderUnavailable, TransportFailure, Timeout,
    CancelledDuringExecution, InvalidResponse, UncertainExecution
}

internal enum OpenAiTransportPhase { BeforeInvocation, WaitingForHeaders, ReadingBody, Complete }

/// <summary>Transport observations only. HTTP rejection, cancellation or timeout never proves
/// zero cost, provider termination, safe retry, quota release, settlement or permission to persist.
/// Response is private unverified parser output, not an approved answer. Correlation IDs are
/// syntactically minimized observations, not credentials or proof of authentic provider execution.</summary>
internal sealed record OpenAiTransportResult(OpenAiTransportOutcome Outcome,
    OpenAiTransportPhase Phase = OpenAiTransportPhase.BeforeInvocation,
    bool HttpInvocationStarted = false, int? HttpStatusCode = null, string? ProviderRequestId = null,
    OpenAiParsedResponsesResponse? Response = null)
{
    public bool ExecutionMayHaveOccurred => HttpInvocationStarted;
    public override string ToString() => $"OpenAI transport ({Outcome}, {Phase}) [private details omitted]";
}
