using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat.Development;

/// <summary>
/// In-memory development storage only. The gate makes local creates/replays atomic,
/// not external M2/M5 revocation or production transactions. No DI registration.
/// </summary>
internal sealed class DevelopmentChatConversationRepository : IChatConversationRepository, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(ChatModule, Guid), ChatConversation> conversations = [];
    private readonly Dictionary<(Guid Subject, ChatModule Module, Guid Account, Guid Profile, Guid Request), ChatConversation> requests = [];

    public async Task<ChatConversation?> FindAsync(ChatModule module, Guid conversationId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return conversations.GetValueOrDefault((module, conversationId));
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<ChatConversation>> ListAsync(ChatParticipantReference participant, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Array.AsReadOnly(conversations.Values
                .Where(conversation => Contains(conversation, participant))
                .OrderBy(conversation => conversation.ConversationId).ToArray());
        }
        finally { gate.Release(); }
    }

    public async Task<ChatConversationResult<ChatConversation>> GetOrCreateAsync(
        ChatConversationScope scope, Guid requestId, ChatConversation candidate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestId == Guid.Empty || !Contains(candidate, scope.Participant))
                return new(ChatConversationStatus.InvalidRequest);

            var participant = scope.Participant;
            var key = (scope.VerifiedSubjectId, participant.Module, participant.AccountId, participant.ModuleProfileId, requestId);
            if (requests.TryGetValue(key, out var replay))
                return Equivalent(replay, candidate)
                    ? new(ChatConversationStatus.Success, replay)
                    : new(ChatConversationStatus.Conflict);

            var target = (candidate.Module, candidate.ConversationId);
            if (conversations.TryGetValue(target, out var existing))
            {
                if (!Contains(existing, participant)) return new(ChatConversationStatus.Unavailable);
                if (!Equivalent(existing, candidate)) return new(ChatConversationStatus.Conflict);
                requests.Add(key, existing);
                return new(ChatConversationStatus.Success, existing);
            }

            // No awaits or cancellation points between the two in-memory mutations.
            conversations.Add(target, candidate);
            requests.Add(key, candidate);
            return new(ChatConversationStatus.Success, candidate);
        }
        finally { gate.Release(); }
    }

    internal static bool Contains(ChatConversation conversation, ChatParticipantReference participant) =>
        conversation.Module == participant.Module && conversation.Participants.Any(value =>
            value.Module == participant.Module && value.AccountId == participant.AccountId &&
            value.ModuleProfileId == participant.ModuleProfileId);

    internal static bool Equivalent(ChatConversation left, ChatConversation right) =>
        left.Module == right.Module && left.ConversationId == right.ConversationId &&
        left.Participants.Count == right.Participants.Count &&
        left.Participants.All(participant => Contains(right, participant));

    public void Dispose() => gate.Dispose();
}
