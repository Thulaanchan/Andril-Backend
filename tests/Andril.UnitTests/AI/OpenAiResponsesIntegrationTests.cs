using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Offline composition of production components. Scripted handlers cannot use sockets;
/// all actors, credentials, messages and counts are synthetic. Counts do not prove tokenizer accuracy.</summary>
public sealed class OpenAiResponsesIntegrationTests
{
    private const string SyntheticCredential = "synthetic-only-never-real";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(30); // Test setting, not product approval.

    [Fact]
    public async Task Correction_ReadOnlyPreAdmissionUsesExactOpenAiPayloadWithoutTransportOrPersistence()
    {
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var conversation = Guid.NewGuid();
        var revision = Guid.NewGuid();
        var context = AiPreAdmissionPromptContext.ForNewConversation(actor, conversation, revision);
        var counter = new SyntheticCounter();
        var repository = new ForbiddenRepository();
        var handler = new Handler((_, _) => throw new InvalidOperationException("HTTP must not execute"));
        OpenAiPreparedResponsesRequest? retained = null;
        var prepared = await AiPromptFactory.PrepareBeforeAdmissionAsync(context, Guid.NewGuid(), "Unsaved private current", 100,
            async (prompt, token) =>
            {
                var request = AiTokenCountRequest.FromPrompt(OpenAiResponsesRequestSerializer.ApprovedModel,
                    OpenAiResponsesRequestSerializer.SerializationRevision, prompt);
                retained = OpenAiResponsesRequestSerializer.Prepare(request,
                    AiProviderRequestCandidate.FromMeasuredRequest(request, Settings()));
                var result = await AiTokenCountContract.CountVerifiedAsync(counter, request, retained.MeasurementPayload, token);
                return new(request, retained.MeasurementPayload, result);
            });
        Assert.NotNull(retained);
        Assert.Same(retained.MeasurementPayload, counter.Payload);
        Assert.Equal(retained.CopyUtf8Payload(), prepared.Measurement.Payload.CopyUtf8Payload());
        Assert.Equal(retained.PayloadSha256, prepared.Measurement.Result.PayloadBinding!.PayloadSha256);
        Assert.Equal(1, counter.Calls);
        Assert.Equal(0, repository.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.True(prepared.MatchesCurrentContext(actor, conversation, revision));
        Assert.False(prepared.MatchesCurrentContext(actor, conversation, Guid.NewGuid()));
    }

    [Fact]
    public void Correction_TransportRemainsInternalAndCannotImplementLegacyExecutionInterface()
    {
        Assert.False(typeof(OpenAiResponsesHttpTransport).IsPublic);
        Assert.False(typeof(OpenAiResponsesResultMapper).IsPublic);
        Assert.False(typeof(IAiResponseProvider).IsAssignableFrom(typeof(OpenAiResponsesHttpTransport)));
        Assert.True(typeof(Andril.AI.Providers.MockAiResponseProvider).IsSealed);
        var friends = typeof(AiChatService).Assembly.GetCustomAttributes(
            typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false)
            .Cast<System.Runtime.CompilerServices.InternalsVisibleToAttribute>().Select(a => a.AssemblyName).ToArray();
        Assert.Equal(new[] { "Andril.UnitTests" }, friends);
    }

    [Theory]
    [InlineData(AiModule.Learning, "English question", "  English answer\n")]
    [InlineData(AiModule.Hobbies, "வணக்கம் தமிழ்", "  தமிழ் பதில்\n")]
    [InlineData(AiModule.Social, "Vanakkam explain pannunga", "Tanglish answer")]
    [InlineData(AiModule.Learning, "ආයුබෝවන් සිංහල", "සිංහල පිළිතුර 👩🏽‍💻 e\u0301")]
    public async Task CompletePipelinePreservesMeasuredBytesLanguagesAndLocalBindings(AiModule module, string question, string answer)
    {
        var fixture = new Fixture(module, question);
        var counter = new SyntheticCounter();
        fixture.Measurement = await AiTokenCountContract.CountVerifiedAsync(counter, fixture.Request, fixture.Prepared.MeasurementPayload);
        Assert.Same(fixture.Prepared.MeasurementPayload, counter.Payload);
        Assert.Equal(AiProviderRequestValidationOutcome.ValidatedCandidate,
            AiProviderRequestPolicy.Validate(fixture.Request, fixture.Candidate, fixture.Measurement, fixture.Prepared));
        var bytes = fixture.Prepared.CopyUtf8Payload();
        var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync(token));
            using var json = JsonDocument.Parse(bytes);
            var input = json.RootElement.GetProperty("input");
            Assert.Equal(fixture.Prompt.SystemInstructions, input[0].GetProperty("content").GetString());
            Assert.Equal(question, input[1].GetProperty("content").GetString());
            Assert.Equal(2, input.GetArrayLength());
            var response = Http(200, Body(answer));
            response.Headers.Add("x-request-id", "req_synthetic-123");
            return response;
        });
        var result = await fixture.ExchangeAsync(handler);
        Assert.Equal(OpenAiMappedResultKind.CompletedTextCandidate, result.Kind);
        Assert.Equal(answer, result.CandidateText);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
        Assert.Equal(fixture.Prepared.MeasurementPayload.Binding, result.LocalBinding);
        Assert.Equal(fixture.Prompt.Fingerprint, result.LocalBinding!.LogicalPromptFingerprint);
        Assert.Equal(fixture.Request.Fingerprint, result.LocalBinding.RequestFingerprint);
        Assert.NotEqual(result.LocalBinding.PayloadSha256, result.LocalBinding.RequestFingerprint);
        Assert.Equal("resp_synthetic", result.ResponseId);
        Assert.Equal("req_synthetic-123", result.ProviderRequestId);
        Assert.Equal(fixture.Request.Model, result.ReturnedModel);
        Assert.Equal(1, counter.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(bytes, fixture.Prepared.CopyUtf8Payload());
        Assert.DoesNotContain("private reasoning", result.CandidateText);
        foreach (var privateValue in new[] { answer.Trim(), question, SyntheticCredential, fixture.Actor.UserId.ToString(), fixture.Request.Fingerprint })
            Assert.DoesNotContain(privateValue, result.ToString());
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("token-request")]
    [InlineData("model")]
    [InlineData("framing")]
    [InlineData("settings")]
    [InlineData("unavailable")]
    [InlineData("unreliable")]
    [InlineData("estimate")]
    [InlineData("unverified")]
    [InlineData("missing-binding")]
    public async Task InvalidRequestsCannotInvokeTheSimulatedHandler(string defect)
    {
        var fixture = new Fixture();
        if (defect == "bytes")
        {
            var copy = fixture.Prepared.CopyUtf8Payload();
            copy[0] ^= 1;
            Assert.False(fixture.Prepared.MatchesPayload(copy));
            var altered = new AiPreparedProviderPayload(fixture.Request, copy,
                fixture.Prepared.MeasurementPayload.Binding.ExecutionConfiguration);
            fixture.Measurement = fixture.Measurement with { PayloadBinding = altered.Binding };
        }
        if (defect == "token-request") fixture.Measurement = fixture.Measurement with
            { Evidence = fixture.Measurement.Evidence! with { RequestFingerprint = "another-request" } };
        if (defect == "model") fixture.Measurement = fixture.Measurement with
            { PayloadBinding = fixture.Measurement.PayloadBinding! with { Model = "unapproved-model" } };
        if (defect == "framing") fixture.Candidate = new(fixture.Request.Model, "unapproved-framing",
            fixture.Request.SystemInstructions, fixture.Request.Messages, 500, fixture.Request.Fingerprint, Settings(), fixture.Request.SourcePromptFingerprint);
        if (defect == "settings") fixture.Candidate = AiProviderRequestCandidate.FromMeasuredRequest(fixture.Request, Settings() with { Capabilities = AiProviderCapabilities.ExternalTools });
        if (defect is "unavailable" or "unreliable" or "estimate") fixture.Measurement = new(defect switch
            { "unavailable" => AiTokenCountState.Unavailable, "unreliable" => AiTokenCountState.Unreliable, _ => AiTokenCountState.Estimated });
        if (defect == "unverified") fixture.Measurement = fixture.Measurement with
            { Evidence = fixture.Measurement.Evidence! with { FullRequestVerified = false } };
        if (defect == "missing-binding") fixture.Measurement = fixture.Measurement with { PayloadBinding = null };
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not invoke"));
        var mapped = await fixture.ExchangeAsync(handler);
        Assert.Equal(OpenAiMappedResultKind.InvalidRequestBinding, mapped.Kind);
        Assert.Equal(OpenAiTransportOutcome.InvalidPreparedRequest, mapped.TransportOutcome);
        Assert.Equal(0, handler.Calls);
        Assert.Null(mapped.CandidateText);
        Assert.Null(mapped.LocalBinding);
        Assert.False(mapped.HttpInvocationStarted);
    }

    [Theory]
    [InlineData("completed", "CompletedTextCandidate", AiTextCompletionState.Completed, AiTextValidationOutcome.Invalid)]
    [InlineData("refused", "Refused", AiTextCompletionState.Refused, AiTextValidationOutcome.Refused)]
    [InlineData("incomplete", "Incomplete", AiTextCompletionState.Incomplete, AiTextValidationOutcome.Incomplete)]
    [InlineData("failed", "ProviderFailure", AiTextCompletionState.Failed, AiTextValidationOutcome.Failed)]
    [InlineData("cancelled", "CancelledDuringExecution", AiTextCompletionState.Cancelled, AiTextValidationOutcome.Cancelled)]
    [InlineData("future", "UncertainExecution", AiTextCompletionState.Uncertain, AiTextValidationOutcome.Uncertain)]
    public async Task ModelCompletionAndTransportClassificationRemainSeparate(string state, string expected,
        AiTextCompletionState completion, AiTextValidationOutcome policy)
    {
        var fixture = new Fixture();
        var body = Body("private answer");
        body["status"] = state == "refused" ? "completed" : state;
        if (state == "refused") body["output"]![1]!["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "private refusal" });
        if (state == "incomplete") body["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
        if (state == "failed") body["error"] = new JsonObject { ["code"] = "server_error", ["message"] = "private provider error" };
        var handler = new Handler((_, _) => Task.FromResult(Http(200, body)));
        var mapped = await fixture.ExchangeAsync(handler);
        Assert.Equal(Enum.Parse<OpenAiMappedResultKind>(expected), mapped.Kind);
        Assert.Equal(OpenAiTransportOutcome.ResponseReceived, mapped.TransportOutcome);
        Assert.Equal(completion, mapped.Completion);
        Assert.Equal(policy, mapped.TextPolicyOutcome);
        if (state != "completed") Assert.Null(mapped.CandidateText);
        if (state == "refused") Assert.Equal("private refusal", mapped.Refusal);
        if (state == "incomplete") Assert.Equal(OpenAiIncompleteReason.MaxOutputTokens, mapped.IncompleteReason);
        Assert.NotNull(mapped.Usage);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("private", mapped.ToString().Replace("private details omitted", "omitted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("malformed", "InvalidResponse")]
    [InlineData("tool", "UnsupportedOutput")]
    [InlineData("wrong-model", "InvalidResponse")]
    public async Task InvalidParsedOutputCannotBecomeCandidateText(string defect, string expected)
    {
        var fixture = new Fixture();
        var body = Body("private answer");
        if (defect == "tool") body["output"] = new JsonArray(new JsonObject { ["type"] = "function_call", ["name"] = "do_not_execute" });
        if (defect == "wrong-model") body["model"] = "gpt-6-luna-unapproved-snapshot";
        var handler = new Handler((_, _) => Task.FromResult(defect == "malformed"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{private-malformed", Encoding.UTF8, "application/json") }
            : Http(200, body)));
        var result = await fixture.ExchangeAsync(handler);
        Assert.Equal(Enum.Parse<OpenAiMappedResultKind>(expected), result.Kind);
        Assert.Equal(OpenAiTransportOutcome.InvalidResponse, result.TransportOutcome);
        Assert.Null(result.CandidateText);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
        if (defect != "malformed") Assert.Equal(1000, result.Usage!.InputTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("missing")]
    [InlineData("over-input")]
    [InlineData("over-output")]
    [InlineData("missing-details")]
    public async Task ObservedUsageIsPreservedWithoutVerificationClampingOrDoubleCounting(string scenario)
    {
        var fixture = new Fixture();
        var body = Body("Candidate text");
        if (scenario == "missing") body.Remove("usage");
        if (scenario == "over-input") { body["usage"]!["input_tokens"] = 2001; body["usage"]!["total_tokens"] = 2101; }
        if (scenario == "over-output") { body["usage"]!["output_tokens"] = 501; body["usage"]!["total_tokens"] = 1501; }
        if (scenario == "missing-details") { body["usage"]!.AsObject().Remove("input_tokens_details"); body["usage"]!.AsObject().Remove("output_tokens_details"); }
        var handler = new Handler((_, _) => Task.FromResult(Http(200, body)));
        var result = await fixture.ExchangeAsync(handler);
        Assert.Equal(OpenAiMappedResultKind.CompletedTextCandidate, result.Kind);
        Assert.Equal("Candidate text", result.CandidateText);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
        if (scenario == "missing")
        {
            Assert.Null(result.Usage);
            Assert.Equal(OpenAiUsageObservationState.Missing, result.UsageState);
            return;
        }
        var usage = result.Usage!;
        Assert.Equal(OpenAiUsageObservationState.Observed, result.UsageState);
        Assert.Equal(scenario == "over-input" ? 2001 : 1000, usage.InputTokens);
        Assert.Equal(scenario == "over-output" ? 501 : 100, usage.OutputTokens);
        Assert.Equal(usage.InputTokens + usage.OutputTokens, usage.TotalTokens);
        Assert.Equal(scenario is "over-input" or "over-output", usage.ExceedsPrototypeLimits);
        if (scenario == "missing-details")
        {
            Assert.Null(usage.ReasoningTokens);
            Assert.Null(usage.CachedInputTokens);
            Assert.Null(usage.CacheWriteTokens);
            Assert.Null(usage.VisibleOutputTokens);
        }
        else
        {
            Assert.Equal(20, usage.ReasoningTokens);
            Assert.Equal(usage.OutputTokens - 20, usage.VisibleOutputTokens);
            Assert.Equal(30, usage.CachedInputTokens);
            Assert.Equal(20, usage.CacheWriteTokens);
            Assert.Equal(15, usage.CacheWrite30MinuteTokens);
            Assert.Equal(5, usage.CacheWrite12HourTokens);
        }
    }

    [Theory]
    [InlineData(400, "RequestRejected")]
    [InlineData(401, "AuthenticationRejected")]
    [InlineData(403, "AccessRejected")]
    [InlineData(404, "RequestRejected")]
    [InlineData(408, "Timeout")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "ProviderFailure")]
    [InlineData(503, "ProviderFailure")]
    [InlineData(504, "Timeout")]
    [InlineData(302, "UncertainExecution")]
    [InlineData(599, "UncertainExecution")]
    public async Task HttpOutcomesNeverAuthorizeRedispatchOrAccountingRelease(int status, string expected)
    {
        var fixture = new Fixture();
        var handler = new Handler((_, _) => Task.FromResult(Http(status, Body("private error"))));
        var result = await fixture.ExchangeAsync(handler);
        Assert.Equal(Enum.Parse<OpenAiMappedResultKind>(expected), result.Kind);
        Assert.Equal(status, result.HttpStatusCode);
        Assert.True(result.HttpInvocationStarted);
        Assert.Null(result.CandidateText);
        Assert.Null(result.Usage); // Missing, not a verified zero or evidence for budget release.
        Assert.Equal(OpenAiUsageObservationState.Missing, result.UsageState);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("before", "CancelledBeforeInvocation")]
    [InlineData("during", "CancelledDuringExecution")]
    [InlineData("timeout", "Timeout")]
    [InlineData("connection", "TransportFailure")]
    public async Task InterruptedExchangesRetainUncertaintyWithoutRetry(string scenario, string expected)
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (scenario == "before") cancellation.Cancel();
        var handler = new Handler((_, token) =>
        {
            if (scenario == "during") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (scenario == "timeout") throw new OperationCanceledException("synthetic client timeout", new TimeoutException(), token);
            throw new HttpRequestException("synthetic connection failure");
        });
        var result = await fixture.ExchangeAsync(handler, cancellation.Token);
        Assert.Equal(Enum.Parse<OpenAiMappedResultKind>(expected), result.Kind);
        Assert.Equal(scenario != "before", result.HttpInvocationStarted);
        Assert.Equal(scenario == "before" ? 0 : 1, handler.Calls);
        Assert.Null(result.CandidateText);
        Assert.Null(result.Usage);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
    }

    [Fact]
    public async Task MappingIsRepeatableAndLocalContextCannotBeReplacedByProviderClaims()
    {
        var fixture = new Fixture();
        var handler = new Handler((_, _) => Task.FromResult(Http(200, Body("Exact candidate"))));
        var first = await fixture.ExchangeAsync(handler);
        var transport = fixture.LastTransport!;
        var second = fixture.Map(transport);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
        var mismatched = fixture.Measurement with { PayloadBinding = fixture.Measurement.PayloadBinding! with { RequestFingerprint = "provider-claimed-fingerprint" } };
        var rejected = OpenAiResponsesResultMapper.Map(fixture.Request, fixture.Candidate, mismatched, fixture.Prepared, transport);
        Assert.Equal(OpenAiMappedResultKind.InvalidRequestBinding, rejected.Kind);
        Assert.Null(rejected.CandidateText);
        Assert.Same(first.Usage, rejected.Usage); // Keep observations for later trusted reconciliation.
        Assert.Null(rejected.LocalBinding);
    }

    [Fact]
    public void MissingOrContradictoryTransportCannotBePromotedByParsedText()
    {
        var fixture = new Fixture();
        var parsed = OpenAiResponsesResponseParser.Parse(Body("private candidate").ToJsonString(), fixture.Request.Model);
        Assert.Equal(OpenAiMappedResultKind.InvalidResponse, fixture.Map(null).Kind);
        foreach (var transport in new[]
        {
            new OpenAiTransportResult(OpenAiTransportOutcome.ResponseReceived, Response: parsed),
            new OpenAiTransportResult(OpenAiTransportOutcome.ResponseReceived, OpenAiTransportPhase.Complete, true, 401, Response: parsed),
            new OpenAiTransportResult((OpenAiTransportOutcome)999, Response: parsed)
        })
        {
            var mapped = fixture.Map(transport);
            Assert.Equal(OpenAiMappedResultKind.InvalidResponse, mapped.Kind);
            Assert.Null(mapped.CandidateText);
            Assert.NotNull(mapped.Usage);
        }
        var timeout = fixture.Map(new(OpenAiTransportOutcome.Timeout, OpenAiTransportPhase.ReadingBody, true, 200, Response: parsed));
        Assert.Equal(OpenAiMappedResultKind.Timeout, timeout.Kind);
        Assert.Null(timeout.CandidateText);
        Assert.Equal(AiTextValidationOutcome.Invalid, timeout.TextPolicyOutcome);
    }

    private static AiProviderExecutionSettings Settings() => new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false);
    [Fact]
    public async Task MappedCandidateCannotEnableLegacyExecutionOrPersistenceBypasses()
    {
        var fixture = new Fixture();
        var handler = new Handler((_, _) => Task.FromResult(Http(200, Body("Private candidate"))));
        var result = await fixture.ExchangeAsync(handler);
        Assert.Equal(OpenAiMappedResultKind.CompletedTextCandidate, result.Kind);
        Assert.Equal(AiTextValidationOutcome.Invalid, result.TextPolicyOutcome);
        var authorization = new Authorization(fixture.Actor);
        var arbitrary = new CandidateProvider(result.CandidateText!);
        var chat = new AiChatService(authorization, arbitrary);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.ReplyAsync(fixture.Actor.Module, "Synthetic question"));
        Assert.Equal(0, arbitrary.Calls);
        var repository = new ForbiddenRepository();
        var lifecycle = new AiConversationLifecycleService(authorization, repository);
        var authCalls = authorization.Calls;
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartConversationAsync(fixture.Actor.Module, Guid.NewGuid(), "Synthetic user"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.ContinueConversationAsync(fixture.Actor.Module, Guid.NewGuid(), Guid.NewGuid(), "Synthetic user"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SaveAssistantReplyAsync(fixture.Actor.Module, Guid.NewGuid(), Guid.NewGuid(), result.CandidateText));
        Assert.Equal(0, repository.Calls);
        Assert.Equal(authCalls, authorization.Calls);
        // Repeated mapping has no dependency for dispatch, quota settlement or budget release.
        Assert.Equal(result, fixture.Map(fixture.LastTransport));
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Authorization(AiAuthorizedActor actor) : IAiActorAuthorization
    {
        internal int Calls { get; private set; }
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<AiAuthorizedActor?>(actor); }
    }
    private sealed class CandidateProvider(string text) : IAiResponseProvider
    {
        internal int Calls { get; private set; }
        public Task<AiProviderResponse> GenerateAsync(AiProviderPrompt prompt, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new AiProviderResponse(text, false)); }
    }
    private sealed class ForbiddenRepository : IAiConversationRepository
    {
        internal int Calls { get; private set; }
        private Exception Called() { Calls++; return new InvalidOperationException("Storage must not execute"); }
        public Task<AiStoredConversation> CreateWithUserMessageAsync(AiAuthorizedActor actor, Guid operationId, string validatedUserText, CancellationToken cancellationToken = default) => throw Called();
        public Task<AiStoredConversation?> FindAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken cancellationToken = default) => throw Called();
        public Task<IReadOnlyList<AiStoredConversation>> ListAsync(AiAuthorizedActor actor, int limit, CancellationToken cancellationToken = default) => throw Called();
        public Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(AiAuthorizedActor actor, Guid conversationId, int limit, CancellationToken cancellationToken = default) => throw Called();
        public Task<AiStoredAiMessage> AppendUserMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string validatedUserText, CancellationToken cancellationToken = default) => throw Called();
        public Task<AiStoredAiMessage> AppendAssistantMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string assistantText, CancellationToken cancellationToken = default) => throw Called();
        public Task DeleteAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken cancellationToken = default) => throw Called();
    }

    private static JsonObject Body(string answer) => new()
    {
        ["object"] = "response", ["id"] = "resp_synthetic", ["model"] = OpenAiResponsesRequestSerializer.ApprovedModel, ["status"] = "completed",
        ["output"] = new JsonArray(
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_synthetic", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "private reasoning" }) },
            new JsonObject { ["type"] = "message", ["id"] = "msg_synthetic", ["role"] = "assistant", ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer }) }),
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = 1000, ["output_tokens"] = 100, ["total_tokens"] = 1100,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 30, ["cache_write_tokens"] = 20,
                ["cache_write_tokens_details"] = new JsonObject { ["ttl_30m_tokens"] = 15, ["ttl_12h_tokens"] = 5 } },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 20 }
        }
    };
    private static HttpResponseMessage Http(int status, JsonObject body) => new((HttpStatusCode)status)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed class Fixture
    {
        internal AiAuthorizedActor Actor { get; }
        internal AiConstructedPrompt Prompt { get; }
        internal AiTokenCountRequest Request { get; }
        internal AiProviderRequestCandidate Candidate { get; set; }
        internal OpenAiPreparedResponsesRequest Prepared { get; }
        internal AiTokenCountResult Measurement { get; set; }
        internal OpenAiTransportResult? LastTransport { get; private set; }
        internal Fixture(AiModule module = AiModule.Learning, string text = "Synthetic question")
        {
            var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
            Actor = new(Guid.NewGuid(), Guid.NewGuid(), module);
            var operation = Guid.NewGuid();
            var conversation = AiConversationEntity.CreateForFirstUserMessage(Actor, operation, now);
            var message = AiMessageEntity.CreateUserMessage(conversation, Actor, operation, text, now);
            // Pure in-memory authorized-context fixture; no admission or persistent conversation.
            Prompt = AiPromptFactory.Create(Actor, conversation, new[] { message }, message.Id, now, 1, _ => 1000);
            Request = AiTokenCountRequest.FromPrompt(OpenAiResponsesRequestSerializer.ApprovedModel,
                OpenAiResponsesRequestSerializer.SerializationRevision, Prompt);
            Candidate = AiProviderRequestCandidate.FromMeasuredRequest(Request, Settings());
            Prepared = OpenAiResponsesRequestSerializer.Prepare(Request, Candidate);
            Measurement = SyntheticCounter.Result(Request, Prepared.MeasurementPayload);
        }
        internal async Task<OpenAiMappedResult> ExchangeAsync(Handler handler, CancellationToken token = default)
        {
            using var client = new HttpClient(handler);
            var transport = new OpenAiResponsesHttpTransport(client, TestDeadline);
            LastTransport = await transport.SendAsync(Request, Candidate, Measurement, Prepared,
                new OpenAiTransportCredential(SyntheticCredential), token);
            return Map(LastTransport);
        }
        internal OpenAiMappedResult Map(OpenAiTransportResult? transport) =>
            OpenAiResponsesResultMapper.Map(Request, Candidate, Measurement, Prepared, transport);
    }
    private sealed class SyntheticCounter : IAiTokenCounter
    {
        internal int Calls { get; private set; }
        internal AiPreparedProviderPayload? Payload { get; private set; }
        internal static AiTokenCountResult Result(AiTokenCountRequest request, AiPreparedProviderPayload payload) =>
            new(AiTokenCountState.Measured, new(request.Model, request.Fingerprint, 200, 200, 200, 200, 200, 1000, 500, true, true),
                new("synthetic-counter", "fixture-v1", request.FramingRevision), payload.Binding);
        public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest request, AiPreparedProviderPayload payload, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Payload = payload; return Task.FromResult(Result(request, payload)); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
    }
}
