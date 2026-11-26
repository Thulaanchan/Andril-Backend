using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat.Development;

// Dormant development contracts. No production authority, registration or HTTP DTO.
internal sealed record ChatConversationScope(Guid VerifiedSubjectId, ChatParticipantReference Participant)
{
    public override string ToString() => nameof(ChatConversationScope);
}

internal enum ChatConversationStatus { Unavailable, Success, Denied, InvalidRequest, Conflict }

internal sealed record ChatConversationResult<T>(ChatConversationStatus Status, T? Value = default)
{
    public override string ToString() => $"{nameof(ChatConversationResult<T>)}({Status})";
}

/// <summary>
/// Test/development seam for owner decisions that have no ratified production adapter.
/// Implementations must resolve scope from the verified subject and independently
/// authorize every creation and listing item using current owner evidence.
/// Ordinary profile access, participation and detail permissions cannot supply these grants.
/// </summary>
internal interface IChatConversationDevelopmentAuthority
{
    Task<ChatConversationScope?> ReadScopeAsync(Guid verifiedSubjectId, ChatModule module, CancellationToken cancellationToken);

    Task<ChatAuthorizationOutcome> AuthorizeCreationAsync(
        ChatConversationScope scope, ChatConversation candidate, Guid requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ChatAuthorizationOutcome.Unavailable);
    }

    Task<ChatAuthorizationOutcome> AuthorizeListingItemAsync(
        ChatConversationScope scope, ChatConversation conversation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ChatAuthorizationOutcome.Unavailable);
    }
}

/// <summary>Internal repository seam. No production persistence implementation is registered.</summary>
internal interface IChatConversationRepository
{
    Task<ChatConversation?> FindAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatConversation>> ListAsync(ChatParticipantReference participant, CancellationToken cancellationToken);
    Task<ChatConversationResult<ChatConversation>> GetOrCreateAsync(
        ChatConversationScope scope, Guid requestId, ChatConversation candidate, CancellationToken cancellationToken);
}
