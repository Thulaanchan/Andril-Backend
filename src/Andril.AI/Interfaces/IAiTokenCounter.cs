using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.Policies;

namespace Andril.AI.Interfaces;

/// <summary>
/// Local, read-only full-request measurement before admission; no operation or attempt required.
/// Implementations must use a trusted server-selected model and pinned framing/tokenizer
/// configuration, measure the supplied exact payload and system/history/current input including
/// all model-visible overhead, and honor
/// cancellation. Never dispatch generation, send content to a counting service, persist prompts,
/// load cross-conversation context, log content, or treat measurement as authorization.
/// Unknown models/configurations and unavailable/unreliable counts return explicit non-success.
/// No real tokenizer, provider support or conservative estimation strategy is supplied here.
/// </summary>
public interface IAiTokenCounter
{
    Task<AiTokenCountResult> CountAsync(AiTokenCountRequest request, AiPreparedProviderPayload payload,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral exact-byte binding. Configuration identifies all execution defaults
/// and controls, including settings absent from JSON. Metadata is not a credential or tokenizer proof.</summary>
public sealed record AiProviderPayloadBinding(string RequestFingerprint, string? LogicalPromptFingerprint,
    string PayloadSha256, string Model, string FramingRevision, string ExecutionConfiguration,
    int MaxBillableOutputTokens)
{
    public override string ToString() => "AI payload binding [private bindings omitted]";
}

/// <summary>Read-only preparation, independent of counting/admission. Copies bytes once and never
/// reserializes. A generic snapshot does not attest provider schema or correspondence to messages;
/// the trusted serializer must establish that correspondence. No transport or storage is performed.
/// UTF-8 length is NOT a token count. A future counter must measure all model-visible framing.</summary>
public sealed class AiPreparedProviderPayload
{
    private readonly byte[] _payload;
    public AiProviderPayloadBinding Binding { get; }

    public AiPreparedProviderPayload(AiTokenCountRequest request, ReadOnlySpan<byte> payload,
        string executionConfiguration)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (payload.IsEmpty || !ValidIdentifier(executionConfiguration))
            throw new ArgumentException("Invalid AI payload preparation.");
        _payload = payload.ToArray();
        Binding = new(request.Fingerprint, request.SourcePromptFingerprint,
            Convert.ToHexString(SHA256.HashData(_payload)), request.Model, request.FramingRevision,
            executionConfiguration, request.MaxBillableOutputTokens);
    }

    public byte[] CopyUtf8Payload() => _payload.ToArray();
    public bool MatchesPayload(ReadOnlySpan<byte> payload) => payload.SequenceEqual(_payload);
    public bool MatchesRequest(AiTokenCountRequest request) =>
        Binding.RequestFingerprint == request.Fingerprint &&
        Binding.LogicalPromptFingerprint == request.SourcePromptFingerprint && Binding.Model == request.Model &&
        Binding.FramingRevision == request.FramingRevision &&
        Binding.MaxBillableOutputTokens == request.MaxBillableOutputTokens;

    internal static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() && !value.Any(char.IsControl);
    internal static bool ValidDigest(string? digest) => digest is { Length: 64 } &&
        digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public override string ToString() => "Prepared AI payload [private content and bindings omitted]";
}

/// <summary>
/// Immutable text-only request snapshot, not a serialized provider request or authorization proof.
/// The caller supplies freshly authorized recent same-conversation history EXCLUDING the current
/// input; construction appends the current user message once. Equal historical text is legitimate.
/// Character validation, history selection and scope authorization remain existing policy duties.
/// FramingRevision selects an independently trusted local configuration accounting for ALL framing,
/// settings and other billable input. Unsupported configurations must fail closed in the counter.
/// JSON below is only a binding format: its byte length is NEVER a provider token count.
/// Changing model/framing/payload requires a new snapshot and measurement, including after any
/// future adapter rendering change. Neither nonempty model names nor fingerprints prove trust.
/// </summary>
public sealed class AiTokenCountRequest
{
    public string Model { get; }
    public string FramingRevision { get; }
    public string SystemInstructions { get; }
    public IReadOnlyList<AiPromptMessage> Messages { get; }
    public int CurrentUserMessageIndex => Messages.Count - 1;
    public int MaxBillableOutputTokens => AiPrototypeLimits.MaxOutputTokens;
    public string? SourcePromptFingerprint { get; }
    public string Fingerprint { get; }

