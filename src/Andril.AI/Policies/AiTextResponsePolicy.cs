using Andril.AI.Interfaces;

namespace Andril.AI.Policies;

// Proposed provider-neutral metadata supplement: existing AiProviderResponse only has Text/IsMock.
// Trusted adapter must verify all provider/request/model/usage binding; records are not credentials.
public enum AiTextCompletionState { Unknown, Completed, Refused, Incomplete, Failed, Cancelled, Uncertain }
public enum AiTextOutputKind { Unknown, Text, ToolCall, Image, Audio, Video, Mixed }
public enum AiTextValidationOutcome { Invalid, ValidText, MockText, Refused, Incomplete, Failed, Cancelled, Uncertain }
public sealed record AiTextResponseExpectation(string Model, string RequestFingerprint, bool IsMock);
/// <summary>Billable output is visible output PLUS reasoning. Counts are authoritative actual
/// usage, not response character counts or requested ceilings. No cached discounts inferred.</summary>
public sealed record AiTextUsage(long InputTokens, long VisibleOutputTokens, long ReasoningTokens,
    long BillableOutputTokens, bool Verified);
public sealed record AiTextResponseInput(AiProviderResponse? Response, AiTextCompletionState Completion,
    AiTextOutputKind Kind, string Model, string RequestFingerprint, AiTextUsage? Usage)
{
    public override string ToString() => "AI text response input [private content omitted]";
}
public sealed record AiTextValidationResult(AiTextValidationOutcome Outcome, string? Text = null)
{
    public override string ToString() => $"AI text validation ({Outcome}) [private content omitted]";
}

/// <summary>Structural validation ONLY. Does not detect semantic harm, hallucination, prompt
/// leakage, diagnoses or manipulation. No execution, persistence, retry, delivery grant or refund.
/// Even rejected/refused/incomplete/over-limit output can incur legitimate provider costs.
/// M6 safety, fresh auth/privacy/revocation, operation/attempt checks, atomic late completion
/// and protected delivery remain mandatory. Mock success is distinct from paid text success.</summary>
public static class AiTextResponsePolicy
{
    public static AiTextValidationResult Validate(AiTextResponseInput? input, AiTextResponseExpectation? expected)
    {
        if (input is null || expected is null || input.Response is null ||
            string.IsNullOrWhiteSpace(expected.Model) || string.IsNullOrWhiteSpace(expected.RequestFingerprint) ||
            input.Model != expected.Model || input.RequestFingerprint != expected.RequestFingerprint ||
            input.Response.IsMock != expected.IsMock || !Enum.IsDefined(input.Completion) ||
            input.Completion == AiTextCompletionState.Unknown || input.Kind != AiTextOutputKind.Text)
            return new(AiTextValidationOutcome.Invalid);

        // Non-completed states never become deliverable assistant text; usage may be unavailable.
        // They are status results, not safety decisions or proof of zero financial liability.
        if (input.Completion != AiTextCompletionState.Completed)
            return new(input.Completion switch
            {
                AiTextCompletionState.Refused => AiTextValidationOutcome.Refused,
                AiTextCompletionState.Incomplete => AiTextValidationOutcome.Incomplete,
                AiTextCompletionState.Failed => AiTextValidationOutcome.Failed,
                AiTextCompletionState.Cancelled => AiTextValidationOutcome.Cancelled,
                AiTextCompletionState.Uncertain => AiTextValidationOutcome.Uncertain,
                _ => AiTextValidationOutcome.Invalid
            });
        if (string.IsNullOrWhiteSpace(input.Response.Text)) return new(AiTextValidationOutcome.Invalid);
        if (expected.IsMock)
            return input.Usage is null ? new(AiTextValidationOutcome.MockText, input.Response.Text) : new(AiTextValidationOutcome.Invalid);

        var usage = input.Usage;
        if (usage is null || !usage.Verified || usage.InputTokens <= 0 || usage.InputTokens > AiPrototypeLimits.MaxInputTokens ||
            usage.VisibleOutputTokens < 0 || usage.ReasoningTokens < 0 || usage.BillableOutputTokens <= 0 ||
            usage.BillableOutputTokens > AiPrototypeLimits.MaxOutputTokens ||
            usage.VisibleOutputTokens > usage.BillableOutputTokens ||
            usage.ReasoningTokens != usage.BillableOutputTokens - usage.VisibleOutputTokens)
            return new(AiTextValidationOutcome.Invalid);
        return new(AiTextValidationOutcome.ValidText, input.Response.Text);
    }
}
