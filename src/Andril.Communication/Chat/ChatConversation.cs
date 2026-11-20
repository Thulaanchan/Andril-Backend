using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Immutable structural snapshot of a module-scoped conversation, not proof of
/// its existence, membership or permission to communicate. No participant-count
/// or group eligibility rules are established here. This is not an API DTO.
/// Every operation still needs authoritative M2/M5 authorization. Social
/// match-based access requires 18+, opt-in, active mutual matching and no blocks;
/// blocking or unmatching must revoke eligibility. Missing evidence must deny.
/// </summary>
public sealed class ChatConversation
{
    public ChatModule Module { get; }

    public Guid ConversationId { get; }

    public IReadOnlyList<ChatParticipantReference> Participants { get; }

    private ChatConversation(
        ChatModule module,
        Guid conversationId,
        ChatParticipantReference[] participants)
    {
        Module = module;
        ConversationId = conversationId;
        Participants = Array.AsReadOnly(participants);
    }

    public static ChatConversation Create(
        ChatModule module,
        Guid conversationId,
        IEnumerable<ChatParticipantReference> participants)
    {
        if (!Enum.IsDefined(module))
        {
            throw new ArgumentOutOfRangeException(nameof(module), "Unsupported ANDRIL module.");
        }

        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation identifier is required.", nameof(conversationId));
        }

        ArgumentNullException.ThrowIfNull(participants);

        // Own the snapshot so caller mutations cannot change its validated scope.
        var snapshot = participants.ToArray();
        var profileIds = new HashSet<Guid>();

        foreach (var participant in snapshot)
        {
            if (participant is null || participant.Module != module)
            {
                throw new ArgumentException("Participants must belong to the conversation module.", nameof(participants));
            }

            // ModuleProfileId identifies a participant within this module.
            // Changing AccountId must not let the same profile appear twice.
            if (!profileIds.Add(participant.ModuleProfileId))
            {
                throw new ArgumentException("Duplicate participant references are not allowed.", nameof(participants));
            }
        }

        return new ChatConversation(module, conversationId, snapshot);
    }

    /// <summary>
    /// Uses STEP 02 validation with this exact target; does not select a sender,
    /// authorize membership, persist anything or send a message.
    /// </summary>
    public ChatTextMessageDraft CreateTextMessageDraft(string? text, string? moodTone = null) =>
        ChatTextMessageDraft.Create(Module, ConversationId, text, moodTone);
}
