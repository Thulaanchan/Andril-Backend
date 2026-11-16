
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;

namespace Andril.AI.Providers;

/// <summary>
/// Simulated, explicitly labeled responses for
/// synthetic-data testing only.
///
/// Does not call a real AI model, access private
/// user data, or execute agent tools.
/// </summary>
public sealed class MockAiResponseProvider
    : IAiResponseProvider
{
    public Task<AiProviderResponse> GenerateAsync(
        AiProviderPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = prompt.Module switch
        {
            AiModule.Learning =>
                "[MOCK] Learning AI test response. " +
                "No live model or Learning tools were used.",

            AiModule.Hobbies =>
                "[MOCK] Hobbies AI test response. " +
                "No live model or Hobbies data were used.",

            AiModule.Social =>
                "[MOCK] Social AI test response. " +
                "No live model or Social matching data were used.",

            _ => throw new ArgumentOutOfRangeException(
                nameof(prompt),
                "Unsupported AI module.")
        };

        return Task.FromResult(
            new AiProviderResponse(
                text,
                IsMock: true));
    }
}
