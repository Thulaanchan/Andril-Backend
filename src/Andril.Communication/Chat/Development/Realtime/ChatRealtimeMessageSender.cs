using Andril.Communication.Interfaces;
namespace Andril.Communication.Chat.Development.Realtime;

/// <summary>Persist through D9 first. No outbox: failure/cancellation after storage requires history reconciliation.</summary>
internal sealed class ChatRealtimeMessageSender(ChatMessageManagementService messages, IChatRealtimeEventPublisher publisher)
{
    internal async Task<ChatRealtimeSendResult> SendAsync(ChatModule module, Guid conversationId,
        Guid requestId, string? text, string? moodTone = null, CancellationToken cancellationToken = default)
    {
        var stored = await messages.SendMessageAsync(module, conversationId, requestId, text, moodTone, cancellationToken);
        if (stored.Status != ChatConversationStatus.Success || stored.Value is null)
            return new(stored, ChatRealtimePublication.NotAttempted);
        try
        {
            var completed = await publisher.PublishMessageCreatedAsync(stored.Value, cancellationToken);
            return new(stored, completed ? ChatRealtimePublication.Attempted : ChatRealtimePublication.Uncertain);
        }
        catch (Exception)
        {
            // The canonical message is already stored. Do not turn a publication problem into a storage failure.
            return new(stored, ChatRealtimePublication.Uncertain);
        }
    }
}
