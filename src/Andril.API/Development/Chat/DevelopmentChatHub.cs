using Andril.Communication.Calls.Development;
using Andril.Communication.Interfaces;
using System.Globalization;
using System.Runtime.CompilerServices;
using Andril.API.Authentication;
using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Chat.Development.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

[assembly: InternalsVisibleTo("Andril.IntegrationTests")]

namespace Andril.API.Development.Chat;

/// <summary>Unmapped SignalR candidate. Only the isolated integration host registers/maps this hub.</summary>
[Authorize]
internal sealed partial class DevelopmentChatHub(DevelopmentChatRealtimeService realtime, IHubContext<DevelopmentChatHub> hub, IHostEnvironment environment, DevelopmentCallManagementService? calls = null, DevelopmentCallSignalRouter? callSignals = null) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (environment.EnvironmentName != "ChatRealtimeDevelopment") { Context.Abort(); return; }
        var subject = VerifiedUserSubject.GetId(Context.User);
        var expirations = Context.User?.FindAll("exp").ToArray();
        if (subject is null || expirations?.Length != 1 || !long.TryParse(expirations[0].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        { Context.Abort(); return; }
        DateTimeOffset expires;
        try { expires = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { Context.Abort(); return; }
        if (await realtime.ConnectAsync(Context.ConnectionId, subject.Value, expires,
            new Client(hub, Context.ConnectionId), Context.ConnectionAborted) != ChatConversationStatus.Success)
        { Context.Abort(); return; }
        if (callSignals is not null) await callSignals.ConnectAsync(Context.ConnectionId, subject.Value, expires,
            new CallClient(hub, Context.ConnectionId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public Task<ChatConversationResult<ChatRealtimeCheckpoint>> SubscribeConversation(ChatModule module, Guid conversationId) =>
        realtime.SubscribeAsync(Context.ConnectionId, module, conversationId, Context.ConnectionAborted);
    public Task<ChatConversationStatus> UnsubscribeConversation(ChatModule module, Guid conversationId) =>
        realtime.UnsubscribeAsync(Context.ConnectionId, module, conversationId, Context.ConnectionAborted);
    public Task<ChatConversationResult<ChatRealtimeHistoryPage>> ReadHistory(ChatModule module, Guid conversationId,
        int pageSize, string? cursor) => realtime.ReadHistoryAsync(Context.ConnectionId, module, conversationId, pageSize, cursor, Context.ConnectionAborted);
    public Task<ChatConversationResult<ChatRealtimeCheckpoint>> Acknowledge(ChatModule module, Guid conversationId, Guid messageId) =>
        realtime.AcknowledgeAsync(Context.ConnectionId, module, conversationId, messageId, Context.ConnectionAborted);
    public Task RefreshSubscriptions() => realtime.RevalidateAsync(Context.ConnectionAborted);
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await realtime.DisconnectAsync(Context.ConnectionId);
        if (callSignals is not null) await callSignals.DisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private sealed class Client(IHubContext<DevelopmentChatHub> hub, string connectionId) : IChatRealtimeClient
    {
        public Task MessageCreatedAsync(ChatMessageCreatedEvent value, CancellationToken cancellationToken) =>
            hub.Clients.Client(connectionId).SendAsync("ConversationMessageCreated", value, cancellationToken);
        public Task AccessRevokedAsync(ChatConversationAccessRevokedEvent value, CancellationToken cancellationToken) =>
            hub.Clients.Client(connectionId).SendAsync("ConversationAccessRevoked", value, cancellationToken);
    }
}
