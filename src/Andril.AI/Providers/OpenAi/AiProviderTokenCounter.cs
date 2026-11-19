using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Providers.OpenAi;

/// <summary>Unregistered local boundary, not a tokenizer. Only the existing pinned model and
/// exact closed serializer framing are recognized. No approved full-request counting engine is
/// available: even valid input returns Unavailable without numeric evidence or provenance.
/// Never use JSON length, word counts, caller-supplied counts or a remote counting endpoint.
/// Real measurement requires separately approved implementation and accuracy/framing vectors.</summary>
internal sealed class AiProviderTokenCounter : IAiTokenCounter
{
    public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest request, AiPreparedProviderPayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payload);
        if (request.Model != OpenAiResponsesRequestSerializer.ApprovedModel)
            return Result(AiTokenCountState.UnsupportedModel);
        if (request.FramingRevision != OpenAiResponsesRequestSerializer.SerializationRevision ||
            payload.Binding.ExecutionConfiguration != OpenAiResponsesRequestSerializer.ExecutionConfiguration)
            return Result(AiTokenCountState.UnsupportedConfiguration);
        if (!payload.MatchesRequest(request)) return Result(AiTokenCountState.Unreliable);
        try
        {
            // This independent rendering checks schema/content correspondence BEFORE any count.
            // It is not used for dispatch, and its byte length is never interpreted as tokens.
            var expected = OpenAiResponsesRequestSerializer.Prepare(request,
                AiTextResponseProvider.Candidate(request));
            if (!expected.MatchesPayload(payload.CopyUtf8Payload()) ||
                expected.MeasurementPayload.Binding != payload.Binding)
                return Result(AiTokenCountState.Unreliable);
        }
        catch (InvalidOperationException) { return Result(AiTokenCountState.Unreliable); }
        cancellationToken.ThrowIfCancellationRequested();
        return Result(AiTokenCountState.Unavailable);
    }

    private static Task<AiTokenCountResult> Result(AiTokenCountState state) => Task.FromResult(new AiTokenCountResult(state));
    public override string ToString() => "AI local counting boundary [measurement unavailable]";
}
