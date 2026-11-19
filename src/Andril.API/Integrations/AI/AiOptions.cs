using Andril.AI.Policies;
using Andril.AI.Providers.OpenAi;
using Microsoft.Extensions.Options;

namespace Andril.API.Integrations.AI;

/// <summary>Server-only configuration, not an execution capability or an HTTP DTO.
/// Disabled defaults require no invented timeout, account IDs, prototype dates or credentials.
/// A populated configuration still cannot supply missing authoritative integration contracts.
/// Never log/destructure/serialize options; private configuration remains functional data.</summary>
public sealed class AiOptions
{
    public bool LiveExecutionEnabled { get; init; }
    public string Model { get; init; } = OpenAiResponsesRequestSerializer.ApprovedModel;
    public string FramingRevision { get; init; } = OpenAiResponsesRequestSerializer.SerializationRevision;
    public string ExecutionConfiguration { get; init; } = OpenAiResponsesRequestSerializer.ExecutionConfiguration;
    public AiTextOutputKind OutputKind { get; init; } = AiTextOutputKind.Text;
    public int MaxInputTokens { get; init; } = AiPrototypeLimits.MaxInputTokens;
    public int MaxBillableOutputTokens { get; init; } = AiPrototypeLimits.MaxOutputTokens;
    public TimeSpan? ProviderTimeout { get; init; }
    public AiBudgetWindow? PrototypeBudgetWindow { get; init; }

    private readonly Guid[]? _prototypeAccounts = [];
    public Guid[]? PrototypeAccounts
    {
        get => _prototypeAccounts?.ToArray();
        init => _prototypeAccounts = value?.ToArray();
    }

    // Requirement only, not a caller-controlled assertion that a secret exists or is trusted.
    // No approved credential-availability reader exists. The validator rejects live activation.
    public bool RequiresTrustedCredential => true;

    public override string ToString() => "AI configuration [private values omitted; not execution authority]";
}

/// <summary>Not registered by this file. Success means valid DISABLED configuration only.
/// Optional prototype details may be absent while disabled, but supplied values must be valid.
/// Live configuration is rejected until the Section 02 authorities and trusted credential
/// availability can be verified. No self-asserted availability/approval flag can override this.</summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("AI configuration is missing.");
        var failures = new List<string>();
        if (options.Model != OpenAiResponsesRequestSerializer.ApprovedModel ||
            options.FramingRevision != OpenAiResponsesRequestSerializer.SerializationRevision ||
            options.ExecutionConfiguration != OpenAiResponsesRequestSerializer.ExecutionConfiguration)
            failures.Add("AI model, framing or execution configuration is unsupported.");
        if (options.OutputKind != AiTextOutputKind.Text ||
            options.MaxInputTokens != AiPrototypeLimits.MaxInputTokens ||
            options.MaxBillableOutputTokens != AiPrototypeLimits.MaxOutputTokens)
            failures.Add("AI text-only prototype token limits are invalid.");
        if (options.ProviderTimeout is { } timeout &&
            (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            failures.Add("AI provider timeout is outside the supported timer range.");
        var accounts = options.PrototypeAccounts;
        if (accounts is null || accounts.Length != 0 && !AiOperationPolicy.ValidAllowlist(accounts))
            failures.Add("AI prototype allowlist is invalid.");
        if (options.PrototypeBudgetWindow is { } window && !AiBudgetPolicy.ValidWindow(window))
            failures.Add("AI prototype budget window is invalid.");
        if (options.LiveExecutionEnabled)
        {
            if (options.ProviderTimeout is null) failures.Add("AI provider timeout is required for live configuration.");
            if (!AiOperationPolicy.ValidAllowlist(accounts)) failures.Add("AI prototype allowlist is required for live configuration.");
            if (options.PrototypeBudgetWindow is null) failures.Add("AI prototype budget window is required for live configuration.");
            failures.Add("Trusted AI credential availability cannot currently be verified.");
            failures.Add("Live AI integration is unavailable: approved counting, transaction, revision, pricing and dispatch authority are required.");
        }
        // Fixed diagnostic text only: never include name, configured values, account IDs or dates.
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public override string ToString() => "AI configuration validator [live activation unavailable]";
}
