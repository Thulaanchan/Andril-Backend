using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Providers.OpenAi;

namespace Andril.AI.Policies;

[Flags]
public enum AiProviderCapabilities
{
    None = 0, WebSearch = 1, FileSearch = 2, ImageGeneration = 4,
    VoiceGeneration = 8, ExternalTools = 16, AutonomousAgents = 32
}

/// <summary>Explicit adapter requirements, NOT proof of actual provider privacy behavior.
/// Nullable switches require an explicit false; unknown/default configurations fail closed.
/// This closed configuration has no arbitrary provider option bag. Future model-visible settings
/// must be pinned to the measured FramingRevision and reviewed before extending this contract.</summary>
public sealed record AiProviderExecutionSettings(bool? Store, bool? UseProviderConversation,
    AiTextOutputKind OutputKind, AiProviderCapabilities Capabilities, bool? AutomaticRedispatch)
{
    public override string ToString() => "AI provider settings [configuration omitted]";
}

/// <summary>
/// Immutable candidate projection for comparison with the existing measured request. Copies the
/// collection without selecting context, normalizing text or appending a current message.
/// Malformed candidates can be represented but must be rejected by AiProviderRequestPolicy.
/// RequestFingerprint is the existing AiTokenCountRequest binding, NOT a hash of provider wire
/// bytes. SourcePromptFingerprint, when present, is the separate logical scoped-prompt binding.
/// No credentials, persisted prompt, execution claim or authorization is carried here.
/// </summary>
public sealed class AiProviderRequestCandidate
{
    public string? Model { get; }
    public string? FramingRevision { get; }
    public string? SystemInstructions { get; }
    public IReadOnlyList<AiPromptMessage>? Messages { get; }
    public int MaxBillableOutputTokens { get; }
    public string? RequestFingerprint { get; }
    public string? SourcePromptFingerprint { get; }
    public AiProviderExecutionSettings? Settings { get; }

    public AiProviderRequestCandidate(string? model, string? framingRevision, string? systemInstructions,
        IReadOnlyList<AiPromptMessage>? messages, int maxBillableOutputTokens,
        string? requestFingerprint, AiProviderExecutionSettings? settings, string? sourcePromptFingerprint = null)
    {
        Model = model;
        FramingRevision = framingRevision;
        SystemInstructions = systemInstructions;
        Messages = messages is null ? null : Array.AsReadOnly(messages.ToArray());
        MaxBillableOutputTokens = maxBillableOutputTokens;
        RequestFingerprint = requestFingerprint;
        SourcePromptFingerprint = sourcePromptFingerprint;
        Settings = settings;
    }

    public static AiProviderRequestCandidate FromMeasuredRequest(AiTokenCountRequest request,
        AiProviderExecutionSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.Model, request.FramingRevision, request.SystemInstructions, request.Messages,
            request.MaxBillableOutputTokens, request.Fingerprint, settings, request.SourcePromptFingerprint);
    }

    public override string ToString() => "AI provider candidate [private content and bindings omitted]";
}

public enum AiProviderRequestValidationOutcome
{
    InvalidRequest, ValidatedCandidate, RequestBindingMismatch, TokenEvidenceUnavailable,
    TokenLimitExceeded, UnsupportedExecutionSettings, PreparedCandidate
}

