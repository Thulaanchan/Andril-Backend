
using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// Replaceable AI provider; no client-held secrets
/// or autonomous tool actions.
/// </summary>
public interface IAiResponseProvider
{
    Task<AiProviderResponse> GenerateAsync(
        AiProviderPrompt prompt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input passed to the AI response provider.
/// </summary>
public sealed record AiProviderPrompt(
    AiModule Module,
    string Text);

/// <summary>
/// Response returned by the AI provider.
/// </summary>
public sealed record AiProviderResponse(
    string Text,
    bool IsMock);