    public AiTokenCountRequest(string model, string framingRevision, string systemInstructions,
        IReadOnlyList<AiPromptMessage> history, string currentUserText)
        : this(model, framingRevision, systemInstructions, history, currentUserText, null) { }

    private AiTokenCountRequest(string model, string framingRevision, string systemInstructions,
        IReadOnlyList<AiPromptMessage> history, string currentUserText, string? sourcePromptFingerprint)
    {
        RequireIdentifier(model, nameof(model));
        RequireIdentifier(framingRevision, nameof(framingRevision));
        if (string.IsNullOrWhiteSpace(systemInstructions) || string.IsNullOrWhiteSpace(currentUserText))
            throw new ArgumentException("Nonempty AI text input is required.");
        ArgumentNullException.ThrowIfNull(history);
        var snapshot = history.ToArray();
        if (snapshot.Any(message => message is null ||
            !Enum.IsDefined(message.Role) || string.IsNullOrWhiteSpace(message.Text)))
            throw new ArgumentException("Invalid AI message history.", nameof(history));

        Model = model;
        FramingRevision = framingRevision;
        SystemInstructions = systemInstructions;
        SourcePromptFingerprint = sourcePromptFingerprint;
        Messages = Array.AsReadOnly(snapshot
            .Append(new AiPromptMessage(AiStoredMessageRole.User, currentUserText)).ToArray());
        var binding = JsonSerializer.Serialize(new
        {
            Version = "andril-token-request-v1", Model, FramingRevision, SourcePromptFingerprint,
            SystemInstructions, Messages, MaxBillableOutputTokens
        });
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
    }

    /// <summary>Reuses existing structured prompt and retains its scoped fingerprint. This
    /// convenience path does not require admission; the direct constructor also supports an
    /// unsaved current message. Neither path changes the existing prompt factory or its policies.</summary>
    public static AiTokenCountRequest FromPrompt(string model, string framingRevision, AiConstructedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Messages.Count == 0 || prompt.Messages[^1].Role != AiStoredMessageRole.User ||
            string.IsNullOrWhiteSpace(prompt.Fingerprint))
            throw new ArgumentException("Invalid AI structured prompt.", nameof(prompt));
        return new(model, framingRevision, prompt.SystemInstructions,
            prompt.Messages.Take(prompt.Messages.Count - 1).ToArray(), prompt.Messages[^1].Text,
            prompt.Fingerprint);
    }

    private static void RequireIdentifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("An AI counting configuration identifier is required.", parameter);
    }

    public override string ToString() => "AI token request [private content and binding omitted]";
}

public enum AiTokenCountState
{
    Unknown, Measured, Estimated, UnsupportedModel, UnsupportedConfiguration, Unavailable, Unreliable
}

/// <summary>Provenance identifiers refer to a trusted local counter/version/configuration.
/// Their presence is structural evidence only, not authentication of the counting algorithm.</summary>
public sealed record AiTokenCountProvenance(string CounterId, string CounterRevision, string FramingRevision)
{
    public override string ToString() => "AI token provenance [configuration omitted]";
}

/// <summary>Only Measured may carry existing AiTokenEvidence. Estimated means only an estimate
/// is available: it intentionally exposes no usable numeric evidence and cannot authorize paid
/// execution. A future approved conservative strategy needs a separately reviewed contract.
/// Evidence and provenance must come from a trusted counter, never client/provider claims.</summary>
public sealed record AiTokenCountResult(AiTokenCountState State, AiTokenEvidence? Evidence = null,
    AiTokenCountProvenance? Provenance = null, AiProviderPayloadBinding? PayloadBinding = null)
{
    public override string ToString() => "AI token result [measurement details omitted]";
}

public enum AiTokenCountValidationOutcome
{
    Invalid, ValidMeasurement, EstimateNotAccepted, UnsupportedModel, UnsupportedConfiguration,
    Unavailable, Unreliable
}

