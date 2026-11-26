using System.Security.Cryptography;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat.Development;

/// <summary>
/// Internal in-memory development storage. One gated append is the local commit
/// point. Owner revocation and durable transaction coordination remain pending.
/// Sequence and cursor registries are development ordering/pagination mechanisms.
/// </summary>
internal sealed class DevelopmentChatMessageRepository(
    TimeProvider? clock = null, Func<Guid>? createMessageId = null) : IChatMessageRepository, IDisposable
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Func<Guid> createMessageId = createMessageId ?? Guid.NewGuid;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<(ChatModule, Guid), List<Entry>> messages = [];
    private readonly Dictionary<string, Cursor> cursors = new(StringComparer.Ordinal);

    public async Task<ChatConversationResult<ChatMessage>> AppendAsync(
        ChatConversationScope scope, ChatConversation conversation, Guid requestId,
        ChatTextMessageDraft content, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DevelopmentChatConversationRepository.Contains(conversation, scope.Participant))
                return new(ChatConversationStatus.Unavailable);
            if (requestId == Guid.Empty || content.Module != conversation.Module ||
                content.ConversationId != conversation.ConversationId)
                return new(ChatConversationStatus.InvalidRequest);

            var target = (conversation.Module, conversation.ConversationId);
            var owner = Owner(scope, conversation);
            messages.TryGetValue(target, out var history);
            var replay = history?.FirstOrDefault(entry => entry.Owner == owner && entry.RequestId == requestId);
            if (replay is not null)
                return replay.Message.Text == content.Text && replay.Message.MoodTone == content.MoodTone
                    ? new(ChatConversationStatus.Success, replay.Message)
                    : new(ChatConversationStatus.Conflict);

            var id = createMessageId();
            if (id == Guid.Empty || messages.Values.Any(entries => entries.Any(entry => entry.Message.MessageId == id)))
                return new(ChatConversationStatus.Unavailable);
            var message = ChatMessage.CreateFromServerValues(conversation.Module, id, conversation.ConversationId,
                conversation, scope.Participant, content.Text, clock.GetUtcNow(), content.MoodTone);
            var sequence = checked((history?.LastOrDefault()?.Sequence ?? 0) + 1);
            var stored = new Entry(owner, requestId, sequence, message);
            cancellationToken.ThrowIfCancellationRequested();

            // Each append has exactly one observable collection mutation. The
            // entry itself carries the idempotency association for safe replay.
            if (history is null) messages.Add(target, [stored]);
            else history.Add(stored);
            return new(ChatConversationStatus.Success, message);
        }
        finally { gate.Release(); }
    }

    public async Task<ChatConversationResult<ChatMessageHistoryPage>> ReadHistoryAsync(
        ChatConversationScope scope, ChatConversation conversation, int pageSize,
        string? cursor, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DevelopmentChatConversationRepository.Contains(conversation, scope.Participant))
                return new(ChatConversationStatus.Unavailable);
            if (pageSize <= 0) return new(ChatConversationStatus.InvalidRequest);
            var owner = Owner(scope, conversation);
            messages.TryGetValue((conversation.Module, conversation.ConversationId), out var stored);
            var history = stored ?? [];
            var upper = history.LastOrDefault()?.Sequence ?? 0;
            long? before = null;
            if (cursor is not null)
            {
                if (!cursors.TryGetValue(cursor, out var position) || position.Owner != owner || position.PageSize != pageSize)
                    return new(ChatConversationStatus.InvalidRequest);
                upper = position.Upper;
                before = position.Before;
            }

            // Pin the first-page sequence ceiling so concurrent later appends
            // cannot duplicate or displace records in an existing traversal.
            var remaining = history.Where(entry => entry.Sequence <= upper &&
                (before is null || entry.Sequence < before)).Reverse().ToArray();
            var page = remaining.Take(pageSize).ToArray();
            string? next = null;
            if (remaining.Length > page.Length && page.Length > 0)
            {
                next = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                cursors.Add(next, new(owner, pageSize, upper, page[^1].Sequence));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(ChatConversationStatus.Success,
                new(Array.AsReadOnly(page.Select(entry => entry.Message).ToArray()), next));
        }
        finally { gate.Release(); }
    }

    public async Task<ChatConversationResult<ChatMessage>> FindAsync(
        ChatConversationScope scope, ChatConversation conversation, Guid messageId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DevelopmentChatConversationRepository.Contains(conversation, scope.Participant))
                return new(ChatConversationStatus.Unavailable);
            var message = messages.GetValueOrDefault((conversation.Module, conversation.ConversationId))?
                .FirstOrDefault(entry => entry.Message.MessageId == messageId)?.Message;
            return message is null ? new(ChatConversationStatus.Unavailable) : new(ChatConversationStatus.Success, message);
        }
        finally { gate.Release(); }
    }

    private static OwnerKey Owner(ChatConversationScope scope, ChatConversation conversation) =>
        new(scope.VerifiedSubjectId, scope.Participant.Module, scope.Participant.AccountId,
            scope.Participant.ModuleProfileId, conversation.ConversationId);

    private sealed record OwnerKey(Guid Subject, ChatModule Module, Guid Account, Guid Profile, Guid Conversation)
    {
        public override string ToString() => nameof(OwnerKey);
    }
    private sealed record Entry(OwnerKey Owner, Guid RequestId, long Sequence, ChatMessage Message);
    private sealed record Cursor(OwnerKey Owner, int PageSize, long Upper, long Before);

    public void Dispose() => gate.Dispose();
}
