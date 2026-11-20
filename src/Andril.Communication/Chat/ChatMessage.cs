using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Immutable, structurally validated message in one conversation and module.
/// This is not an API DTO, persisted record, delivery receipt or authorization.
/// The sender snapshot and all identifiers still require authoritative M2/M5
/// checks at the server/persistence boundary before any operation is permitted.
/// </summary>
public sealed class ChatMessage
{
    public ChatModule Module { get; }

    public Guid MessageId { get; }

    public Guid ConversationId { get; }

    public ChatParticipantReference Sender { get; }

    public string Text { get; }

    public string? MoodTone { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    private ChatMessage(
        Guid messageId,
        ChatTextMessageDraft content,
        ChatParticipantReference sender,
        DateTimeOffset createdAtUtc)
    {
        Module = content.Module;
        MessageId = messageId;
        ConversationId = content.ConversationId;
        Sender = sender;
        Text = content.Text;
        MoodTone = content.MoodTone;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// In-process server creation with a generated ID and server UTC clock.
    /// No client-supplied message ID or timestamp is accepted. Generating these
    /// values does not authorize messaging or guarantee persistent uniqueness.
    /// </summary>
    public static ChatMessage Create(
        ChatConversation conversation,
        ChatParticipantReference sender,
        string? text,
        string? moodTone = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return CreateFromServerValues(conversation.Module, Guid.NewGuid(), conversation.ConversationId,
            conversation, sender, text, DateTimeOffset.UtcNow, moodTone);
    }

    /// <summary>
    /// Internal seam for metadata generated or validated by a trusted server
    /// boundary, never raw client metadata. Structural validation cannot prove
    /// provenance, persistent ID uniqueness, profile ownership or permission.
    /// Persistence must enforce uniqueness and recheck authorization atomically
    /// with future writes; neither persistence nor revocation enforcement exists here.
    /// </summary>
    internal static ChatMessage CreateFromServerValues(
        ChatModule module,
        Guid messageId,
        Guid conversationId,
        ChatConversation conversation,
        ChatParticipantReference sender,
        string? text,
        DateTimeOffset createdAt,
        string? moodTone = null)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("A message identifier is required.", nameof(messageId));
        }

        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(sender);

        // STEP 02 remains the single content/module/target-shape validator.
        var content = ChatTextMessageDraft.Create(module, conversationId, text, moodTone);

        if (conversation.Module != module || conversation.ConversationId != conversationId)
        {
            throw new ArgumentException("Message target must match the conversation.", nameof(conversation));
        }

        if (sender.Module != module)
        {
            throw new ArgumentException("Sender must belong to the conversation module.", nameof(sender));
        }

        var participant = conversation.Participants.FirstOrDefault(candidate =>
            candidate.Module == module && candidate.AccountId == sender.AccountId &&
            candidate.ModuleProfileId == sender.ModuleProfileId);

        if (participant is null)
        {
            throw new ArgumentException("Sender must be included in the conversation snapshot.", nameof(sender));
        }

        if (createdAt == DateTimeOffset.MinValue || createdAt == DateTimeOffset.MaxValue)
        {
            throw new ArgumentException("A valid server creation timestamp is required.", nameof(createdAt));
        }

        // Keep the canonical immutable participant reference from the snapshot.
        return new ChatMessage(messageId, content, participant, createdAt.ToUniversalTime());
    }

    public override string ToString() => nameof(ChatMessage);
}
