using Andril.Communication.Interfaces;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Proposed, undeployed Chat storage shape. Not a response DTO or authorization.
/// No production context, persistence operations or participant-count rules exist here.
/// </summary>
internal sealed class ChatConversationRecord
{
    private ChatConversationRecord() { }

    public ChatModule Module { get; private set; }
    public Guid ConversationId { get; private set; }
}
