
using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.UnitTests.AI.Fakes;

/// <summary>
/// Volatile, synthetic-data-only implementation of
/// IAiConversationRepository.
///
/// Not durable PostgreSQL persistence.
/// Never register for public or production use.
///
/// Every call checks IAiActorAuthorization.
/// Real database authorization must ultimately be
/// enforced transactionally.
/// </summary>
public sealed class InMemoryAiConversationRepository
    : IAiConversationRepository
{
    private const int MaximumReadLimit = 100;

    private readonly IAiActorAuthorization _authorization;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private readonly Dictionary<Guid, State>
        _conversations = new();

    private readonly Dictionary<
        (Guid Account,
         Guid Profile,
         AiModule Module,
         Guid Operation),
        Guid> _userOperations = new();

    public InMemoryAiConversationRepository(
        IAiActorAuthorization authorization,
        TimeProvider? clock = null)
    {
        _authorization = authorization
            ?? throw new ArgumentNullException(
                nameof(authorization));

        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AiStoredConversation>
        CreateWithUserMessageAsync(
            AiAuthorizedActor actor,
            Guid operationId,
            string validatedUserText,
            CancellationToken cancellationToken = default)
    {
        RequireOperation(operationId);
        RequireUserText(validatedUserText);

        await RequireAuthorizedAsync(
            actor, cancellationToken);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _clock.GetUtcNow();
            var key = OperationKey(actor, operationId);

            if (_userOperations.TryGetValue(
                key, out var existingId))
            {
                var existing = GetActive(
                    actor, existingId, now);

                if (existing is null ||
                    existing.Messages.Count == 0 ||
                    existing.Messages[0].Text !=
                        validatedUserText ||
                    existing.CreateOperationId != operationId)
                {
                    throw Unavailable();
                }

                return existing.Conversation;
            }

            var conversationId = Guid.NewGuid();

            var conversation = new AiStoredConversation(
                conversationId,
                actor.UserId,
                actor.ModuleProfileId,
                actor.Module,
                now,
                now,
                AiConversationExpiryPolicy
                    .GetExpiresAtUtc(now));

            var userMessage = new AiStoredAiMessage(
                Guid.NewGuid(),
                conversationId,
                AiStoredMessageRole.User,
                validatedUserText,
                now);

            var state = new State(
                conversation, operationId);

            state.Messages.Add(userMessage);
            state.UserByOperation.Add(
                operationId, userMessage);

            _conversations.Add(
                conversationId, state);

            _userOperations.Add(
                key, conversationId);

            return conversation;
        }
    }

    public async Task<AiStoredConversation?> FindAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedAsync(
            actor, cancellationToken))
        {
            return null;
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return GetActive(
                actor,
                conversationId,
                _clock.GetUtcNow())?.Conversation;
        }
    }

    public async Task<IReadOnlyList<AiStoredConversation>>
        ListAsync(
            AiAuthorizedActor actor,
            int limit,
            CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);

        if (!await IsAuthorizedAsync(
            actor, cancellationToken))
        {
            return Array.Empty<AiStoredConversation>();
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _clock.GetUtcNow();

            return _conversations.Values
                .Where(s => IsActiveForActor(
                    s, actor, now))
                .Select(s => s.Conversation)
                .OrderByDescending(
                    c => c.LastSavedUserMessageAtUtc)
                .ThenByDescending(c => c.CreatedAtUtc)
                .Take(limit)
                .ToArray();
        }
    }

    public async Task<IReadOnlyList<AiStoredAiMessage>>
        ReadRecentMessagesAsync(
            AiAuthorizedActor actor,
            Guid conversationId,
            int limit,
            CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);

        if (!await IsAuthorizedAsync(
            actor, cancellationToken))
        {
            return Array.Empty<AiStoredAiMessage>();
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = GetActive(
                actor,
                conversationId,
                _clock.GetUtcNow());

            return state is null
                ? Array.Empty<AiStoredAiMessage>()
                : state.Messages.TakeLast(limit).ToArray();
        }
    }

    public async Task<AiStoredAiMessage>
        AppendUserMessageAsync(
            AiAuthorizedActor actor,
            Guid conversationId,
            Guid operationId,
            string validatedUserText,
            CancellationToken cancellationToken = default)
    {
        RequireOperation(operationId);
        RequireUserText(validatedUserText);

        await RequireAuthorizedAsync(
            actor, cancellationToken);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = _clock.GetUtcNow();

            var state = GetActive(
                actor, conversationId, now)
                ?? throw Unavailable();

            var key = OperationKey(actor, operationId);

            if (_userOperations.TryGetValue(
                key, out var previousConversationId))
            {
                if (previousConversationId != conversationId ||
                    !state.UserByOperation.TryGetValue(
                        operationId, out var prior) ||
                    prior.Text != validatedUserText)
                {
                    throw new InvalidOperationException(
                        "AI operation conflict.");
                }

                // An idempotent retry does not renew expiry.
                return prior;
            }

            if (now <
                state.Conversation.LastSavedUserMessageAtUtc)
            {
                now = state.Conversation
                    .LastSavedUserMessageAtUtc;
            }

            var message = new AiStoredAiMessage(
                Guid.NewGuid(),
                conversationId,
                AiStoredMessageRole.User,
                validatedUserText,
                now);

            state.Messages.Add(message);
            state.UserByOperation.Add(
                operationId, message);

            _userOperations.Add(
                key, conversationId);

            state.Conversation = state.Conversation with
            {
                LastSavedUserMessageAtUtc = now,
                ExpiresAtUtc =
                    AiConversationExpiryPolicy
                        .GetExpiresAtUtc(now)
            };

            return message;
        }
    }

    public async Task<AiStoredAiMessage>
        AppendAssistantMessageAsync(
            AiAuthorizedActor actor,
            Guid conversationId,
            Guid operationId,
            string assistantText,
            CancellationToken cancellationToken = default)
    {
        RequireOperation(operationId);

        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new ArgumentException(
                "A non-empty assistant reply is required.",
                nameof(assistantText));
        }

        await RequireAuthorizedAsync(
            actor, cancellationToken);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = GetActive(
                actor,
                conversationId,
                _clock.GetUtcNow())
                ?? throw Unavailable();

            if (!state.UserByOperation.ContainsKey(
                operationId))
            {
                throw new InvalidOperationException(
                    "No matching accepted user operation.");
            }

            if (state.AssistantByOperation.TryGetValue(
                operationId, out var previous))
            {
                return previous;
            }

            var message = new AiStoredAiMessage(
                Guid.NewGuid(),
                conversationId,
                AiStoredMessageRole.Assistant,
                assistantText,
                _clock.GetUtcNow());

            state.Messages.Add(message);
            state.AssistantByOperation.Add(
                operationId, message);

            // Assistant replies never renew inactivity expiry.
            return message;
        }
    }

    public async Task DeleteAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await RequireAuthorizedAsync(
            actor, cancellationToken);

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_conversations.TryGetValue(
                    conversationId, out var state) ||
                !Owns(state.Conversation, actor))
            {
                return;
            }

            state.Deleted = true;

            // Remove synthetic conversation content.
            state.Messages.Clear();
            state.UserByOperation.Clear();
            state.AssistantByOperation.Clear();

            // Keep an in-memory tombstone to prevent
            // an idempotent retry resurrecting the chat.
            // Production purge/backup jobs are separate.
        }
    }

    private async Task RequireAuthorizedAsync(
        AiAuthorizedActor actor,
        CancellationToken token)
    {
        if (!await IsAuthorizedAsync(actor, token))
        {
            throw new UnauthorizedAccessException(
                "AI module access denied.");
        }
    }

    private async Task<bool> IsAuthorizedAsync(
        AiAuthorizedActor? actor,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(actor.Module))
        {
            return false;
        }

        var verified = await _authorization
            .GetAuthorizedActorAsync(actor.Module, token);

        token.ThrowIfCancellationRequested();

        return verified is not null &&
            verified.UserId == actor.UserId &&
            verified.ModuleProfileId ==
                actor.ModuleProfileId &&
            verified.Module == actor.Module;
    }

    private State? GetActive(
        AiAuthorizedActor actor,
        Guid id,
        DateTimeOffset now)
    {
        return _conversations.TryGetValue(
                id, out var state) &&
            IsActiveForActor(state, actor, now)
                ? state
                : null;
    }

    private static bool IsActiveForActor(
        State state,
        AiAuthorizedActor actor,
        DateTimeOffset now)
    {
        return !state.Deleted &&
            Owns(state.Conversation, actor) &&
            now < state.Conversation.ExpiresAtUtc;
    }

    private static bool Owns(
        AiStoredConversation conversation,
        AiAuthorizedActor actor)
    {
        return conversation.AccountId == actor.UserId &&
            conversation.ModuleProfileId ==
                actor.ModuleProfileId &&
            conversation.Module == actor.Module;
    }

    private static (
        Guid, Guid, AiModule, Guid) OperationKey(
            AiAuthorizedActor actor,
            Guid id)
    {
        return (
            actor.UserId,
            actor.ModuleProfileId,
            actor.Module,
            id);
    }

    private static void RequireOperation(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Operation ID is required.",
                nameof(id));
        }
    }

    private static void RequireLimit(int limit)
    {
        if (limit is < 1 or > MaximumReadLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Limit must be between 1 and 100.");
        }
    }

    private static void RequireUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            new StringInfo(text).LengthInTextElements > 500)
        {
            throw new ArgumentException(
                "Message must contain 1–500 text elements.",
                nameof(text));
        }
    }

    private static InvalidOperationException Unavailable()
    {
        return new InvalidOperationException(
            "AI conversation unavailable.");
    }

    private sealed class State(
        AiStoredConversation conversation,
        Guid createOperationId)
    {
        public AiStoredConversation Conversation { get; set; }
            = conversation;

        public Guid CreateOperationId { get; }
            = createOperationId;

        public bool Deleted { get; set; }

        public List<AiStoredAiMessage> Messages { get; }
            = new();

        public Dictionary<Guid, AiStoredAiMessage>
            UserByOperation
        { get; } = new();

        public Dictionary<Guid, AiStoredAiMessage>
            AssistantByOperation
        { get; } = new();
    }
}