/// <summary>Deterministic contract checks, not proof of tokenizer accuracy or provider support.
/// Reuses AiBudgetPolicy for components, overflow-safe totals, full-request verification and
/// approved token ceilings. No character heuristic, estimation margin or new authorization.
/// Consumers must independently establish counter/model/configuration trust and retain this
/// exact immutable payload through transport. Missing payload binding never falls back to the
/// legacy logical fingerprint. ValidMeasurement grants no execution.</summary>
public static class AiTokenCountContract
{
    public static AiTokenCountValidationOutcome Validate(AiTokenCountRequest? request, AiTokenCountResult? result,
        AiPreparedProviderPayload? payload = null)
    {
        if (request is null || result is null || !Enum.IsDefined(result.State) || result.State == AiTokenCountState.Unknown)
            return AiTokenCountValidationOutcome.Invalid;
        var provenance = result.Provenance;
        if (provenance is not null && (!AiPreparedProviderPayload.ValidIdentifier(provenance.CounterId) ||
            !AiPreparedProviderPayload.ValidIdentifier(provenance.CounterRevision) || provenance.FramingRevision != request.FramingRevision))
            return AiTokenCountValidationOutcome.Invalid;
        if (result.State != AiTokenCountState.Measured)
        {
            // A failure or unapproved estimate cannot smuggle usable numeric evidence.
            if (result.Evidence is not null || result.PayloadBinding is not null) return AiTokenCountValidationOutcome.Invalid;
            return result.State switch
            {
                AiTokenCountState.Estimated => AiTokenCountValidationOutcome.EstimateNotAccepted,
                AiTokenCountState.UnsupportedModel => AiTokenCountValidationOutcome.UnsupportedModel,
                AiTokenCountState.UnsupportedConfiguration => AiTokenCountValidationOutcome.UnsupportedConfiguration,
                AiTokenCountState.Unavailable => AiTokenCountValidationOutcome.Unavailable,
                AiTokenCountState.Unreliable => AiTokenCountValidationOutcome.Unreliable,
                _ => AiTokenCountValidationOutcome.Invalid
            };
        }
        var evidence = result.Evidence;
        return payload is not null && payload.MatchesRequest(request) &&
            AiPreparedProviderPayload.ValidDigest(result.PayloadBinding?.PayloadSha256) &&
            result.PayloadBinding == payload.Binding &&
            provenance is not null && AiBudgetPolicy.ValidTokens(evidence, request.Model) &&
            evidence!.RequestFingerprint == request.Fingerprint &&
            evidence.MaxBillableOutputTokens == request.MaxBillableOutputTokens
            ? AiTokenCountValidationOutcome.ValidMeasurement : AiTokenCountValidationOutcome.Invalid;
    }

    /// <summary>Structural comparison against the independently retained expected payload binding.
    /// Does not authenticate the counter or authorize a reservation/dispatch.</summary>
    public static bool MatchesBinding(AiTokenCountResult? result, AiProviderPayloadBinding? expected) =>
        result is { State: AiTokenCountState.Measured, Provenance: { } provenance, Evidence: { } evidence } &&
        expected is not null && result.PayloadBinding == expected &&
        AiPreparedProviderPayload.ValidDigest(expected.PayloadSha256) &&
        AiPreparedProviderPayload.ValidDigest(expected.RequestFingerprint) &&
        AiPreparedProviderPayload.ValidIdentifier(expected.Model) &&
        AiPreparedProviderPayload.ValidIdentifier(expected.FramingRevision) &&
        AiPreparedProviderPayload.ValidIdentifier(expected.ExecutionConfiguration) &&
        AiPreparedProviderPayload.ValidIdentifier(provenance.CounterId) &&
        AiPreparedProviderPayload.ValidIdentifier(provenance.CounterRevision) &&
        provenance.FramingRevision == expected.FramingRevision &&
        AiBudgetPolicy.ValidTokens(evidence, expected.Model) &&
        evidence.RequestFingerprint == expected.RequestFingerprint &&
        evidence.MaxBillableOutputTokens == expected.MaxBillableOutputTokens;

    /// <summary>Fail-closed consumer for callers needing verified-shaped numeric evidence.
    /// Cancellation before/after the counter prevents returning success; no fallback or retry.
    /// The selected counter must still be independently trusted. No tokenizer is implemented.</summary>
    public static async Task<AiTokenCountResult> CountVerifiedAsync(IAiTokenCounter counter,
        AiTokenCountRequest request, AiPreparedProviderPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        if (!payload.MatchesRequest(request))
            throw new InvalidOperationException("AI payload binding is inconsistent.");
        var result = await counter.CountAsync(request, payload, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Validate(request, result, payload) != AiTokenCountValidationOutcome.ValidMeasurement)
            throw new InvalidOperationException("Reliable AI token measurement is unavailable.");
        return result; // Keep the payload binding attached; never return detached legacy evidence.
    }
}
