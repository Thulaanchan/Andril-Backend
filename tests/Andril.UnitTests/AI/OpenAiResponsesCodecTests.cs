using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Synthetic embedded fixtures only. No tokenizer, provider transport or invoice proof.</summary>
public sealed class OpenAiResponsesCodecTests
{
    private static AiProviderExecutionSettings Settings() =>
        new(false, false, AiTextOutputKind.Text, AiProviderCapabilities.None, false);
    private static AiTokenCountRequest Request(string current = "Current synthetic question") =>
        new(OpenAiResponsesRequestSerializer.ApprovedModel, OpenAiResponsesRequestSerializer.SerializationRevision,
            "Trusted synthetic system instructions", new[]
            {
                new AiPromptMessage(AiStoredMessageRole.User, "Earlier synthetic question"),
                new AiPromptMessage(AiStoredMessageRole.Assistant, "Earlier synthetic answer")
            }, current);
    private static OpenAiPreparedResponsesRequest Prepare(AiTokenCountRequest request) =>
        OpenAiResponsesRequestSerializer.Prepare(request, AiProviderRequestCandidate.FromMeasuredRequest(request, Settings()));
    [Theory]
    [InlineData("output")]
    [InlineData("status")]
    [InlineData("error")]
    [InlineData("message")]
    [InlineData("object")]
    public void Correction_UnusableContentDoesNotDiscardIndependentUsage(string defect)
    {
        var root = Root();
        switch (defect)
        {
            case "output": root["output"] = "invalid"; break;
            case "status": root["status"] = null; break;
            case "error": root["error"] = new JsonObject { ["code"] = "private-error", ["message"] = "sensitive" }; break;
            case "message": root["output"]![0]!["role"] = "user"; break;
            default: root["object"] = "invalid"; break;
        }
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, parsed.Outcome);
        Assert.Null(parsed.AssistantText);
        Assert.Equal(OpenAiUsageObservationState.Observed, parsed.UsageState);
        Assert.Equal(1000, parsed.Usage!.InputTokens);
        Assert.Equal(100, parsed.Usage.OutputTokens);
        Assert.Equal(20, parsed.Usage.CacheWriteTokens);
        Assert.Equal(15, parsed.Usage.CacheWrite30MinuteTokens);
        Assert.Equal(5, parsed.Usage.CacheWrite12HourTokens);
        Assert.NotEqual(AiTextValidationOutcome.ValidText, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Fact]
    public void Correction_MalformedOrAmbiguousJsonCannotRecoverUsageFragments()
    {
        var malformed = OpenAiResponsesResponseParser.Parse(SyntheticResponse[..^1], Request().Model);
        Assert.Equal(OpenAiUsageObservationState.Unknown, malformed.UsageState);
        Assert.Null(malformed.Usage);
        var ambiguous = SyntheticResponse.Replace("\"input_tokens\":1000", "\"input_tokens\":1000,\"input_tokens\":1000");
        var parsed = OpenAiResponsesResponseParser.Parse(ambiguous, Request().Model);
        Assert.Null(parsed.Usage);
        Assert.Equal(OpenAiUsageObservationState.Unknown, parsed.UsageState);
    }

    [Fact]
    public async Task PreparedOpenAiPayloadFlowsUnchangedThroughSyntheticCountingAndValidation()
    {
        var request = Request("தமிழ் සිංහල escaped \" 👩🏽‍💻");
        var candidate = AiProviderRequestCandidate.FromMeasuredRequest(request, Settings());
        var prepared = OpenAiResponsesRequestSerializer.Prepare(request, candidate);
        var bytes = prepared.CopyUtf8Payload();
        var counter = new RetainedPayloadCounter(prepared.MeasurementPayload);
        var result = await AiTokenCountContract.CountVerifiedAsync(counter, request, prepared.MeasurementPayload);
        Assert.Equal(AiTokenCountValidationOutcome.ValidMeasurement,
            AiTokenCountContract.Validate(request, result, prepared.MeasurementPayload));
        Assert.Equal(AiProviderRequestValidationOutcome.ValidatedCandidate,
            AiProviderRequestPolicy.Validate(request, candidate, result, prepared));
        Assert.Equal(bytes, prepared.CopyUtf8Payload());
        Assert.True(prepared.MatchesPayload(bytes));
        Assert.Equal(prepared.PayloadSha256, result.PayloadBinding!.PayloadSha256);
        Assert.Equal(request.Fingerprint, result.PayloadBinding.RequestFingerprint);
        Assert.Equal(OpenAiResponsesRequestSerializer.ExecutionConfiguration, result.PayloadBinding.ExecutionConfiguration);
        Assert.Equal(1, counter.Calls);
    }

