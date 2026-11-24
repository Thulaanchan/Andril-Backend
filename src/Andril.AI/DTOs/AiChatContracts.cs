using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Andril.AI.Policies;

namespace Andril.AI.DTOs;

// Proposed public HTTP contracts requiring M1/client/M2 policy review. Not registered routes.
// Camel-case JSON follows the existing ASP.NET Core web defaults. Opaque IDs are references,
// never credentials. Account/profile/attempt/pricing/authorization evidence is deliberately absent.
// DTO validation does not authorize processing, mutation, dispatch or delivery.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiChatSubmissionRequest(Guid OperationId, string Text) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OperationId == Guid.Empty) yield return new("An operation reference is required.", [nameof(OperationId)]);
        if (!AiPublicContractValidation.ValidInput(Text))
            yield return new("AI input must contain between 1 and 500 Unicode text elements.", [nameof(Text)]);
    }
    public override string ToString() => "AI submission [private input and reference omitted]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiConversationRequest(Guid ConversationId) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        AiPublicContractValidation.Required(ConversationId, nameof(ConversationId));
    public override string ToString() => "AI conversation reference [identifier omitted]";
}

/// <summary>Same logical operation ID supplies idempotency; changed text/scope must conflict
/// under the future transaction. No second independently scoped idempotency key is invented.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiOperationRequest(Guid OperationId) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        AiPublicContractValidation.Required(OperationId, nameof(OperationId));
    public override string ToString() => "AI operation reference [identifier omitted]";
}

// Cancellation and manual retry remain unavailable. An explicit user request would reference
// the operation, not a client-issued attempt or financial disposition. No success DTO is defined.

public sealed record AiConversationSummary(Guid ConversationId, DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSavedUserMessageAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => "AI conversation summary [private metadata omitted]";
}

public sealed record AiHistoryMessage(Guid MessageId, string Role, string Text, DateTimeOffset SavedAtUtc)
{
    public override string ToString() => "AI history message [private content and identifier omitted]";
}

/// <summary>Proposed stable status strings are mapped explicitly at the API boundary.
/// No reply text, attempts, internal revision, quota ledger or provider observations are exposed.</summary>
public sealed record AiPublicOperationStatus(Guid OperationId, string Status)
{
    public override string ToString() => "AI operation status [private reference omitted]";
}

/// <summary>Envelope defensively copies the selected authorized page; no implicit full history.</summary>
public sealed class AiHistoryPage<T>
{
    public IReadOnlyList<T> Items { get; }
    public AiHistoryPage(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.Take(AiConversationReadPolicy.MaxReadLimit + 1).ToArray();
        if (copy.Length > AiConversationReadPolicy.MaxReadLimit || copy.Any(item => item is null))
            throw new ArgumentException("Invalid AI history page.");
        Items = Array.AsReadOnly(copy);
    }
    public override string ToString() => "AI history page [private items omitted]";
}

// Privacy notice, acknowledgement, processing-permission and contextual consent transport
// schemas remain blocked on applicable policy/interaction/revision authority. Internal evidence
// records MUST NOT be reused as HTTP bodies. No legal revision or completed-withdrawal semantics
// are invented here. General account privacy acknowledgement cannot establish AI permission.

/// <summary>Minimal explicit report intent, not a validated flag or admission/processing grant.
/// Routing remains blocked on M6's purpose-specific authorization and durable receipt contract.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiSafetyReportRequest(Guid RequestId, Guid MessageId) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        AiPublicContractValidation.Required(RequestId, nameof(RequestId))
            .Concat(AiPublicContractValidation.Required(MessageId, nameof(MessageId)));
    public override string ToString() => "AI report intent [private references omitted]";
}

/// <summary>Proposed stable codes for ProblemDetails extensions; not a separate error envelope.
/// No outcome is inferred by parsing exception messages or raw provider/database responses.</summary>
public static class AiApiErrorCodes
{
    public const string InvalidRequest = "ai_invalid_request";
    public const string AuthenticationRequired = "ai_authentication_required";
    public const string AccessUnavailable = "ai_access_unavailable";
    public const string IntegrationUnavailable = "ai_integration_unavailable";
    public const string Cancelled = "ai_request_cancelled";
    public const string UnexpectedFailure = "ai_unexpected_failure";
}

internal static class AiPublicContractValidation
{
    internal static IEnumerable<ValidationResult> Required(Guid id, string member) => id == Guid.Empty
        ? [new ValidationResult("A nonempty reference is required.", [member])] : [];

    internal static bool ValidInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            new StringInfo(text).LengthInTextElements > AiPrototypeLimits.MaxUserTextElements) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }
}
