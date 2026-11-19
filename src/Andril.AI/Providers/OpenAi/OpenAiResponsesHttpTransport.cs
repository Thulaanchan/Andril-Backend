using System.Net.Http.Headers;
using System.Text;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.AI.Providers.OpenAi;

/// <summary>Future trusted server-side credential handoff ONLY. No configuration/env/key loading.
/// No publicly serializable secret property. Never log/serialize the credential or HTTP messages.
/// Synthetic values only in tests. This wrapper is not an authorization grant.</summary>
internal sealed class OpenAiTransportCredential
{
    private readonly string _bearer;
    internal OpenAiTransportCredential(string bearer)
    {
        if (string.IsNullOrEmpty(bearer) || bearer.Any(c => !char.IsAsciiLetterOrDigit(c) &&
            c is not '-' and not '.' and not '_' and not '~' and not '+' and not '/' and not '='))
            throw new ArgumentException("Invalid transport credential.");
        _bearer = bearer;
    }
    internal void Attach(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
    public override string ToString() => "OpenAI credential [secret omitted]";
}

/// <summary>Isolated INTERNAL implementation primitive, not IAiResponseProvider or a live service.
/// Unregistered; no endpoint/legacy chat integration, authorization grant, admission or accounting.
/// A future adapter MUST enforce authoritative dispatch permission and durable attempt fencing
/// before entry. No default network client/factory, retries, fallback or endpoint configuration.
/// The injected client/handler must be independently trusted: disable redirects, unapproved
/// proxies, retry middleware and unsafe logging; retain TLS validation. This class cannot inspect
/// hidden handler behavior or guarantee exactly-once network execution.
///
/// Caller supplies a finite positive technical operation deadline, NOT an approved product timeout.
/// The same deadline covers headers and ALL response reading. HttpClient's own timeout may also
/// expire earlier. Cancellation is cooperative; injected handlers/streams must honor the token.
/// References: OpenAI Responses create/API overview/error-codes; .NET HttpClient.SendAsync.
/// No raw bodies, secrets or exception messages are included in results or logs.</summary>
internal sealed class OpenAiResponsesHttpTransport
{
    private static readonly Uri Destination = new("https://api.openai.com/v1/responses");
    private readonly HttpClient _http;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeProvider _clock;

    internal OpenAiResponsesHttpTransport(HttpClient http, TimeSpan operationTimeout, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        // Platform timer range, not a new product limit. No default timeout is selected here.
        if (operationTimeout <= TimeSpan.Zero || operationTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "A finite operation deadline is required.");
        _http = http;
        _operationTimeout = operationTimeout;
        _clock = clock ?? TimeProvider.System;
    }