    private sealed class RetainedPayloadCounter(AiPreparedProviderPayload expected) : IAiTokenCounter
    {
        public int Calls { get; private set; }
        public Task<AiTokenCountResult> CountAsync(AiTokenCountRequest request, AiPreparedProviderPayload payload,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Same(expected, payload);
            Assert.True(payload.MatchesRequest(request));
            return Task.FromResult(new AiTokenCountResult(AiTokenCountState.Measured,
                new(request.Model, request.Fingerprint, 400, 400, 400, 400, 400, 2000, 500, true, true),
                new("synthetic-counter", "fixture-v1", request.FramingRevision), payload.Binding));
        }
    }

    [Fact]
    public void PreparationNeedsNoMeasurementAndExportsOnlyImmutableCountingEvidence()
    {
        var request = Request();
        var prepared = Prepare(request);
        var payload = prepared.MeasurementPayload;
        Assert.Same(payload, prepared.MeasurementPayload);
        Assert.True(payload.MatchesRequest(request));
        Assert.Equal(prepared.PayloadSha256, payload.Binding.PayloadSha256);
        Assert.Equal(prepared.CopyUtf8Payload(), payload.CopyUtf8Payload());
        var copy = payload.CopyUtf8Payload();
        copy[0] ^= 1;
        Assert.False(prepared.MatchesPayload(copy));
        Assert.True(prepared.MatchesPayload(payload.CopyUtf8Payload()));
        Assert.Equal(AiProviderRequestValidationOutcome.TokenEvidenceUnavailable,
            AiProviderRequestPolicy.Validate(request, AiProviderRequestCandidate.FromMeasuredRequest(request, Settings()), null, prepared));
        foreach (var value in new object[] { prepared, payload, payload.Binding })
        {
            Assert.DoesNotContain(request.SystemInstructions, value.ToString());
            Assert.DoesNotContain(request.Fingerprint, value.ToString());
            Assert.DoesNotContain(prepared.PayloadSha256, value.ToString());
        }
    }

    private static AiTextResponseExpectation LocalExpectation() =>
        new(OpenAiResponsesRequestSerializer.ApprovedModel, "local-binding-not-echoed-by-provider", false);

    private const string SyntheticResponse = """
        {
          "id":"resp_synthetic", "object":"response", "model":"gpt-6-luna", "status":"completed",
          "error":null, "incomplete_details":null,
          "output":[{"id":"msg_synthetic","type":"message","role":"assistant","status":"completed",
            "content":[{"type":"output_text","text":"Synthetic answer தமிழ் සිංහල","annotations":[]}]}],
          "usage":{"input_tokens":1000,"output_tokens":100,"total_tokens":1100,
            "input_tokens_details":{"cached_tokens":30,"cache_write_tokens":20,
              "cache_write_tokens_details":{"ttl_30m_tokens":15,"ttl_12h_tokens":5}},
            "output_tokens_details":{"reasoning_tokens":20}}
        }
        """;
    private static JsonObject Root() => JsonNode.Parse(SyntheticResponse)!.AsObject();
    private static OpenAiParsedResponsesResponse Parse(JsonNode root) =>
        OpenAiResponsesResponseParser.Parse(root.ToJsonString(), OpenAiResponsesRequestSerializer.ApprovedModel);

