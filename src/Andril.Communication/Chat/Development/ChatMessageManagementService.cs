using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat.Development;

/// <summary>
/// Dormant development message operations sharing D8's trusted conversation
/// boundary. Successful authorities remain confined to isolated test providers.
/// </summary>
internal sealed class ChatMessageManagementService(
    ChatConversationManagementService conversations, IChatMessageRepository repository)
{
    internal Task<ChatConversationResult<ChatMessage>> SendMessageAsync(
        ChatModule module, Guid conversationId, Guid requestId, string? text, string? moodTone = null,
        CancellationToken cancellationToken = default) =>
        conversations.WithConversationAsync<ChatMessage>(module, conversationId, ChatOperation.SubmitMessage,
            async (scope, conversation, token) =>
            {
                if (requestId == Guid.Empty) return new(ChatConversationStatus.InvalidRequest);
                ChatTextMessageDraft content;
                try { content = conversation.CreateTextMessageDraft(text, moodTone); }
                catch (ArgumentException) { return new(ChatConversationStatus.InvalidRequest); }
                var result = await repository.AppendAsync(scope, conversation, requestId, content, token);
                if (result.Status != ChatConversationStatus.Success)
                    return Failure<ChatMessage>(result.Status);
                var message = result.Value;
                return Valid(message, conversation) &&
                    message!.Sender.AccountId == scope.Participant.AccountId &&
                    message.Sender.ModuleProfileId == scope.Participant.ModuleProfileId &&
                    message.Text == content.Text && message.MoodTone == content.MoodTone
                    ? new(ChatConversationStatus.Success, message)
                    : new(ChatConversationStatus.Unavailable);
            }, cancellationToken);

    internal Task<ChatConversationResult<ChatMessageHistoryPage>> GetMessageHistoryAsync(
        ChatModule module, Guid conversationId, int pageSize, string? cursor = null,
        CancellationToken cancellationToken = default) =>
        conversations.WithConversationAsync<ChatMessageHistoryPage>(module, conversationId, ChatOperation.ReadMessageHistory,
            async (scope, conversation, token) =>
            {
                if (pageSize <= 0) return new(ChatConversationStatus.InvalidRequest);
                var result = await repository.ReadHistoryAsync(scope, conversation, pageSize, cursor, token);
                if (result.Status != ChatConversationStatus.Success)
                    return Failure<ChatMessageHistoryPage>(result.Status);
                return result.Value is not null && result.Value.Items is not null &&
                    result.Value.Items.Count <= pageSize && result.Value.Items.All(message => Valid(message, conversation))
                    ? result
                    : new(ChatConversationStatus.Unavailable);
            }, cancellationToken);

    internal Task<ChatConversationResult<ChatMessage>> GetMessageAsync(
        ChatModule module, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default) =>
        conversations.WithConversationAsync<ChatMessage>(module, conversationId, ChatOperation.ReadMessageHistory,
            async (scope, conversation, token) =>
            {
                if (messageId == Guid.Empty) return new(ChatConversationStatus.InvalidRequest);
                var result = await repository.FindAsync(scope, conversation, messageId, token);
                if (result.Status != ChatConversationStatus.Success)
                    return Failure<ChatMessage>(result.Status);
                return Valid(result.Value, conversation) && result.Value!.MessageId == messageId
                    ? result
                    : new(ChatConversationStatus.Unavailable);
            }, cancellationToken);

    private static ChatConversationResult<T> Failure<T>(ChatConversationStatus status) =>
        new(Enum.IsDefined(status) && status != ChatConversationStatus.Success ? status : ChatConversationStatus.Unavailable);

    private static bool Valid(ChatMessage? message, ChatConversation conversation) =>
        message is not null && message.Module == conversation.Module &&
        message.ConversationId == conversation.ConversationId &&
        DevelopmentChatConversationRepository.Contains(conversation, message.Sender);
}
