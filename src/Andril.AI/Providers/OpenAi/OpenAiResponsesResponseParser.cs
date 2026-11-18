using System.Text;
using System.Text.Json;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Providers.OpenAi;

public enum OpenAiResponseParseOutcome { Malformed, Parsed, ModelMismatch, UnsupportedOutput, Unresolved }
public enum OpenAiUsageObservationState { Missing, Observed, Invalid }
public enum OpenAiIncompleteReason { None, MaxOutputTokens, ContentFilter, MaxMessages, Steered, Unknown }
public enum OpenAiResponseErrorClass { None, ProviderError }

/// <summary>Untrusted observations with syntactic consistency checks only, never a verified bill.
/// OutputTokens already includes ReasoningTokens; do not add them. Missing detail stays null.
/// Cache writes/TTL pricing cannot be represented by current AiBudgetPolicy pricing evidence:
/// financial integration must be reviewed before spending; no rate or discount is inferred here.</summary>
public sealed record OpenAiObservedUsage(long InputTokens, long OutputTokens, long TotalTokens,
    long? CachedInputTokens, long? ReasoningTokens, long? CacheWriteTokens,
    long? CacheWrite30MinuteTokens, long? CacheWrite12HourTokens)
{
    public long? VisibleOutputTokens => ReasoningTokens is { } reasoning ? OutputTokens - reasoning : null;
    public bool ExceedsPrototypeLimits => InputTokens > AiPrototypeLimits.MaxInputTokens ||
        OutputTokens > AiPrototypeLimits.MaxOutputTokens;
    public override string ToString() => "OpenAI usage observation [details omitted; not verified billing]";
}

/// <summary>Private parsed observations, not safe output, authentication or financial evidence.
/// Only completed non-refusal text is exposed as AssistantText; partial/reasoning text is withheld.
/// Do not log/serialize this object or its private fields into telemetry. Safe ToString only.</summary>
public sealed record OpenAiParsedResponsesResponse(OpenAiResponseParseOutcome Outcome,
    string? ResponseId = null, string? Model = null, AiTextCompletionState Completion = AiTextCompletionState.Unknown,
    string? AssistantText = null, string? Refusal = null, OpenAiIncompleteReason IncompleteReason = OpenAiIncompleteReason.None,
    OpenAiObservedUsage? Usage = null, OpenAiUsageObservationState UsageState = OpenAiUsageObservationState.Missing,
    OpenAiResponseErrorClass ErrorClass = OpenAiResponseErrorClass.None, string? ErrorCode = null)
{
    /// <summary>Reuse existing response policy WITHOUT inventing provider fingerprint echoes or
    /// authenticating parsed usage. The fingerprint comes solely from future trusted LOCAL context.
    /// Completed observations intentionally fail normal acceptance (Verified=false). Refusal,
    /// incomplete/failure/cancellation/uncertainty remain status results, never delivery permission.</summary>
    public AiTextValidationResult ValidateUnverifiedText(AiTextResponseExpectation localExpectation)
    {
        ArgumentNullException.ThrowIfNull(localExpectation);
        if (Outcome is not (OpenAiResponseParseOutcome.Parsed or OpenAiResponseParseOutcome.Unresolved) || localExpectation.IsMock)
            return new(AiTextValidationOutcome.Invalid);
        AiTextUsage? usage = Usage?.ReasoningTokens is { } reasoning
            ? new(Usage.InputTokens, Usage.OutputTokens - reasoning, reasoning, Usage.OutputTokens, false) : null;
        return AiTextResponsePolicy.Validate(new(new AiProviderResponse(AssistantText ?? "", false), Completion,
            AiTextOutputKind.Text, Model ?? "", localExpectation.RequestFingerprint, usage), localExpectation);
    }

    public override string ToString() => "Parsed OpenAI response [private content and identifiers omitted]";
}

/// <summary>
/// Offline strict text/reasoning subset of the Responses API schema, checked 18 November 2026:
/// https://developers.openai.com/api/reference/resources/responses/methods/create
/// https://developers.openai.com/api/docs/guides/prompt-caching
/// No aliases/snapshot equivalence is invented: expected and returned model must match exactly.
/// IDs, status and usage remain untrusted; no transport/attempt/auth/safety/consent/billing proof.
/// Unknown output types fail closed. Unknown root metadata is ignored, but duplicate property
/// names at ANY depth are rejected (including escaped equivalents). No raw-body error messages.
/// ProposedMaxJsonBytes and ProposedMaxDepth are proposed technical safety bounds, not product
/// limits. They require operational review before live integration. No logs or external effects.
/// </summary>
public static class OpenAiResponsesResponseParser
{
    public const int ProposedMaxJsonBytes = 256 * 1024;
    public const int ProposedMaxDepth = 32;

