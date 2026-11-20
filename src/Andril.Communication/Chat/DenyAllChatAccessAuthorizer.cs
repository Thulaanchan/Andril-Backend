using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Safe fallback until authoritative M2 profile access and M5 relationship
/// authorization are integrated. Always denies; no repositories, message delivery,
/// network calls or configurable authorization bypasses are used.
/// </summary>
public sealed class DenyAllChatAccessAuthorizer : IChatAccessAuthorizer
{
    public Task<ChatAuthorizedActor?> AuthorizeConversationAsync(
        ChatModule module,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ChatAuthorizedActor?>(null);
    }
}
