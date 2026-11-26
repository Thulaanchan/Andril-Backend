namespace Andril.Communication.Chat.Development;

// Backend-only page over existing immutable ChatMessage objects. No HTTP DTO.
internal sealed record ChatMessageHistoryPage(IReadOnlyList<ChatMessage> Items, string? NextCursor)
{
    public override string ToString() => nameof(ChatMessageHistoryPage);
}

internal interface IChatMessageRepository
{
    Task<ChatConversationResult<ChatMessage>> AppendAsync(
        ChatConversationScope scope, ChatConversation conversation, Guid requestId,
        ChatTextMessageDraft content, CancellationToken cancellationToken);

    Task<ChatConversationResult<ChatMessageHistoryPage>> ReadHistoryAsync(
        ChatConversationScope scope, ChatConversation conversation, int pageSize,
        string? cursor, CancellationToken cancellationToken);

    Task<ChatConversationResult<ChatMessage>> FindAsync(
        ChatConversationScope scope, ChatConversation conversation, Guid messageId,
        CancellationToken cancellationToken);
}