    internal async Task<OpenAiTransportResult> SendAsync(AiTokenCountRequest? intended,
        AiProviderRequestCandidate? candidate, AiTokenCountResult? measurement,
        OpenAiPreparedResponsesRequest? prepared, OpenAiTransportCredential? credential,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(OpenAiTransportOutcome.CancelledBeforeInvocation);
        if (credential is null || _http.DefaultRequestHeaders.Any() ||
            AiProviderRequestPolicy.Validate(intended, candidate, measurement, prepared) !=
                AiProviderRequestValidationOutcome.ValidatedCandidate)
            return new(OpenAiTransportOutcome.InvalidPreparedRequest);

        // No reserialization: copies retain EXACT measured bytes. Check the retained snapshot too.
        var bytes = prepared!.CopyUtf8Payload();
        if (!prepared.MatchesPayload(bytes)) return new(OpenAiTransportOutcome.InvalidPreparedRequest);
        using var deadline = new CancellationTokenSource(_operationTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var phase = OpenAiTransportPhase.BeforeInvocation;
        int? status = null;
        string? requestId = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Destination);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            credential.Attach(request);
            token.ThrowIfCancellationRequested();
            // Conservative invocation boundary: even a synchronous SendAsync exception is ambiguous.
            phase = OpenAiTransportPhase.WaitingForHeaders;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            status = (int)response.StatusCode;
            requestId = ReadRequestId(response);
            token.ThrowIfCancellationRequested();
            if (response.RequestMessage?.RequestUri is { } actual && actual != Destination)
                return Result(OpenAiTransportOutcome.InvalidResponse);
            if (status != 200)
            {
                // Error/redirect bodies are deliberately not parsed/read or returned. The headers
                // establish only HTTP classification, never complete generation or zero liability.
                return Result(status switch
                {
                    400 or 404 or 413 or 422 => OpenAiTransportOutcome.RequestRejected,
                    401 => OpenAiTransportOutcome.AuthenticationRejected,
                    403 => OpenAiTransportOutcome.AccessRejected,
                    408 or 504 => OpenAiTransportOutcome.Timeout,
                    429 => OpenAiTransportOutcome.RateLimited,
                    500 or 502 or 503 => OpenAiTransportOutcome.ProviderUnavailable,
                    _ => OpenAiTransportOutcome.UncertainExecution
                });
            }

            phase = OpenAiTransportPhase.ReadingBody;
            var content = response.Content;
            var type = content.Headers.ContentType;
            if (!string.Equals(type?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                (type?.CharSet is { } charset && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) ||
                content.Headers.ContentEncoding.Any())
                return Result(OpenAiTransportOutcome.InvalidResponse);
            var length = content.Headers.ContentLength;
            if (length > OpenAiResponsesResponseParser.ProposedMaxJsonBytes)
                return Result(OpenAiTransportOutcome.InvalidResponse);
            using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Read at most limit+1 bytes; never trust Content-Length or buffer an unbounded body.
                var capacity = Math.Min(buffer.Length, OpenAiResponsesResponseParser.ProposedMaxJsonBytes + 1 - (int)body.Length);
                var count = await stream.ReadAsync(buffer.AsMemory(0, capacity), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (count == 0) break;
                body.Write(buffer, 0, count);
                if (body.Length > OpenAiResponsesResponseParser.ProposedMaxJsonBytes)
                    return Result(OpenAiTransportOutcome.InvalidResponse);
            }
            if (length is { } declared && declared != body.Length)
                return Result(OpenAiTransportOutcome.UncertainExecution);
            phase = OpenAiTransportPhase.Complete;
            var json = new UTF8Encoding(false, true).GetString(body.GetBuffer(), 0, (int)body.Length);
            var parsed = OpenAiResponsesResponseParser.Parse(json, intended!.Model);
            token.ThrowIfCancellationRequested();
            return Result(parsed.Outcome is OpenAiResponseParseOutcome.Parsed or OpenAiResponseParseOutcome.Unresolved
                ? OpenAiTransportOutcome.ResponseReceived : OpenAiTransportOutcome.InvalidResponse, parsed);
        }
        catch (OperationCanceledException)
        {
            return Result(phase == OpenAiTransportPhase.BeforeInvocation && cancellationToken.IsCancellationRequested
                ? OpenAiTransportOutcome.CancelledBeforeInvocation
                : cancellationToken.IsCancellationRequested ? OpenAiTransportOutcome.CancelledDuringExecution
                : OpenAiTransportOutcome.Timeout);
        }
        catch (HttpRequestException) { return Result(phase == OpenAiTransportPhase.ReadingBody
            ? OpenAiTransportOutcome.UncertainExecution : OpenAiTransportOutcome.TransportFailure); }
        catch (IOException) { return Result(OpenAiTransportOutcome.UncertainExecution); }
        catch (EncoderFallbackException) { return Result(OpenAiTransportOutcome.InvalidResponse); }
        catch (DecoderFallbackException) { return Result(OpenAiTransportOutcome.InvalidResponse); }
        catch (ObjectDisposedException) { return Result(OpenAiTransportOutcome.UncertainExecution); }
        catch (InvalidOperationException) { return Result(OpenAiTransportOutcome.UncertainExecution); }

        OpenAiTransportResult Result(OpenAiTransportOutcome outcome, OpenAiParsedResponsesResponse? parsed = null) =>
            new(outcome, phase, phase != OpenAiTransportPhase.BeforeInvocation, status, requestId, parsed);
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        // Proposed technical correlation bound; not a product requirement or authenticity check.
        if (!response.Headers.TryGetValues("x-request-id", out var values)) return null;
        var ids = values.Take(2).ToArray();
        return ids.Length == 1 && ids[0].Length is > 0 and <= 128 &&
            ids[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? ids[0] : null;
    }
}
