using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Providers.OpenAi;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Actual unavailable-boundary behavior, not tokenizer accuracy. No numeric production
/// measurement is expected; synthetic validation/2,000-token cases live in the adapter tests.</summary>
public sealed class AiProviderTokenCounterTests
{
    private static AiTokenCountRequest Request(string model = OpenAiResponsesRequestSerializer.ApprovedModel,
        string framing = OpenAiResponsesRequestSerializer.SerializationRevision, string text = "Private தமிழ் සිංහල") =>
        new(model, framing, "Private system", [], text);
    private static AiPreparedProviderPayload Payload(AiTokenCountRequest r) =>
        OpenAiResponsesRequestSerializer.Prepare(r, AiTextResponseProvider.Candidate(r)).MeasurementPayload;

    [Theory]
    [InlineData("English")]
    [InlineData("தமிழ்")]
    [InlineData("සිංහල")]
    [InlineData("Tanglish mixed தமிழ் 👩🏽‍💻")]
    public async Task PinnedExactPayloadReturnsUnavailableNotInventedNumbers(string text)
    {
        var request = Request(text: text); var payload = Payload(request);
        var before = payload.CopyUtf8Payload();
        var result = await new AiProviderTokenCounter().CountAsync(request, payload);
        Assert.Equal(AiTokenCountState.Unavailable, result.State);
        Assert.Null(result.Evidence); Assert.Null(result.Provenance); Assert.Null(result.PayloadBinding);
        Assert.Equal(before, payload.CopyUtf8Payload());
        Assert.DoesNotContain(text, result.ToString());
        Assert.NotEqual(AiTokenCountValidationOutcome.ValidMeasurement, AiTokenCountContract.Validate(request, result, payload));
    }

    [Theory]
    [InlineData("model", AiTokenCountState.UnsupportedModel)]
    [InlineData("framing", AiTokenCountState.UnsupportedConfiguration)]
    [InlineData("configuration", AiTokenCountState.UnsupportedConfiguration)]
    [InlineData("stale", AiTokenCountState.Unreliable)]
    [InlineData("bytes", AiTokenCountState.Unreliable)]
    [InlineData("generic", AiTokenCountState.Unreliable)]
    public async Task UnsupportedOrInconsistentInputFailsClosed(string defect, AiTokenCountState expected)
    {
        var request = Request(); var payload = Payload(request);
        switch (defect)
        {
            case "model": request = Request(model: "unapproved-model"); break;
            case "framing": request = Request(framing: "stale-framing"); break;
            case "configuration": payload = new(request, payload.CopyUtf8Payload(), "unapproved-config"); break;
            case "stale": request = Request(text: "different private current"); break;
            case "bytes":
                var bytes = payload.CopyUtf8Payload(); bytes[0] ^= 1;
                payload = new(request, bytes, payload.Binding.ExecutionConfiguration); break;
            default: payload = new(request, "{}"u8, payload.Binding.ExecutionConfiguration); break;
        }
        var result = await new AiProviderTokenCounter().CountAsync(request, payload);
        Assert.Equal(expected, result.State);
        Assert.Null(result.Evidence); Assert.Null(result.Provenance); Assert.Null(result.PayloadBinding);
    }

    [Fact]
    public async Task CancellationAndMissingInputAreRejected()
    {
        var r = Request(); var p = Payload(r); var counter = new AiProviderTokenCounter();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(r, p, cts.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => counter.CountAsync(null!, p));
        await Assert.ThrowsAsync<ArgumentNullException>(() => counter.CountAsync(r, null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AiTokenCountContract.CountVerifiedAsync(counter, r, p));
    }
}
