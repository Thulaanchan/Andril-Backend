using System.Buffers;
using System.Text;
using System.Text.Json;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Providers.OpenAi;

/// <summary>In-memory preparation, never dispatch permission. CopyUtf8Payload returns a copy;
/// callers must protect it as private content and never log/persist it. PayloadSha256 identifies
/// exact bytes and is deliberately distinct from the existing logical/token fingerprints.
/// MeasurementPayload carries these same bytes and bindings into the provider-neutral counting
/// contract. Future transport must use this retained snapshot, never reserialize after counting.</summary>
public sealed class OpenAiPreparedResponsesRequest
{
    public AiPreparedProviderPayload MeasurementPayload { get; }
    public string RequestFingerprint => MeasurementPayload.Binding.RequestFingerprint;
    public string? LogicalPromptFingerprint => MeasurementPayload.Binding.LogicalPromptFingerprint;
    public string PayloadSha256 => MeasurementPayload.Binding.PayloadSha256;
    public AiProviderExecutionSettings Settings { get; }
    public string SerializationRevision => OpenAiResponsesRequestSerializer.SerializationRevision;

    internal OpenAiPreparedResponsesRequest(byte[] payload, AiTokenCountRequest request,
        AiProviderExecutionSettings settings)
    {
        MeasurementPayload = new(request, payload, OpenAiResponsesRequestSerializer.ExecutionConfiguration);
        Settings = settings;
    }

    public byte[] CopyUtf8Payload() => MeasurementPayload.CopyUtf8Payload();
    public bool MatchesPayload(ReadOnlySpan<byte> bytes) => MeasurementPayload.MatchesPayload(bytes);
    public bool MatchesRequest(AiTokenCountRequest request, AiProviderRequestCandidate candidate) =>
        MeasurementPayload.MatchesRequest(request) && Settings == candidate.Settings &&
        MeasurementPayload.Binding.ExecutionConfiguration == OpenAiResponsesRequestSerializer.ExecutionConfiguration &&
        request.Model == OpenAiResponsesRequestSerializer.ApprovedModel && request.FramingRevision == SerializationRevision;
    public override string ToString() => "Prepared OpenAI request [private payload and bindings omitted]";
}

/// <summary>
/// Offline System.Text.Json codec for the approved gpt-6-luna text prototype.
/// API schema: https://developers.openai.com/api/reference/resources/responses/methods/create
/// EasyInputMessage permits string content for system/user/assistant; no multimodal objects,
/// IDs, previous_response_id, conversation, metadata or credentials are serialized.
/// Only this pinned framing revision is supported; account model access remains unverified.
/// Reasoning effort, service tier and cache overrides are intentionally omitted, not newly approved.
/// Their provider defaults and cost/token/privacy implications require review before real use.
/// store:false is a request control, not a guarantee of zero provider retention.
/// No measurement/admission/attempt is required here, avoiding serialization/counting circularity.
/// No HTTP, persistence, logs, quotas, billing, tokenizer or authorization is implemented.
/// </summary>
public static class OpenAiResponsesRequestSerializer
{
    public const string ApprovedModel = "gpt-6-luna";
    public const string SerializationRevision = "openai-responses-text-v1";
    // Identifies the exact closed settings rendered below, including omitted reasoning/tier/cache
    // options. These provider defaults remain unapproved for live use. Any option/default-policy
    // change requires a reviewed new configuration AND framing revision and fresh measurement.
    public const string ExecutionConfiguration = "openai-responses-closed-text-defaults-v1";

    public static OpenAiPreparedResponsesRequest Prepare(AiTokenCountRequest? intended,
        AiProviderRequestCandidate? candidate)
    {
        // Shared structure/settings checks are independent of measurement and execution eligibility.
        if (intended is null || candidate is null || intended.Model != ApprovedModel ||
            intended.FramingRevision != SerializationRevision ||
            AiProviderRequestPolicy.ValidatePreparation(intended, candidate) !=
                AiProviderRequestValidationOutcome.PreparedCandidate)
            throw Invalid();

        try
        {
            // Reject invalid UTF-16 rather than letting encoding replace private message characters.
            var strictUtf8 = new UTF8Encoding(false, true);
            strictUtf8.GetByteCount(intended.SystemInstructions);
            foreach (var message in intended.Messages) strictUtf8.GetByteCount(message.Text);

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("model", ApprovedModel);
                writer.WriteStartArray("input");
                WriteMessage(writer, "system", intended.SystemInstructions);
                foreach (var message in intended.Messages)
                    WriteMessage(writer, message.Role == AiStoredMessageRole.User ? "user" : "assistant", message.Text);
                writer.WriteEndArray();
                writer.WriteNumber("max_output_tokens", intended.MaxBillableOutputTokens);
                writer.WriteBoolean("store", false);
                writer.WriteBoolean("stream", false);
                writer.WriteBoolean("background", false);
                writer.WriteStartArray("tools");
                writer.WriteEndArray();
                writer.WriteString("tool_choice", "none");
                writer.WriteString("truncation", "disabled");
                writer.WriteStartObject("text");
                writer.WriteStartObject("format");
                writer.WriteString("type", "text");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.Flush();
            }
            return new(buffer.WrittenSpan.ToArray(), intended, candidate.Settings!);
        }
        catch (EncoderFallbackException) { throw Invalid(); }
        catch (ArgumentException) { throw Invalid(); }
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", text);
        writer.WriteEndObject();
    }

    private static InvalidOperationException Invalid() => new("OpenAI request preparation is invalid or unsupported.");
}
