using Andril.Communication.Chat.Development;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Calls.Development;

/// <summary>Call signaling over the D10 connection adapter. Routing is not authorization; no production grants.</summary>
internal sealed class DevelopmentCallSignalRouter(DevelopmentCallManagementService calls, TimeProvider? timeProvider = null) : ICallSignalPublisher, IDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    internal async Task ConnectAsync(string id, Guid subject, DateTimeOffset expires, ICallSignalClient client, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { if (subject != Guid.Empty && expires > clock.GetUtcNow() && expires != DateTimeOffset.MaxValue && !connections.ContainsKey(id)) connections.Add(id, new(subject, expires, client)); }
        finally { gate.Release(); }
    }
    internal async Task DisconnectAsync(string id)
    {
        await gate.WaitAsync(); try { connections.Remove(id); } finally { gate.Release(); }
    }
    internal async Task<CallOutcome> SubscribeAsync(string id, ChatModule module, Guid conversation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!connections.TryGetValue(id, out var connection) || connection.Expires <= clock.GetUtcNow()) return CallOutcome.Unavailable;
            var actor = await calls.AuthorizeSignalConversationAsync(connection.Subject, module, conversation, token);
            if (actor is null) { connection.Subscriptions.Remove((module, conversation)); return CallOutcome.Unavailable; }
            connection.Subscriptions[(module, conversation)] = actor.Scope;
            return CallOutcome.Success;
        }
        finally { gate.Release(); }
    }
    internal async Task<CallOutcome> UnsubscribeAsync(string id, ChatModule module, Guid conversation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { if (!connections.TryGetValue(id, out var connection)) return CallOutcome.Unavailable; connection.Subscriptions.Remove((module, conversation)); return CallOutcome.Success; }
        finally { gate.Release(); }
    }
    public async Task<bool> PublishAsync(CallSession call, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var completed = true;
            foreach (var connection in connections.Values)
            {
                var key = (call.Module, call.ConversationId);
                if (!connection.Subscriptions.TryGetValue(key, out var scope)) continue;
                var current = connection.Expires > clock.GetUtcNow() ? await calls.ReadForSignalAsync(connection.Subject, call.Module, call.CallId, cancellationToken) : null;
                if (current is null || !new CallParticipant(scope.VerifiedSubjectId, scope.Participant).Matches(current.Value.Actor.Scope))
                { connection.Subscriptions.Remove(key); continue; }
                // A delayed/replayed signal must not advertise an obsolete state after a concurrent terminal transition.
                if (current.Value.Call.Version != call.Version || current.Value.Call.Status != call.Status) continue;
                var kinds = call.Status switch
                {
                    CallStatus.Ringing => connection.Subject == call.Callee.Subject ? new[] { CallSignalKind.IncomingCall, CallSignalKind.CallRinging } : [CallSignalKind.CallRinging],
                    CallStatus.Connecting => call.PreviousStatus == CallStatus.Accepted ? new[] { CallSignalKind.CallAccepted, CallSignalKind.CallConnecting } : [CallSignalKind.CallConnecting],
                    CallStatus.Active => [CallSignalKind.CallActive],
                    CallStatus.Rejected => [CallSignalKind.CallRejected],
                    CallStatus.Cancelled => [CallSignalKind.CallCancelled],
                    CallStatus.Ended => [CallSignalKind.CallEnded],
                    CallStatus.Failed => [CallSignalKind.CallFailed],
                    CallStatus.Missed => [CallSignalKind.CallMissed],
                    CallStatus.Disconnected => [CallSignalKind.CallDisconnected],
                    _ => Array.Empty<CallSignalKind>()
                };
                foreach (var kind in kinds)
                {
                    current = connection.Expires > clock.GetUtcNow() ? await calls.ReadForSignalAsync(connection.Subject, call.Module, call.CallId, cancellationToken) : null;
                    if (current is null || !new CallParticipant(scope.VerifiedSubjectId, scope.Participant).Matches(current.Value.Actor.Scope))
                    { connection.Subscriptions.Remove(key); break; }
                    if (current.Value.Call.Version != call.Version || current.Value.Call.Status != call.Status) break;
                    var signal = CallSignal.Create(call, kind);
                    if (connection.Delivered.Contains(signal.EventId)) continue;
                    try { await connection.Client.ReceiveAsync(signal, cancellationToken); connection.Delivered.Add(signal.EventId); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { completed = false; }
                }
            }
            return completed;
        }
        finally { gate.Release(); }
    }
    private sealed class Connection(Guid subject, DateTimeOffset expires, ICallSignalClient client)
    {
        internal Guid Subject { get; } = subject;
        internal DateTimeOffset Expires { get; } = expires;
        internal ICallSignalClient Client { get; } = client;
        internal Dictionary<(ChatModule, Guid), ChatConversationScope> Subscriptions { get; } = [];
        internal HashSet<string> Delivered { get; } = new(StringComparer.Ordinal);
    }
    public void Dispose() => gate.Dispose();
}