/// <summary>
/// Pure comparison and restricted-setting validation. The intended request must come from trusted
/// preparation with freshly authorized same-conversation context; matching content/fingerprints
/// or self-asserted measurement metadata do not authenticate that provenance or Social eligibility.
/// Reuses AiTokenCountContract (and therefore AiBudgetPolicy) for token validation. Does not assemble
/// context, count tokens, persist, log, reserve money/quota, call a provider or authorize dispatch.
///
/// Only text, explicit non-storing, no hosted conversation, no tools and no automatic redispatch
/// are supported. The existing exact-byte SHA-256 is carried by the immutable OpenAI preparation
/// and measurement binding; the logical fingerprint algorithm remains unchanged. Generic token
/// contracts have no OpenAI dependency. This policy deliberately accepts only the current concrete
/// serializer's sealed preparation, ensuring callers cannot substitute self-described JSON bytes.
/// It compares pinned framing/configuration but cannot prove provider framing accuracy, retention,
/// model availability or tokenizer accuracy. Live provider defaults still require separate review.
/// Any serialization/settings change requires appropriate new binding/measurement before execution.
/// ValidatedCandidate means structural eligibility for further checks only: fresh authorization,
/// privacy, operation/attempt, quota, budget, safety, consent and delivery gates remain mandatory.
/// </summary>
public static class AiProviderRequestPolicy
{
    public static AiProviderRequestValidationOutcome Validate(AiTokenCountRequest? intended,
        AiProviderRequestCandidate? candidate, AiTokenCountResult? measurement,
        OpenAiPreparedResponsesRequest? prepared = null)
    {
        var preparation = ValidatePreparation(intended, candidate);
        if (preparation != AiProviderRequestValidationOutcome.PreparedCandidate) return preparation;
        // A generic byte snapshot cannot certify OpenAI schema correspondence. Require the sealed
        // serializer-produced representation (internal construction), not caller-asserted hashes.
        if (prepared is null) return AiProviderRequestValidationOutcome.TokenEvidenceUnavailable;
        if (!prepared.MatchesRequest(intended!, candidate!))
            return AiProviderRequestValidationOutcome.RequestBindingMismatch;
        var outcome = AiTokenCountContract.Validate(intended, measurement, prepared.MeasurementPayload);
        if (outcome != AiTokenCountValidationOutcome.ValidMeasurement)
        {
            return measurement?.State == AiTokenCountState.Measured &&
                measurement.Evidence?.FullInputTokens > AiPrototypeLimits.MaxInputTokens
                ? AiProviderRequestValidationOutcome.TokenLimitExceeded
                : AiProviderRequestValidationOutcome.TokenEvidenceUnavailable;
        }
        return AiProviderRequestValidationOutcome.ValidatedCandidate;
    }

    /// <summary>Read-only premeasurement structural comparison, never ValidatedCandidate or dispatch
    /// permission. Shared by the serializer and final validation to avoid circular prerequisites.</summary>
    public static AiProviderRequestValidationOutcome ValidatePreparation(AiTokenCountRequest? intended,
        AiProviderRequestCandidate? candidate)
    {
        if (intended is null || candidate is null || string.IsNullOrWhiteSpace(candidate.Model) ||
            string.IsNullOrWhiteSpace(candidate.FramingRevision) || string.IsNullOrWhiteSpace(candidate.SystemInstructions) ||
            candidate.Messages is null || candidate.Messages.Count == 0 || candidate.MaxBillableOutputTokens <= 0)
            return AiProviderRequestValidationOutcome.InvalidRequest;

        var messages = candidate.Messages;
        if (messages.Any(message => message is null || !Enum.IsDefined(message.Role) ||
                string.IsNullOrWhiteSpace(message.Text)) || messages[^1].Role != AiStoredMessageRole.User)
            return AiProviderRequestValidationOutcome.InvalidRequest;
        // Character limits are distinct from token measurement; preserve the current text-element
        // convention, without rewriting existing context selection or trimming any accepted text.
        if (messages.Any(message => message.Role == AiStoredMessageRole.User &&
            new StringInfo(message.Text).LengthInTextElements > AiPrototypeLimits.MaxUserTextElements))
            return AiProviderRequestValidationOutcome.InvalidRequest;
        if (candidate.MaxBillableOutputTokens > AiPrototypeLimits.MaxOutputTokens)
            return AiProviderRequestValidationOutcome.TokenLimitExceeded;

        var settings = candidate.Settings;
        if (settings is null || settings.Store != false || settings.UseProviderConversation != false ||
            settings.OutputKind != AiTextOutputKind.Text || settings.Capabilities != AiProviderCapabilities.None ||
            settings.AutomaticRedispatch != false)
            return AiProviderRequestValidationOutcome.UnsupportedExecutionSettings;

        // Compare the actual structured contents as well as the claimed binding. Never rebuild
        // through a constructor that could append the already present current user message.
        if (candidate.Model != intended.Model || candidate.FramingRevision != intended.FramingRevision ||
            candidate.SystemInstructions != intended.SystemInstructions ||
            candidate.SourcePromptFingerprint != intended.SourcePromptFingerprint ||
            string.IsNullOrWhiteSpace(candidate.RequestFingerprint) || candidate.RequestFingerprint != intended.Fingerprint ||
            candidate.MaxBillableOutputTokens != intended.MaxBillableOutputTokens ||
            !messages.SequenceEqual(intended.Messages))
            return AiProviderRequestValidationOutcome.RequestBindingMismatch;

        return AiProviderRequestValidationOutcome.PreparedCandidate;
    }
}
