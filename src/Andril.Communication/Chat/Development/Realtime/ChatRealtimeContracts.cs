using Andril.Communication.Interfaces;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Andril.API")]
[assembly: InternalsVisibleTo("Andril.IntegrationTests")]

namespace Andril.Communication.Chat.Development.Realtime;

// Candidate protocol v1. None of these types is a production authority or HTTP registration.
internal sealed record ChatRealtimeMessage(Guid MessageId, ChatModule Module, Guid ConversationId,
    string Text, DateTimeOffset CreatedAtUtc, string? MoodTone)
{
    internal static ChatRealtimeMessage From(ChatMessage value) => new(value.MessageId, value.Module,
        value.ConversationId, value.Text, value.CreatedAtUtc, value.MoodTone);
    public override string ToString() => nameof(ChatRealtimeMessage);
}

// Metadata only: clients must reconcile content with D9 history. MessageId is the stable logical event ID.
internal sealed record ChatMessageCreatedEvent(int Version, Guid EventId, ChatModule Module,
    Guid ConversationId, Guid MessageId, DateTimeOffset CreatedAtUtc)
{
    internal static ChatMessageCreatedEvent From(ChatMessage value) =>
        new(1, value.MessageId, value.Module, value.ConversationId, value.MessageId, value.CreatedAtUtc);
    public override string ToString() => nameof(ChatMessageCreatedEvent);
}

internal sealed record ChatConversationAccessRevokedEvent(int Version, ChatModule Module, Guid ConversationId)
{
    public override string ToString() => nameof(ChatConversationAccessRevokedEvent);
}
internal sealed record ChatRealtimeCheckpoint(Guid? LastReconciledMessageId)
{
    public override string ToString() => nameof(ChatRealtimeCheckpoint);
}
internal sealed record ChatRealtimeHistoryPage(IReadOnlyList<ChatRealtimeMessage> Items, string? NextCursor)
{
    public override string ToString() => nameof(ChatRealtimeHistoryPage);
}

// Resolved afresh by a trusted server adapter; clients never supply this scope or these services.
internal sealed record ChatRealtimeSession(ChatConversationScope Scope, ChatMessageManagementService Messages,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => nameof(ChatRealtimeSession);
}
internal interface IChatRealtimeDevelopmentAuthority
{
    Task<ChatRealtimeSession?> ResolveAsync(Guid verifiedSubject, ChatModule module, CancellationToken cancellationToken);
    // The four D3 operations do not imply a positive realtime-subscription permission.
    Task<bool> AuthorizeSubscriptionAsync(ChatRealtimeSession session, Guid conversationId,
        CancellationToken cancellationToken) => Task.FromResult(false);
}
internal interface IChatRealtimeClient
{
    Task MessageCreatedAsync(ChatMessageCreatedEvent value, CancellationToken cancellationToken);
    Task AccessRevokedAsync(ChatConversationAccessRevokedEvent value, CancellationToken cancellationToken);
}
internal interface IChatRealtimeEventPublisher
{
    // True means current delivery attempts completed, never durable or exactly-once delivery.
    Task<bool> PublishMessageCreatedAsync(ChatMessage canonical, CancellationToken cancellationToken);
}
internal enum ChatRealtimePublication { NotAttempted, Attempted, Uncertain }
internal sealed record ChatRealtimeSendResult(ChatConversationResult<ChatMessage> Storage,
    ChatRealtimePublication Publication)
{
    public override string ToString() => $"{nameof(ChatRealtimeSendResult)}({Storage.Status}, {Publication})";
}
