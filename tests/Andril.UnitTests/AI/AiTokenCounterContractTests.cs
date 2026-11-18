using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Contract validation over synthetic counts, never tokenizer accuracy or live framing.</summary>
public sealed class AiTokenCounterContractTests
{
    private const string Model = "synthetic-model";
    private const string Framing = "synthetic-text-framing-v1";
    private static AiTokenCountRequest Request(string current = "Current question") =>
        new(Model, Framing, "Trusted synthetic system instructions",
            new[] { new AiPromptMessage(AiStoredMessageRole.User, "Earlier question"),
                new AiPromptMessage(AiStoredMessageRole.Assistant, "Earlier answer") }, current);
    private static AiTokenCountProvenance Provenance() => new("synthetic-counter", "fixture-v1", Framing);
    private static AiTokenEvidence Evidence(AiTokenCountRequest request, int total = 500) =>
        new(request.Model, request.Fingerprint, 100, 100, 100, 100, total - 400, total,
            AiPrototypeLimits.MaxOutputTokens, true, true);
    private static AiTokenCountResult Result(AiTokenCountRequest request, int total = 500) =>
        new(AiTokenCountState.Measured, Evidence(request, total), Provenance());

    [Fact]
    public async Task ValidSyntheticMeasurement_UsesProductionContractAndForwardsExactRequest()
    {
        var request = Request();
        var expected = Result(request);
        var counter = new SyntheticCounter((_, _) => Task.FromResult(expected));
        using var cancellation = new CancellationTokenSource();

        Assert.Same(typeof(AiPromptFactory).Assembly, typeof(AiTokenCountContract).Assembly);
        Assert.Equal(AiTokenCountValidationOutcome.ValidMeasurement, AiTokenCountContract.Validate(request, expected));
        Assert.Same(expected.Evidence, await AiTokenCountContract.CountVerifiedAsync(counter, request, cancellation.Token));
        Assert.Same(request, counter.Request);
        Assert.Equal(cancellation.Token, counter.Token);
        Assert.Equal(1, counter.Calls);
    }

    [Theory]
    [InlineData(2000, AiTokenCountValidationOutcome.ValidMeasurement)]
    [InlineData(2001, AiTokenCountValidationOutcome.Invalid)]
    public void InputCeiling_IsDelegatedToExistingBudgetPolicy(int total, AiTokenCountValidationOutcome expected)
    {
        var request = Request();
        var result = Result(request, total);
        Assert.Equal(expected, AiTokenCountContract.Validate(request, result));
        Assert.Equal(expected == AiTokenCountValidationOutcome.ValidMeasurement,
            AiBudgetPolicy.ValidTokens(result.Evidence, request.Model));
        Assert.Equal(2000, AiPrototypeLimits.MaxInputTokens);
    }

