using Andril.Communication.Interfaces;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Proposed, undeployed private profile reference within a Chat conversation.
/// Does not establish ownership, active eligibility or permission to communicate.
/// The M2 composite profile foreign key and deletion lifecycle remain unapproved.
/// </summary>
internal sealed class ChatParticipantRecord
{
    private ChatParticipantRecord() { }

    public ChatModule Module { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid ModuleProfileId { get; private set; }
}
