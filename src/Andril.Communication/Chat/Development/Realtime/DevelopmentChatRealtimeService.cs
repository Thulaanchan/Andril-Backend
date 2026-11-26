using Andril.Communication.Interfaces;
namespace Andril.Communication.Chat.Development.Realtime;

/// <summary>
/// Development-only connection/subscription registry. Never registered in production.
/// Serializes local routing changes and rechecks owner authority plus D9 history access before delivery.
/// It cannot retract in-flight network bytes or coordinate distributed production revocation.
/// </summary>
internal sealed class DevelopmentChatRealtimeService(IChatRealtimeDevelopmentAuthority? authority = null,
    TimeProvider? timeProvider = null) : IChatRealtimeEventPublisher, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);

    internal async Task<ChatConversationStatus> ConnectAsync(string connectionId, Guid verifiedSubject,
        DateTimeOffset authenticationExpires, IChatRealtimeClient client, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(connectionId) || verifiedSubject == Guid.Empty ||
                !FiniteFuture(authenticationExpires)) return ChatConversationStatus.Unavailable;
            if (connections.ContainsKey(connectionId)) return ChatConversationStatus.Conflict;
            connections.Add(connectionId, new(verifiedSubject, authenticationExpires, client));
            return ChatConversationStatus.Success;
        }
        finally { gate.Release(); }
    }

    internal async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { connections.Remove(connectionId); }
        finally { gate.Release(); }
    }

    internal async Task<ChatConversationResult<ChatRealtimeCheckpoint>> SubscribeAsync(string connectionId,
        ChatModule module, Guid conversationId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var connection)) return new(ChatConversationStatus.Unavailable);
            var key = (module, conversationId);
            var session = await ResolveAsync(connection, module, conversationId, null, cancellationToken);
            if (session is not null)
            {
                var history = await session.Messages.GetMessageHistoryAsync(module, conversationId, 1, cancellationToken: cancellationToken);
                var current = await ResolveAsync(connection, module, conversationId, session.Scope, cancellationToken);
                if (history.Status == ChatConversationStatus.Success && current is not null)
                {
                    if (!connection.Subscriptions.TryGetValue(key, out var subscription) || !Same(subscription.Scope, current.Scope))
                        connection.Subscriptions[key] = subscription = new(current.Scope);
                    return new(ChatConversationStatus.Success, new(subscription.Checkpoint));
                }
            }
            connection.Subscriptions.Remove(key);
            return new(ChatConversationStatus.Unavailable);
        }
        finally { gate.Release(); }
    }

    internal async Task<ChatConversationStatus> UnsubscribeAsync(string connectionId, ChatModule module,
        Guid conversationId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!connections.TryGetValue(connectionId, out var connection)) return ChatConversationStatus.Unavailable;
            connection.Subscriptions.Remove((module, conversationId));
            return ChatConversationStatus.Success;
        }
        finally { gate.Release(); }
    }

    internal async Task<ChatConversationResult<ChatRealtimeHistoryPage>> ReadHistoryAsync(string connectionId,
        ChatModule module, Guid conversationId, int pageSize, string? cursor = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!TrySubscription(connectionId, module, conversationId, out var connection, out var subscription))
                return new(ChatConversationStatus.Unavailable);
            var session = await ResolveAsync(connection!, module, conversationId, subscription!.Scope, cancellationToken);
            if (session is null) { await RevokeAsync(connection!, module, conversationId, cancellationToken); return new(ChatConversationStatus.Unavailable); }
            var result = await session.Messages.GetMessageHistoryAsync(module, conversationId, pageSize, cursor, cancellationToken);
            if (await ResolveAsync(connection!, module, conversationId, subscription.Scope, cancellationToken) is null ||
                result.Status is ChatConversationStatus.Denied or ChatConversationStatus.Unavailable)
            { await RevokeAsync(connection!, module, conversationId, cancellationToken); return new(ChatConversationStatus.Unavailable); }
            return result.Status == ChatConversationStatus.Success && result.Value is not null
                ? new(ChatConversationStatus.Success, new(result.Value.Items.Select(ChatRealtimeMessage.From).ToArray(), result.Value.NextCursor))
                : new(result.Status);
        }
        finally { gate.Release(); }
    }

    internal async Task<ChatConversationResult<ChatRealtimeCheckpoint>> AcknowledgeAsync(string connectionId,
        ChatModule module, Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!TrySubscription(connectionId, module, conversationId, out var connection, out var subscription))
                return new(ChatConversationStatus.Unavailable);
            var session = await ResolveAsync(connection!, module, conversationId, subscription!.Scope, cancellationToken);
            if (session is null) return new(ChatConversationStatus.Unavailable);
            var result = await session.Messages.GetMessageAsync(module, conversationId, messageId, cancellationToken);
            if (result.Status != ChatConversationStatus.Success ||
                await ResolveAsync(connection!, module, conversationId, subscription.Scope, cancellationToken) is null)
                return new(ChatConversationStatus.Unavailable);
            subscription.Checkpoint = messageId;
            return new(ChatConversationStatus.Success, new(messageId));
        }
        finally { gate.Release(); }
    }

    public async Task<bool> PublishMessageCreatedAsync(ChatMessage canonical, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var completed = true;
            foreach (var connection in connections.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connection.Subscriptions.TryGetValue((canonical.Module, canonical.ConversationId), out var subscription)) continue;
                var session = await ResolveAsync(connection, canonical.Module, canonical.ConversationId, subscription.Scope, cancellationToken);
                ChatConversationResult<ChatMessage>? result = session is null ? null :
                    await session.Messages.GetMessageAsync(canonical.Module, canonical.ConversationId, canonical.MessageId, cancellationToken);
                if (result?.Status != ChatConversationStatus.Success || result.Value is null ||
                    await ResolveAsync(connection, canonical.Module, canonical.ConversationId, subscription.Scope, cancellationToken) is null)
                { await RevokeAsync(connection, canonical.Module, canonical.ConversationId, cancellationToken); continue; }
                if (subscription.Delivered.Contains(canonical.MessageId)) continue;
                try
                {
                    await connection.Client.MessageCreatedAsync(ChatMessageCreatedEvent.From(result.Value), cancellationToken);
                    subscription.Delivered.Add(canonical.MessageId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { completed = false; }
            }
            return completed;
        }
        finally { gate.Release(); }
    }

    // Explicit refresh hook, not a fabricated M2/M5 revocation event adapter. Every delivery also revalidates.
    internal async Task RevalidateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var connection in connections.Values)
            foreach (var key in connection.Subscriptions.Keys.ToArray())
            {
                var subscription = connection.Subscriptions[key];
                var session = await ResolveAsync(connection, key.Module, key.Conversation, subscription.Scope, cancellationToken);
                var history = session is null ? null : await session.Messages.GetMessageHistoryAsync(key.Module, key.Conversation, 1, cancellationToken: cancellationToken);
                if (history?.Status != ChatConversationStatus.Success ||
                    await ResolveAsync(connection, key.Module, key.Conversation, subscription.Scope, cancellationToken) is null)
                    await RevokeAsync(connection, key.Module, key.Conversation, cancellationToken);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<ChatRealtimeSession?> ResolveAsync(Connection connection, ChatModule module, Guid conversationId,
        ChatConversationScope? expected, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (authority is null || !Enum.IsDefined(module) || conversationId == Guid.Empty || !FiniteFuture(connection.Expires)) return null;
            var session = await authority.ResolveAsync(connection.Subject, module, cancellationToken);
            if (session is null || session.Scope.VerifiedSubjectId != connection.Subject || session.Scope.Participant.Module != module ||
                !FiniteFuture(session.ExpiresAtUtc) || (expected is not null && !Same(expected, session.Scope))) return null;
            if (!await authority.AuthorizeSubscriptionAsync(session, conversationId, cancellationToken)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            // Authority checks may await owner work. Re-resolve the trusted scope before routing.
            var current = await authority.ResolveAsync(connection.Subject, module, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return current is not null && Same(session.Scope, current.Scope) &&
                FiniteFuture(connection.Expires) && FiniteFuture(session.ExpiresAtUtc) && FiniteFuture(current.ExpiresAtUtc) ? current : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private bool TrySubscription(string id, ChatModule module, Guid conversation, out Connection? connection, out Subscription? subscription)
    {
        subscription = null;
        return connections.TryGetValue(id, out connection) && connection.Subscriptions.TryGetValue((module, conversation), out subscription);
    }
    private async Task RevokeAsync(Connection connection, ChatModule module, Guid conversation, CancellationToken cancellationToken)
    {
        connection.Subscriptions.Remove((module, conversation));
        try { await connection.Client.AccessRevokedAsync(new(1, module, conversation), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* The subscription is removed even if its control event is lost. */ }
    }
    private bool FiniteFuture(DateTimeOffset value) => value != DateTimeOffset.MaxValue && value > clock.GetUtcNow();
    private static bool Same(ChatConversationScope left, ChatConversationScope right) =>
        left.VerifiedSubjectId == right.VerifiedSubjectId && left.Participant.Module == right.Participant.Module &&
        left.Participant.AccountId == right.Participant.AccountId && left.Participant.ModuleProfileId == right.Participant.ModuleProfileId;
    private sealed class Connection(Guid subject, DateTimeOffset expires, IChatRealtimeClient client)
    {
        internal Guid Subject { get; } = subject;
        internal DateTimeOffset Expires { get; } = expires;
        internal IChatRealtimeClient Client { get; } = client;
        internal Dictionary<(ChatModule Module, Guid Conversation), Subscription> Subscriptions { get; } = [];
    }
    private sealed class Subscription(ChatConversationScope scope)
    {
        internal ChatConversationScope Scope { get; } = scope;
        internal HashSet<Guid> Delivered { get; } = [];
        internal Guid? Checkpoint { get; set; }
    }
    public void Dispose() => gate.Dispose();
}
