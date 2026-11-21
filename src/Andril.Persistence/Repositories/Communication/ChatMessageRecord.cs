using Andril.Communication.Interfaces;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Proposed, undeployed message storage shape, not an API DTO or delivery path.
/// Server metadata, content and authorization must be validated by future trusted
/// boundaries. No retry, retention, read-receipt or delivery semantics are implied.
/// </summary>
internal sealed class ChatMessageRecord
{
    private ChatMessageRecord() { }

    public Guid MessageId { get; private set; }
    public ChatModule Module { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid SenderModuleProfileId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public string? MoodTone { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
