
using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Providers;

namespace Andril.AI.Services;

/// <summary>
/// Synthetic-data, built-in-mock-only development primitive. Never register as production AI Chat
/// or expose through HTTP. Not a persistent conversation service or live execution boundary.
/// Live generation requires separate guarded orchestration: fresh authorization/privacy,
/// admission/quota, trusted token measurement/budget, input safety/applicable consent,
/// fenced dispatch, output safety, atomic completion and fresh delivery authorization.
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

        cancellationToken.ThrowIfCancellationRequested();

        // Step 6: Deny missing, invalid or mismatched actors.
        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            actor.Module != module)
        {
            throw new UnauthorizedAccessException(
                "AI module access denied.");
        }

        // Step 7: Only the sealed, deterministic, network-free built-in mock may execute here.
        // An arbitrary implementation's IsMock claim is not evidence: reject BEFORE invocation.
        if (provider is not MockAiResponseProvider mock)
            throw new InvalidOperationException("Legacy AI chat supports only the built-in development mock.");

        cancellationToken.ThrowIfCancellationRequested();
        var response = await mock.GenerateAsync(
            new AiProviderPrompt(
                module,
                userMessage.Trim()),
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // Step 8: Reject unusable provider responses.
        if (response is null || !response.IsMock ||
            string.IsNullOrWhiteSpace(response.Text))
        {
            throw new InvalidOperationException(
                "AI response unavailable.");
        }

        // Step 9: Approval before generation is not approval to release a reply. This remains
        // the built-in-mock-only primitive, not production dispatch/transaction authority.
        var releaseActor = await authorization.GetAuthorizedActorAsync(module, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (releaseActor is null || releaseActor.UserId != actor.UserId ||
            releaseActor.ModuleProfileId != actor.ModuleProfileId || releaseActor.Module != module)
            throw new UnauthorizedAccessException("AI module access denied.");

        // Step 10: Return only to the same currently authorized actor scope.
        return new AiChatReply(
            module,
            response.Text,
            response.IsMock);
    }
}
