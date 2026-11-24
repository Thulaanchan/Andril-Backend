using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Validated text content for one module and conversation, not a persisted message
/// or proof of permission to send. No sender identity is accepted or resolved here.
/// Every operation still requires independent, authoritative Chat authorization.
/// </summary>
public sealed class ChatTextMessageDraft
{
    public ChatModule Module { get; }

    public Guid ConversationId { get; }

    public string Text { get; }

    public string? MoodTone { get; }

    private ChatTextMessageDraft(ChatModule module, Guid conversationId, string text, string? moodTone)
    {
        Module = module;
        ConversationId = conversationId;
        Text = text;
        MoodTone = moodTone;
    }

    /// <summary>
    /// Validates only content and target shape. Preserves text exactly, including
    /// Unicode and whitespace around content; no approved length limit exists yet.
    /// Identifiers select a target and do not establish ownership or participation.
    /// </summary>
    public static ChatTextMessageDraft Create(
        ChatModule module,
        Guid conversationId,
        string? text,
        string? moodTone = null)
    {
        if (!Enum.IsDefined(module))
        {
            throw new ArgumentOutOfRangeException(nameof(module), "Unsupported ANDRIL module.");
        }

        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation identifier is required.", nameof(conversationId));
        }

        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Message text is required.", nameof(text));
        }

        ChatMoodTonePolicy.Validate(module, moodTone);

        return new ChatTextMessageDraft(module, conversationId, text, moodTone);
    }
}