    public static OpenAiParsedResponsesResponse Parse(string? json, string? expectedModel)
    {
        if (string.IsNullOrWhiteSpace(json) || expectedModel != OpenAiResponsesRequestSerializer.ApprovedModel)
            return new(OpenAiResponseParseOutcome.Malformed);
        try
        {
            var encoding = new UTF8Encoding(false, true);
            if (encoding.GetByteCount(json) > ProposedMaxJsonBytes) return new(OpenAiResponseParseOutcome.Malformed);
            using var document = JsonDocument.Parse(encoding.GetBytes(json), new JsonDocumentOptions
            {
                MaxDepth = ProposedMaxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object);
            RejectDuplicates(root);
            Require(Text(root, "object") == "response");
            var id = Text(root, "id");
            var model = Text(root, "model");
            var status = Text(root, "status");
            var (usage, usageState) = ReadUsage(root);
            if (usageState == OpenAiUsageObservationState.Invalid)
                return new(OpenAiResponseParseOutcome.Malformed, id, model, UsageState: usageState);

            var errorClass = OpenAiResponseErrorClass.None;
            string? errorCode = null;
            if (Present(root, "error", out var error))
            {
                Require(error.ValueKind == JsonValueKind.Object);
                errorCode = Text(error, "code");
                _ = Text(error, "message"); // Discard provider error messages; never diagnostic text.
                errorClass = OpenAiResponseErrorClass.ProviderError;
            }
            Require(status != "completed" || errorClass == OpenAiResponseErrorClass.None);
            Require(status != "failed" || errorClass == OpenAiResponseErrorClass.ProviderError);

            var incomplete = OpenAiIncompleteReason.None;
            if (Present(root, "incomplete_details", out var details))
            {
                Require(details.ValueKind == JsonValueKind.Object && status == "incomplete");
                incomplete = OptionalText(details, "reason") switch
                {
                    "max_output_tokens" => OpenAiIncompleteReason.MaxOutputTokens,
                    "content_filter" => OpenAiIncompleteReason.ContentFilter,
                    "max_messages" => OpenAiIncompleteReason.MaxMessages,
                    "steered" => OpenAiIncompleteReason.Steered,
                    _ => OpenAiIncompleteReason.Unknown
                };
            }
            else if (status == "incomplete") incomplete = OpenAiIncompleteReason.Unknown;

            var completion = status switch
            {
                "completed" => AiTextCompletionState.Completed,
                "incomplete" => AiTextCompletionState.Incomplete,
                "failed" => AiTextCompletionState.Failed,
                "cancelled" => AiTextCompletionState.Cancelled,
                _ => AiTextCompletionState.Uncertain
            };
            Require(root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array);
            if (!ReadOutput(output, status == "completed", out var text, out var refusal))
                return new(OpenAiResponseParseOutcome.UnsupportedOutput, id, model, completion,
                    IncompleteReason: incomplete, Usage: usage, UsageState: usageState, ErrorClass: errorClass, ErrorCode: errorCode);
            if (model != expectedModel)
                return new(OpenAiResponseParseOutcome.ModelMismatch, id, model, completion,
                    Usage: usage, UsageState: usageState, ErrorClass: errorClass, ErrorCode: errorCode);
            if (completion == AiTextCompletionState.Completed && refusal is not null) completion = AiTextCompletionState.Refused;
            if (completion == AiTextCompletionState.Completed && string.IsNullOrWhiteSpace(text))
                return new(OpenAiResponseParseOutcome.Malformed, id, model, Usage: usage, UsageState: usageState);
            return new(completion == AiTextCompletionState.Uncertain ? OpenAiResponseParseOutcome.Unresolved : OpenAiResponseParseOutcome.Parsed,
                id, model, completion, completion == AiTextCompletionState.Completed ? text : null, refusal,
                incomplete, usage, usageState, errorClass, errorCode);
        }
        catch (JsonException) { return new(OpenAiResponseParseOutcome.Malformed); }
        catch (EncoderFallbackException) { return new(OpenAiResponseParseOutcome.Malformed); }
        catch (InvalidOperationException) { return new(OpenAiResponseParseOutcome.Malformed); }
        catch (FormatException) { return new(OpenAiResponseParseOutcome.Malformed); }
    }

    private static bool ReadOutput(JsonElement output, bool completed, out string text, out string? refusal)
    {
        var texts = new StringBuilder();
        var refusals = new StringBuilder();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        text = "";
        refusal = null;
        foreach (var item in output.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.Object);
            var type = Text(item, "type");
            if (type is not ("message" or "reasoning")) return false;
            Require(ids.Add(Text(item, "id")));
            if (type == "reasoning")
            {
                // Validate only supported reasoning metadata; never return its content/summary.
                Require(item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array);
                foreach (var part in summary.EnumerateArray())
                    Require(part.ValueKind == JsonValueKind.Object && Text(part, "type") == "summary_text" &&
                        part.TryGetProperty("text", out var summaryText) && summaryText.ValueKind == JsonValueKind.String);
                if (Present(item, "encrypted_content", out var encrypted)) Require(encrypted.ValueKind == JsonValueKind.String);
                if (Present(item, "content", out var content))
                {
                    Require(content.ValueKind == JsonValueKind.Array);
                    foreach (var part in content.EnumerateArray())
                        Require(part.ValueKind == JsonValueKind.Object && Text(part, "type") == "reasoning_text" &&
                            part.TryGetProperty("text", out var thought) && thought.ValueKind == JsonValueKind.String);
                }
                continue;
            }
            Require(Text(item, "role") == "assistant");
            var status = Text(item, "status");
            Require(status is "completed" or "incomplete" or "in_progress");
            Require(!completed || status == "completed");
            Require(item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array);
            foreach (var part in parts.EnumerateArray())
            {
                Require(part.ValueKind == JsonValueKind.Object);
                switch (Text(part, "type"))
                {
                    case "output_text":
                        Require(part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String);
                        texts.Append(value.GetString());
                        if (Present(part, "annotations", out var annotations)) Require(annotations.ValueKind == JsonValueKind.Array);
                        break;
                    case "refusal": refusals.Append(Text(part, "refusal")); break;
                    default: return false;
                }
            }
        }
        text = texts.ToString();
        refusal = refusals.Length == 0 ? null : refusals.ToString();
        return true;
    }

