
using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Services;

/// <summary>
/// First mock-first AI service boundary.
/// Not a persistent conversation service.
///
/// Do not expose through HTTP until authenticated,
/// database-backed module authorization, privacy
/// acknowledgment and quota controls are implemented.
/// </summary>
public sealed class AiChatService(
    IAiActorAuthorization authorization,
    IAiResponseProvider provider)
{
    public const int MaxInputCharacters = 500;

    public async Task<AiChatReply> ReplyAsync(
        AiModule module,
        string? userMessage,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Respect cancellation.
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: Allow only the three ANDRIL modules.
        if (!Enum.IsDefined(module))
        {
            throw new AiChatValidationException(
                "Unsupported AI module.");
        }

        // Step 3: Reject empty or whitespace-only messages.
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new AiChatValidationException(
                "A non-empty message is required.");
        }

        // Step 4: Enforce the approved 500-character limit.
        // Unicode text-element counting is provisional.
        // Confirm Flutter/API counting consistency later.
        if (new StringInfo(userMessage).LengthInTextElements
            > MaxInputCharacters)
        {
            throw new AiChatValidationException(
                "AI messages may contain at most 500 characters.");
        }

        // Step 5: Resolve the actor through a trusted
        // backend authorization implementation.
        var actor = await authorization
            .GetAuthorizedActorAsync(module, cancellationToken);

        // Step 6: Deny missing, invalid or mismatched actors.
        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            actor.Module != module)
        {
            throw new UnauthorizedAccessException(
                "AI module access denied.");
        }

        // Step 7: Generate a response only after authorization.
        var response = await provider.GenerateAsync(
            new AiProviderPrompt(
                module,
                userMessage.Trim()),
            cancellationToken);

        // Step 8: Reject unusable provider responses.
        if (response is null ||
            string.IsNullOrWhiteSpace(response.Text))
        {
            throw new InvalidOperationException(
                "AI response unavailable.");
        }

        // Step 9: Return the result.
        return new AiChatReply(
            module,
            response.Text,
            response.IsMock);
    }
}