    [Theory]
    [InlineData("English question")]
    [InlineData("வணக்கம் தமிழ்")]
    [InlineData("Vanakkam konjam explain pannunga")]
    [InlineData("ආයුබෝවන් සිංහල")]
    [InlineData("  Mixed தமிழ் සිංහල 👩🏽‍💻 e\u0301  ")]
    [InlineData("Quotes \" slash \\ newline\n tab\t null\0")]
    public void RequestSerialization_PreservesRolesOrderContentAndClosedControls(string current)
    {
        var request = Request(current);
        var prepared = Prepare(request); // No token measurement or admitted operation is supplied.
        using var json = JsonDocument.Parse(prepared.CopyUtf8Payload());
        var root = json.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        var input = root.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, input.Select(item => item.GetProperty("role").GetString()));
        Assert.Equal(request.SystemInstructions, input[0].GetProperty("content").GetString());
        Assert.Equal(request.Messages.Select(message => message.Text), input.Skip(1).Select(item => item.GetProperty("content").GetString()));
        Assert.Single(input, item => item.GetProperty("content").GetString() == current);
        Assert.Equal(AiPrototypeLimits.MaxOutputTokens, root.GetProperty("max_output_tokens").GetInt32());
        foreach (var field in new[] { "store", "stream", "background" }) Assert.False(root.GetProperty(field).GetBoolean());
        Assert.Empty(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("none", root.GetProperty("tool_choice").GetString());
        Assert.Equal("disabled", root.GetProperty("truncation").GetString());
        Assert.Equal("text", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        foreach (var omitted in new[] { "conversation", "previous_response_id", "metadata", "user", "reasoning",
            "service_tier", "prompt_cache_key", "prompt_cache_retention", "prompt_cache_options" })
            Assert.False(root.TryGetProperty(omitted, out _));
    }

    [Fact]
    public void PreparedPayload_IsDeterministicDefensivelyCopiedAndHasDistinctByteDigest()
    {
        var request = Request();
        var first = Prepare(request);
        var bytes = first.CopyUtf8Payload();
        Assert.Equal(bytes, Prepare(request).CopyUtf8Payload());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), first.PayloadSha256);
        Assert.NotEqual(request.Fingerprint, first.PayloadSha256);
        Assert.Equal(request.Fingerprint, first.RequestFingerprint);
        bytes[0] ^= 1;
        Assert.False(first.MatchesPayload(bytes));
        Assert.True(first.MatchesPayload(first.CopyUtf8Payload()));
        Assert.Equal(Prepare(request).CopyUtf8Payload(), first.CopyUtf8Payload());
    }

    [Fact]
    public void Preparation_ReusesScopedPromptButNeverSerializesInternalIdentifiers()
    {
        var now = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        var actor = new AiAuthorizedActor(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        var operation = Guid.NewGuid();
        var conversation = AiConversationEntity.CreateForFirstUserMessage(actor, operation, now);
        var user = AiMessageEntity.CreateUserMessage(conversation, actor, operation, "Hello", now);
        var prompt = AiPromptFactory.Create(actor, conversation, new[] { user }, user.Id, now, 1, _ => 100);
        var request = AiTokenCountRequest.FromPrompt(OpenAiResponsesRequestSerializer.ApprovedModel,
            OpenAiResponsesRequestSerializer.SerializationRevision, prompt);
        var prepared = Prepare(request);
        Assert.Equal(prompt.Fingerprint, prepared.LogicalPromptFingerprint);
        var wire = Encoding.UTF8.GetString(prepared.CopyUtf8Payload());
        foreach (var internalValue in new[] { actor.UserId.ToString(), actor.ModuleProfileId.ToString(), conversation.Id.ToString(),
            operation.ToString(), user.Id.ToString(), prompt.Fingerprint, request.Fingerprint })
            Assert.DoesNotContain(internalValue, wire);
    }

    [Fact]
    public void OriginalMessageCollectionMutation_CannotChangePreparedBytes()
    {
        var history = Request().Messages.Take(2).ToList();
        var request = new AiTokenCountRequest(OpenAiResponsesRequestSerializer.ApprovedModel,
            OpenAiResponsesRequestSerializer.SerializationRevision, "System", history, "Current");
        var prepared = Prepare(request);
        var bytes = prepared.CopyUtf8Payload();
        history[0] = history[0] with { Text = "Mutation" };
        history.Clear();
        Assert.Equal(bytes, prepared.CopyUtf8Payload());
        Assert.Equal(bytes, Prepare(request).CopyUtf8Payload());
    }

    [Theory]
    [InlineData("intended")]
    [InlineData("candidate")]
    [InlineData("model")]
    [InlineData("framing")]
    [InlineData("role")]
    [InlineData("output-zero")]
    [InlineData("output-over")]
    [InlineData("store")]
    [InlineData("tools")]
    [InlineData("hosted")]
    [InlineData("redispatch")]
    [InlineData("modality")]
    [InlineData("settings")]
    [InlineData("binding")]
    [InlineData("unicode")]
    public void InvalidPreparation_IsRejectedWithoutContentInExceptions(string defect)
    {
        var original = Request();
        var request = defect is "model" or "framing" or "unicode"
            ? new AiTokenCountRequest(defect == "model" ? "unapproved-private-model" : original.Model,
                defect == "framing" ? "unreviewed-framing" : original.FramingRevision,
                original.SystemInstructions, original.Messages.Take(2).ToArray(), defect == "unicode" ? "\ud800" : original.Messages[^1].Text)
            : original;
        AiProviderExecutionSettings? settings = defect switch
        {
            "store" => Settings() with { Store = true },
            "tools" => Settings() with { Capabilities = AiProviderCapabilities.ExternalTools },
            "hosted" => Settings() with { UseProviderConversation = true },
            "redispatch" => Settings() with { AutomaticRedispatch = true },
            "modality" => Settings() with { OutputKind = AiTextOutputKind.Audio },
            "settings" => null,
            _ => Settings()
        };
        var messages = request.Messages.ToArray();
        if (defect == "role") messages[0] = messages[0] with { Role = (AiStoredMessageRole)99 };
        var candidate = new AiProviderRequestCandidate(request.Model, request.FramingRevision, request.SystemInstructions,
            messages, defect == "output-zero" ? 0 : defect == "output-over" ? AiPrototypeLimits.MaxOutputTokens + 1 : request.MaxBillableOutputTokens,
            defect == "binding" ? "wrong" : request.Fingerprint, settings);
        var error = Assert.Throws<InvalidOperationException>(() => OpenAiResponsesRequestSerializer.Prepare(
            defect == "intended" ? null : request, defect == "candidate" ? null : candidate));
        Assert.DoesNotContain(original.Messages[^1].Text, error.Message);
        Assert.DoesNotContain("unapproved-private-model", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void CompletedResponse_ExtractsObservationsWithoutAuthenticatingOrSettlingThem()
    {
        var parsed = Parse(Root());
        Assert.Equal(OpenAiResponseParseOutcome.Parsed, parsed.Outcome);
        Assert.Equal("resp_synthetic", parsed.ResponseId);
        Assert.Equal("gpt-6-luna", parsed.Model);
        Assert.Equal(AiTextCompletionState.Completed, parsed.Completion);
        Assert.Equal("Synthetic answer தமிழ் සිංහල", parsed.AssistantText);
        Assert.Equal(OpenAiUsageObservationState.Observed, parsed.UsageState);
        Assert.Equal(1000, parsed.Usage!.InputTokens);
        Assert.Equal(100, parsed.Usage.OutputTokens); // Already includes 20 reasoning tokens.
        Assert.Equal(1100, parsed.Usage.TotalTokens);
        Assert.Equal(20, parsed.Usage.ReasoningTokens);
        Assert.Equal(80, parsed.Usage.VisibleOutputTokens);
        Assert.Equal(30, parsed.Usage.CachedInputTokens);
        Assert.Equal(20, parsed.Usage.CacheWriteTokens);
        Assert.Equal(15, parsed.Usage.CacheWrite30MinuteTokens);
        Assert.Equal(5, parsed.Usage.CacheWrite12HourTokens);
        Assert.False(parsed.Usage.ExceedsPrototypeLimits);
        Assert.Equal(AiTextValidationOutcome.Invalid, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
        Assert.Null(parsed.ValidateUnverifiedText(LocalExpectation()).Text);
    }

    [Fact]
    public void ReasoningBeforeMultipleMessages_IsIgnoredAndTextOrderIsPreserved()
    {
        var root = Root();
        var output = root["output"]!.AsArray();
        output.Insert(0, JsonNode.Parse("""
            {"id":"rs_synthetic","type":"reasoning","summary":[{"type":"summary_text","text":"private reasoning summary"}],
             "content":[{"type":"reasoning_text","text":"private reasoning text"}],"encrypted_content":"opaque-private-data"}
            """));
        output.Add(JsonNode.Parse("""
            {"id":"msg_second","type":"message","role":"assistant","status":"completed",
             "content":[{"type":"output_text","text":" Second"},{"type":"output_text","text":" third"}]}
            """));
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.Parsed, parsed.Outcome);
        Assert.Equal("Synthetic answer தமிழ் සිංහල Second third", parsed.AssistantText);
        Assert.DoesNotContain("reasoning", parsed.AssistantText!);
        Assert.DoesNotContain("opaque-private", parsed.AssistantText!);
    }

    [Fact]
    public void Refusal_IsNotNormalAssistantCompletion()
    {
        var root = Root();
        root["output"]![0]!["content"] = JsonNode.Parse("""[{"type":"refusal","refusal":"Synthetic refusal"}]""");
        var parsed = Parse(root);
        Assert.Equal(AiTextCompletionState.Refused, parsed.Completion);
        Assert.Null(parsed.AssistantText);
        Assert.Equal("Synthetic refusal", parsed.Refusal);
        Assert.Equal(AiTextValidationOutcome.Refused, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Theory]
    [InlineData("max_output_tokens", OpenAiIncompleteReason.MaxOutputTokens)]
    [InlineData("content_filter", OpenAiIncompleteReason.ContentFilter)]
    [InlineData("max_messages", OpenAiIncompleteReason.MaxMessages)]
    [InlineData("steered", OpenAiIncompleteReason.Steered)]
    [InlineData("future_reason", OpenAiIncompleteReason.Unknown)]
    public void IncompleteResponses_WithholdPartialText(string reason, OpenAiIncompleteReason expected)
    {
        var root = Root();
        root["status"] = "incomplete";
        root["output"]![0]!["status"] = "incomplete";
        root["incomplete_details"] = new JsonObject { ["reason"] = reason };
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.Parsed, parsed.Outcome);
        Assert.Equal(AiTextCompletionState.Incomplete, parsed.Completion);
        Assert.Equal(expected, parsed.IncompleteReason);
        Assert.Null(parsed.AssistantText);
        Assert.NotNull(parsed.Usage);
        Assert.Equal(AiTextValidationOutcome.Incomplete, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Theory]
    [InlineData("failed", AiTextCompletionState.Failed, AiTextValidationOutcome.Failed)]
    [InlineData("cancelled", AiTextCompletionState.Cancelled, AiTextValidationOutcome.Cancelled)]
    [InlineData("in_progress", AiTextCompletionState.Uncertain, AiTextValidationOutcome.Uncertain)]
    [InlineData("queued", AiTextCompletionState.Uncertain, AiTextValidationOutcome.Uncertain)]
    [InlineData("future_status", AiTextCompletionState.Uncertain, AiTextValidationOutcome.Uncertain)]
    public void NonSuccessStatus_NeverDeliversText(string status, AiTextCompletionState completion, AiTextValidationOutcome validation)
    {
        var root = Root();
        root["status"] = status;
        root["output"] = new JsonArray();
        if (status == "failed") root["error"] = new JsonObject { ["code"] = "server_error", ["message"] = "private provider failure" };
        var parsed = Parse(root);
        Assert.Equal(completion, parsed.Completion);
        Assert.Null(parsed.AssistantText);
        Assert.Equal(validation, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
        if (status == "failed")
        {
            Assert.Equal(OpenAiResponseErrorClass.ProviderError, parsed.ErrorClass);
            Assert.Equal("server_error", parsed.ErrorCode);
        }
    }

    [Theory]
    [InlineData("function_call")]
    [InlineData("web_search_call")]
    [InlineData("file_search_call")]
    [InlineData("image_generation_call")]
    [InlineData("computer_call")]
    [InlineData("unknown_item")]
    public void UnsupportedOutputItems_AreRejectedWhileUsageIsRetained(string type)
    {
        var root = Root();
        root["output"]!.AsArray().Insert(0, new JsonObject { ["id"] = "unsupported", ["type"] = type });
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.UnsupportedOutput, parsed.Outcome);
        Assert.Null(parsed.AssistantText);
        Assert.Equal(100, parsed.Usage!.OutputTokens);
        Assert.Equal(AiTextValidationOutcome.Invalid, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Theory]
    [InlineData("output_audio")]
    [InlineData("output_image")]
    [InlineData("input_file")]
    [InlineData("unknown_content")]
    public void UnsupportedContentModalities_AreRejected(string type)
    {
        var root = Root();
        root["output"]![0]!["content"]![0]!["type"] = type;
        Assert.Equal(OpenAiResponseParseOutcome.UnsupportedOutput, Parse(root).Outcome);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("reasoning-only")]
    [InlineData("blank")]
    public void CompletedResponse_RequiresAssistantText(string defect)
    {
        var root = Root();
        if (defect == "empty") root["output"] = new JsonArray();
        if (defect == "reasoning-only") root["output"] = JsonNode.Parse("""[{"type":"reasoning","id":"rs_only","summary":[]}]""");
        if (defect == "blank") root["output"]![0]!["content"]![0]!["text"] = "  ";
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, Parse(root).Outcome);
    }

    [Theory]
    [InlineData("other-model")]
    [InlineData("gpt-6-luna-unapproved-snapshot")]
    public void ReturnedModel_RequiresExactBindingWithoutAliasAssumptions(string model)
    {
        var root = Root();
        root["model"] = model;
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.ModelMismatch, parsed.Outcome);
        Assert.Null(parsed.AssistantText);
        Assert.NotNull(parsed.Usage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingUsage_IsNotVerifiedZero(bool remove)
    {
        var root = Root();
        if (remove) root.Remove("usage"); else root["usage"] = null;
        var parsed = Parse(root);
        Assert.Equal(OpenAiUsageObservationState.Missing, parsed.UsageState);
        Assert.Null(parsed.Usage);
        Assert.Equal(AiTextValidationOutcome.Invalid, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Theory]
    [InlineData("input-negative")]
    [InlineData("output-negative")]
    [InlineData("total-negative")]
    [InlineData("fraction")]
    [InlineData("string")]
    [InlineData("overflow")]
    [InlineData("sum-overflow")]
    [InlineData("total")]
    [InlineData("cached")]
    [InlineData("writes")]
    [InlineData("overlap")]
    [InlineData("reasoning")]
    [InlineData("ttl")]
    [InlineData("ttl-missing")]
    [InlineData("details-type")]
    [InlineData("missing-output")]
    public void InvalidUsage_IsExplicitlyRejected(string defect)
    {
        var root = Root();
        var usage = root["usage"]!.AsObject();
        switch (defect)
        {
            case "input-negative": usage["input_tokens"] = -1; break;
            case "output-negative": usage["output_tokens"] = -1; break;
            case "total-negative": usage["total_tokens"] = -1; break;
            case "fraction": usage["input_tokens"] = 1000.5; break;
            case "string": usage["input_tokens"] = "1000"; break;
            case "overflow": usage["input_tokens"] = JsonNode.Parse("9223372036854775808"); break;
            case "sum-overflow": usage["input_tokens"] = long.MaxValue; break;
            case "total": usage["total_tokens"] = 1101; break;
            case "cached": usage["input_tokens_details"]!["cached_tokens"] = 1001; break;
            case "writes": usage["input_tokens_details"]!["cache_write_tokens"] = 1001; break;
            case "overlap": usage["input_tokens_details"]!["cached_tokens"] = 990; break;
            case "reasoning": usage["output_tokens_details"]!["reasoning_tokens"] = 101; break;
            case "ttl": usage["input_tokens_details"]!["cache_write_tokens_details"]!["ttl_12h_tokens"] = 10; break;
            case "ttl-missing": usage["input_tokens_details"]!["cache_write_tokens_details"]!.AsObject().Remove("ttl_12h_tokens"); break;
            case "details-type": usage["input_tokens_details"] = true; break;
            case "missing-output": usage.Remove("output_tokens"); break;
        }
        var parsed = Parse(root);
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, parsed.Outcome);
        Assert.Equal(OpenAiUsageObservationState.Invalid, parsed.UsageState);
        Assert.Null(parsed.Usage);
        Assert.Null(parsed.AssistantText);
    }

    [Fact]
    public void UnreportedUsageDetails_RemainUnknownRatherThanZero()
    {
        var root = Root();
        root["usage"]!.AsObject().Remove("input_tokens_details");
        root["usage"]!.AsObject().Remove("output_tokens_details");
        var parsed = Parse(root);
        Assert.Equal(OpenAiUsageObservationState.Observed, parsed.UsageState);
        Assert.Null(parsed.Usage!.CachedInputTokens);
        Assert.Null(parsed.Usage.CacheWriteTokens);
        Assert.Null(parsed.Usage.ReasoningTokens);
        Assert.Null(parsed.Usage.VisibleOutputTokens);
    }

    [Theory]
    [InlineData(3000, 100, 20)]
    [InlineData(1000, 600, 100)]
    public void AboveLimitUsage_IsPreservedWithoutClamping(int input, int output, int reasoning)
    {
        var root = Root();
        root["usage"]!["input_tokens"] = input;
        root["usage"]!["output_tokens"] = output;
        root["usage"]!["total_tokens"] = input + output;
        root["usage"]!["output_tokens_details"]!["reasoning_tokens"] = reasoning;
        var parsed = Parse(root);
        Assert.Equal(input, parsed.Usage!.InputTokens);
        Assert.Equal(output, parsed.Usage.OutputTokens);
        Assert.True(parsed.Usage.ExceedsPrototypeLimits);
        Assert.Equal(output - reasoning, parsed.Usage.VisibleOutputTokens);
        Assert.Equal(AiTextValidationOutcome.Invalid, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"private invalid root\"")]
    [InlineData("{\"object\":\"response\"}")]
    public void MalformedJson_NeverThrowsRawBodyErrors(string? json)
    {
        var parsed = OpenAiResponsesResponseParser.Parse(json, OpenAiResponsesRequestSerializer.ApprovedModel);
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, parsed.Outcome);
        Assert.Null(parsed.AssistantText);
        Assert.DoesNotContain("private invalid root", parsed.ToString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("output")]
    [InlineData("content")]
    [InlineData("role")]
    [InlineData("error")]
    [InlineData("message-status")]
    [InlineData("duplicate-item-id")]
    public void WrongTypesAndContradictoryItems_AreRejected(string defect)
    {
        var root = Root();
        if (defect == "status") root["status"] = 1;
        if (defect == "output") root["output"] = false;
        if (defect == "content") root["output"]![0]!["content"] = "not an array";
        if (defect == "role") root["output"]![0]!["role"] = "user";
        if (defect == "error") root["error"] = "not an object";
        if (defect == "message-status") root["output"]![0]!["status"] = "in_progress";
        if (defect == "duplicate-item-id") root["output"]!.AsArray().Add(root["output"]![0]!.DeepClone());
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, Parse(root).Outcome);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("escaped-model")]
    [InlineData("usage")]
    [InlineData("role")]
    public void DuplicateSecurityFields_AreRejectedEvenWithEscapedNames(string location)
    {
        var json = Root().ToJsonString();
        json = location switch
        {
            "model" => json.Replace("\"model\":", "\"model\":\"other\",\"model\":"),
            "escaped-model" => json.Replace("\"model\":", "\"\\u006dodel\":\"other\",\"model\":"),
            "usage" => json.Replace("\"input_tokens\":", "\"input_tokens\":1,\"input_tokens\":"),
            _ => json.Replace("\"role\":", "\"role\":\"user\",\"role\":")
        };
        Assert.Equal(OpenAiResponseParseOutcome.Malformed,
            OpenAiResponsesResponseParser.Parse(json, OpenAiResponsesRequestSerializer.ApprovedModel).Outcome);
    }

    [Fact]
    public void ProposedJsonSafetyBounds_RejectOversizeAndExcessDepth()
    {
        var large = Root();
        large["extra"] = new string('x', OpenAiResponsesResponseParser.ProposedMaxJsonBytes);
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, Parse(large).Outcome);
        var deep = Root();
        deep["extra"] = JsonNode.Parse(new string('[', OpenAiResponsesResponseParser.ProposedMaxDepth + 1) + "0" +
            new string(']', OpenAiResponsesResponseParser.ProposedMaxDepth + 1));
        Assert.Equal(OpenAiResponseParseOutcome.Malformed, Parse(deep).Outcome);
    }

    [Fact]
    public void DiagnosticsAndUnverifiedAcceptance_DoNotExposeOrAuthorizeObservedPrivateData()
    {
        var root = Root();
        root["output"]![0]!["content"]![0]!["text"] = "private-marker-answer";
        var parsed = Parse(root);
        foreach (var value in new object[] { parsed, parsed.Usage!, Prepare(Request("private-marker-question")) })
        {
            Assert.DoesNotContain("private-marker", value.ToString());
            Assert.DoesNotContain("resp_synthetic", value.ToString());
        }
        var usage = parsed.Usage;
        Assert.Equal(AiTextValidationOutcome.Invalid, parsed.ValidateUnverifiedText(LocalExpectation()).Outcome);
        Assert.Same(usage, parsed.Usage);
        Assert.Equal(AiTextValidationOutcome.Invalid,
            parsed.ValidateUnverifiedText(LocalExpectation() with { IsMock = true }).Outcome);
        // No transport, execution, ledger, consent or persistence dependency exists in these codecs.
    }
}