    [Theory]
    [InlineData("model-null")]
    [InlineData("model-empty")]
    [InlineData("model-space")]
    [InlineData("model-trim")]
    [InlineData("model-control")]
    [InlineData("framing-null")]
    [InlineData("framing-empty")]
    [InlineData("framing-space")]
    [InlineData("framing-trim")]
    [InlineData("framing-control")]
    public void MissingOrMalformedConfiguration_IsRejectedWithoutLeakingItsValue(string defect)
    {
        var model = Model;
        var framing = Framing;
        var value = defect.Split('-')[1] switch
        {
            "null" => null!, "empty" => "", "space" => " \t", "trim" => " private-value ",
            _ => "private-value\n"
        };
        if (defect.StartsWith("model", StringComparison.Ordinal)) model = value;
        else framing = value;
        var error = Assert.Throws<ArgumentException>(() => new AiTokenCountRequest(model, framing,
            "system", Array.Empty<AiPromptMessage>(), "current"));
        Assert.DoesNotContain("private-value", error.Message);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("current")]
    [InlineData("history")]
    [InlineData("null-message")]
    [InlineData("role")]
    [InlineData("text")]
    public void InvalidStructuredInput_IsRejected(string defect)
    {
        IReadOnlyList<AiPromptMessage> history = new[] { new AiPromptMessage(AiStoredMessageRole.User, "history") };
        if (defect == "history") history = null!;
        if (defect == "null-message") history = new AiPromptMessage[] { null! };
        if (defect == "role") history = new[] { new AiPromptMessage((AiStoredMessageRole)99, "private-value") };
        if (defect == "text") history = new[] { new AiPromptMessage(AiStoredMessageRole.Assistant, " ") };
        Assert.ThrowsAny<ArgumentException>(() => new AiTokenCountRequest(Model, Framing,
            defect == "system" ? " " : "system", history, defect == "current" ? " " : "current"));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("request")]
    [InlineData("evidence")]
    [InlineData("provenance")]
    [InlineData("counter")]
    [InlineData("counter-revision")]
    [InlineData("framing")]
    [InlineData("model")]
    [InlineData("fingerprint")]
    [InlineData("empty-fingerprint")]
    [InlineData("unverified")]
    [InlineData("reasoning")]
    [InlineData("components")]
    [InlineData("overflow")]
    [InlineData("output-ceiling")]
    [InlineData("output-binding")]
    public void MissingContradictoryOrMismatchedEvidence_FailsClosed(string defect)
    {
        AiTokenCountRequest? request = Request();
        AiTokenCountResult? result = Result(request);
        var evidence = result.Evidence!;
        result = defect switch
        {
            "result" => null,
            "evidence" => result with { Evidence = null },
            "provenance" => result with { Provenance = null },
            "counter" => result with { Provenance = Provenance() with { CounterId = " " } },
            "counter-revision" => result with { Provenance = Provenance() with { CounterRevision = "" } },
            "framing" => result with { Provenance = Provenance() with { FramingRevision = "other" } },
            "model" => result with { Evidence = evidence with { Model = "other" } },
            "fingerprint" => result with { Evidence = evidence with { RequestFingerprint = Request("changed").Fingerprint } },
            "empty-fingerprint" => result with { Evidence = evidence with { RequestFingerprint = "" } },
            "unverified" => result with { Evidence = evidence with { FullRequestVerified = false } },
            "reasoning" => result with { Evidence = evidence with { OutputIncludesReasoning = false } },
            "components" => result with { Evidence = evidence with { FullInputTokens = 499 } },
            "overflow" => result with { Evidence = evidence with { SystemTokens = int.MaxValue, HistoryTokens = int.MaxValue } },
            "output-ceiling" => result with { Evidence = evidence with { MaxBillableOutputTokens = 501 } },
            "output-binding" => result with { Evidence = evidence with { MaxBillableOutputTokens = 250 } },
            _ => result
        };
        if (defect == "request") request = null;
        Assert.Equal(AiTokenCountValidationOutcome.Invalid, AiTokenCountContract.Validate(request, result));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("history")]
    [InlineData("framing")]
    [InlineData("other")]
    [InlineData("total")]
    [InlineData("output")]
    public void NegativeCounts_AreRejectedByExistingEvidencePolicy(string component)
    {
        var request = Request();
        var evidence = Evidence(request);
        evidence = component switch
        {
            "system" => evidence with { SystemTokens = -1 },
            "user" => evidence with { UserTokens = -1 },
            "history" => evidence with { HistoryTokens = -1 },
            "framing" => evidence with { FramingTokens = -1 },
            "other" => evidence with { OtherInputTokens = -1 },
            "total" => evidence with { FullInputTokens = -1 },
            _ => evidence with { MaxBillableOutputTokens = -1 }
        };
        Assert.False(AiBudgetPolicy.ValidTokens(evidence, request.Model));
        Assert.Equal(AiTokenCountValidationOutcome.Invalid,
            AiTokenCountContract.Validate(request, new(AiTokenCountState.Measured, evidence, Provenance())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void UnknownState_IsNeverSuccess(int state) =>
        Assert.Equal(AiTokenCountValidationOutcome.Invalid,
            AiTokenCountContract.Validate(Request(), new((AiTokenCountState)state)));

    [Theory]
    [InlineData(AiTokenCountState.UnsupportedModel, AiTokenCountValidationOutcome.UnsupportedModel)]
    [InlineData(AiTokenCountState.UnsupportedConfiguration, AiTokenCountValidationOutcome.UnsupportedConfiguration)]
    [InlineData(AiTokenCountState.Unavailable, AiTokenCountValidationOutcome.Unavailable)]
    [InlineData(AiTokenCountState.Unreliable, AiTokenCountValidationOutcome.Unreliable)]
    [InlineData(AiTokenCountState.Estimated, AiTokenCountValidationOutcome.EstimateNotAccepted)]
    public async Task NonMeasuredStates_NeverBecomeZeroOrVerifiedEvidence(AiTokenCountState state,
        AiTokenCountValidationOutcome expected)
    {
        var request = Request();
        var result = new AiTokenCountResult(state);
        Assert.Null(result.Evidence);
        Assert.Equal(expected, AiTokenCountContract.Validate(request, result));
        Assert.Equal(AiTokenCountValidationOutcome.Invalid,
            AiTokenCountContract.Validate(request, result with { Evidence = Evidence(request) }));
        var counter = new SyntheticCounter((_, _) => Task.FromResult(result));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AiTokenCountContract.CountVerifiedAsync(counter, request));
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void Snapshot_AppendsCurrentOnceAndCannotBeMutatedThroughOriginalCollection()
    {
        var history = new List<AiPromptMessage> { new(AiStoredMessageRole.User, "Earlier"), new(AiStoredMessageRole.Assistant, "Answer") };
        var request = new AiTokenCountRequest(Model, Framing, "System", history, "  Current\nதமிழ் සිංහල  ");
        var fingerprint = request.Fingerprint;
        history[0] = history[0] with { Text = "Changed" };
        history.Clear();
        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("Earlier", request.Messages[0].Text);
        Assert.Equal("  Current\nதமிழ் සිංහල  ", request.Messages[request.CurrentUserMessageIndex].Text);
        Assert.Equal(AiStoredMessageRole.User, request.Messages[^1].Role);
        Assert.Single(request.Messages, message => message.Text.StartsWith("  Current", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => ((IList<AiPromptMessage>)request.Messages).Clear());
        Assert.Equal(fingerprint, request.Fingerprint);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("framing")]
    [InlineData("system")]
    [InlineData("role")]
    [InlineData("history")]
    [InlineData("current")]
    public void Fingerprint_BindsEverySupportedRequestField(string change)
    {
        var original = Request();
        var history = original.Messages.Take(2).ToArray();
        if (change == "role") history[1] = history[1] with { Role = AiStoredMessageRole.User };
        if (change == "history") history[0] = history[0] with { Text = "changed" };
        var changed = new AiTokenCountRequest(change == "model" ? "other" : Model,
            change == "framing" ? "other" : Framing,
            change == "system" ? "changed" : original.SystemInstructions, history,
            change == "current" ? "Current question " : original.Messages[^1].Text);
        Assert.Equal(original.Fingerprint, Request().Fingerprint);
        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
        Assert.Equal(AiTokenCountValidationOutcome.Invalid, AiTokenCountContract.Validate(changed, Result(original)));
    }

    [Fact]
    public void ExistingStructuredPrompt_IsReusedWithoutDuplicatingItsCurrentMessage()
    {
        var now = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var operation = Guid.NewGuid();
        // In-memory entities only; no admitted operation, attempt or repository is required.
        var conversation = AiConversationEntity.CreateForFirstUserMessage(actor, operation, now);
        var user = AiMessageEntity.CreateUserMessage(conversation, actor, operation, "Current", now);
        var prompt = AiPromptFactory.Create(actor, conversation, new[] { user }, user.Id, now, 1, _ => 100);
        var request = AiTokenCountRequest.FromPrompt(Model, Framing, prompt);
        Assert.Equal(prompt.Fingerprint, request.SourcePromptFingerprint);
        Assert.Equal(prompt.SystemInstructions, request.SystemInstructions);
        Assert.Equal(prompt.Messages, request.Messages);
        Assert.Equal("Current", Assert.Single(request.Messages).Text);
        Assert.NotEqual(prompt.Fingerprint, request.Fingerprint);
        Assert.Throws<ArgumentNullException>(() => AiTokenCountRequest.FromPrompt(Model, Framing, null!));
    }

    [Fact]
    public void CharacterValidation_IsSeparateFromTokenEvidence()
    {
        var request = Request(new string('x', 501));
        Assert.Equal(AiTokenCountValidationOutcome.ValidMeasurement, AiTokenCountContract.Validate(request, Result(request)));
        Assert.Throws<ArgumentException>(() => AiOperationPolicy.ValidateRequest(
            new AiOperationAdmissionRequest(AiModule.Learning, Guid.NewGuid(), Guid.NewGuid(), request.Messages[^1].Text)));
    }

    [Fact]
    public void MeasurementRecords_UseImmutableCopiesAndSafeDiagnostics()
    {
        const string privateText = "private-content-never-log";
        var request = Request(privateText);
        var result = Result(request);
        var changed = result with { Evidence = result.Evidence! with { UserTokens = -1 } };
        Assert.Equal(100, result.Evidence!.UserTokens);
        Assert.Equal(AiTokenCountValidationOutcome.Invalid, AiTokenCountContract.Validate(request, changed));
        foreach (var value in new object[] { request, result, result.Provenance! })
        {
            Assert.DoesNotContain(privateText, value.ToString());
            Assert.DoesNotContain(request.Fingerprint, value.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrDuringMeasurement_NeverReturnsEvidence(bool during)
    {
        using var cancellation = new CancellationTokenSource();
        var request = Request();
        var counter = new SyntheticCounter((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Result(request));
        });
        if (!during) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AiTokenCountContract.CountVerifiedAsync(counter, request, cancellation.Token));
        Assert.Equal(during ? 1 : 0, counter.Calls);
    }

    [Fact]
    public async Task PendingMeasurementCancellation_PropagatesExactTokenWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counter = new SyntheticCounter(async (_, token) =>
        {
            entered.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        var pending = AiTokenCountContract.CountVerifiedAsync(counter, Request(), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task DependencyFailure_PropagatesWithoutFallbackOrRetry()
    {
        var expected = new InvalidOperationException("Synthetic local counter unavailable");
        var counter = new SyntheticCounter((_, _) => throw expected);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AiTokenCountContract.CountVerifiedAsync(counter, Request())));
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task MissingConsumerDependencies_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => AiTokenCountContract.CountVerifiedAsync(null!, Request()));
        var counter = new SyntheticCounter((_, _) => throw new InvalidOperationException("Must not be called"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => AiTokenCountContract.CountVerifiedAsync(counter, null!));
        Assert.Equal(0, counter.Calls);
    }

    // Scripted evidence source exercises the PRODUCTION consumer; it does not tokenize text.
    private sealed class SyntheticCounter(Func<AiTokenCountRequest, CancellationToken, Task<AiTokenCountResult>> count)
        : IAiTokenCounter
    {
        public int Calls { get; private set; }
        public AiTokenCountRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            Token = cancellationToken;
            return count(request, cancellationToken);
        }
    }
}