    private static (OpenAiObservedUsage?, OpenAiUsageObservationState) ReadUsage(JsonElement root)
    {
        if (!Present(root, "usage", out var usage)) return (null, OpenAiUsageObservationState.Missing);
        try
        {
            Require(usage.ValueKind == JsonValueKind.Object);
            var input = Number(usage, "input_tokens");
            var output = Number(usage, "output_tokens");
            var total = Number(usage, "total_tokens");
            Require(input <= long.MaxValue - output && input + output == total);
            long? cached = null, reasoning = null, writes = null, writes30 = null, writes12 = null;
            if (Present(usage, "input_tokens_details", out var inputDetails))
            {
                Require(inputDetails.ValueKind == JsonValueKind.Object);
                cached = OptionalNumber(inputDetails, "cached_tokens");
                writes = OptionalNumber(inputDetails, "cache_write_tokens");
                Require(cached is null || cached <= input);
                Require(writes is null || writes <= input);
                Require(cached is null || writes is null || cached <= input - writes);
                if (Present(inputDetails, "cache_write_tokens_details", out var writeDetails))
                {
                    Require(writeDetails.ValueKind == JsonValueKind.Object);
                    writes30 = Number(writeDetails, "ttl_30m_tokens");
                    writes12 = Number(writeDetails, "ttl_12h_tokens");
                    Require(writes is not null && writes30 <= long.MaxValue - writes12 && writes30 + writes12 == writes);
                }
            }
            if (Present(usage, "output_tokens_details", out var outputDetails))
            {
                Require(outputDetails.ValueKind == JsonValueKind.Object);
                reasoning = OptionalNumber(outputDetails, "reasoning_tokens");
                Require(reasoning is null || reasoning <= output);
            }
            return (new(input, output, total, cached, reasoning, writes, writes30, writes12), OpenAiUsageObservationState.Observed);
        }
        catch (FormatException) { return (null, OpenAiUsageObservationState.Invalid); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name));
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static bool Present(JsonElement value, string name, out JsonElement property) =>
        value.TryGetProperty(name, out property) && property.ValueKind != JsonValueKind.Null;
    private static string Text(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String);
        var result = property.GetString();
        Require(!string.IsNullOrWhiteSpace(result));
        return result!;
    }
    private static string? OptionalText(JsonElement value, string name) => Present(value, name, out _) ? Text(value, name) : null;
    private static long Number(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number);
        Require(property.TryGetInt64(out var result) && result >= 0);
        return result;
    }
    private static long? OptionalNumber(JsonElement value, string name) => Present(value, name, out _) ? Number(value, name) : null;
    private static void Require(bool condition)
    {
        if (!condition) throw new FormatException("Malformed OpenAI response.");
    }
}
