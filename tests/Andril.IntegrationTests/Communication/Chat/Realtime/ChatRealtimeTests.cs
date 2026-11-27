using Andril.Communication.Interfaces;
using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Chat.Development.Realtime;
using Xunit;
using static Andril.IntegrationTests.Communication.Chat.Realtime.ChatRealtimeFixture;

namespace Andril.IntegrationTests.Communication.Chat.Realtime;

public sealed class ChatRealtimeTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task IndependentConnectionsReceiveCanonicalEventsOnlyAfterD9Storage(ChatModule module)
    {
        using var f = await ChatRealtimeFixture.CreateAsync(module);
        var a = await f.ConnectAsync("a", f.A);
        var b = await f.ConnectAsync("b", f.B);
        var secondB = await f.ConnectAsync("b2", f.B);
        var c = await f.ConnectAsync("c", f.C, false);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("c", module, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b", (ChatModule)99, Conversation)).Status);
        var sent = await f.Sender().SendAsync(module, Conversation, Key(1), "private text");
        Assert.Equal(ChatConversationStatus.Success, sent.Storage.Status);
        Assert.Equal(ChatRealtimePublication.Attempted, sent.Publication);
        Assert.Equal(sent.Storage.Value!.MessageId, Assert.Single(b.Events).MessageId);
        Assert.Equal(Assert.Single(a.Events), Assert.Single(secondB.Events));
        Assert.Empty(c.Events);
        var history = await f.Realtime.ReadHistoryAsync("b", module, Conversation, 2);
        Assert.Equal(sent.Storage.Value.Text, Assert.Single(history.Value!.Items).Text);
        Assert.DoesNotContain("private text", b.Events.Single().ToString());
        Assert.DoesNotContain("private text", history.Value.ToString());
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.ReadHistoryAsync("c", module, Conversation, 2)).Status);
    }

    [Fact]
    public async Task HistoryPermissionDoesNotGrantRealtimeSubscription()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        f.B.SubscriptionAllowed = false;
        var b = await f.ConnectAsync("b", f.B, false);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b", ChatModule.Social, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Success, (await f.B.Messages.GetMessageHistoryAsync(ChatModule.Social, Conversation, 2)).Status);
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "stored");
        Assert.Empty(b.Events);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("invalid")]
    [InlineData("foreign")]
    public async Task FailedD9OperationsNeverPublish(string failure)
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        f.A.SendAllowed = failure != "denied";
        var sender = f.Sender(failure == "foreign" ? f.C : f.A);
        var result = await sender.SendAsync(ChatModule.Social, Conversation, Key(1), failure == "invalid" ? " " : "text");
        Assert.NotEqual(ChatConversationStatus.Success, result.Storage.Status);
        Assert.Equal(ChatRealtimePublication.NotAttempted, result.Publication);
        Assert.Empty(b.Events);
        Assert.Empty((await f.B.Messages.GetMessageHistoryAsync(ChatModule.Social, Conversation, 10)).Value!.Items);
    }

    [Fact]
    public async Task ConcurrentIdempotentSendsHaveOneLogicalMessageAndOneEventPerConnection()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        var sends = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(
            () => f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "retry"))));
        Assert.All(sends, value => Assert.Equal(ChatConversationStatus.Success, value.Storage.Status));
        Assert.Single(b.Events);
        Assert.Single((await f.B.Messages.GetMessageHistoryAsync(ChatModule.Social, Conversation, 10)).Value!.Items);
        var conflict = await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "different");
        Assert.Equal(ChatRealtimePublication.NotAttempted, conflict.Publication);
        Assert.Equal(ChatConversationStatus.Conflict, conflict.Storage.Status);
    }

    [Fact]
    public async Task LostDeliveryAcknowledgementRetriesStableIdentityAndClientDeduplicates()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        b.LoseAcknowledgement = true;
        var first = await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "stored");
        Assert.Equal(ChatConversationStatus.Success, first.Storage.Status);
        Assert.Equal(ChatRealtimePublication.Uncertain, first.Publication);
        b.LoseAcknowledgement = false;
        var retry = await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "stored");
        Assert.Equal(first.Storage.Value!.MessageId, retry.Storage.Value!.MessageId);
        Assert.Equal(2, b.Attempts);
        Assert.Single(b.Events);
    }

    [Fact]
    public async Task FailedPublisherPreservesStorageAndHistoryRecoversAfterReconnect()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        b.Fail = true;
        var sent = await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "missed");
        Assert.Equal(ChatRealtimePublication.Uncertain, sent.Publication);
        Assert.Equal(ChatConversationStatus.Success, sent.Storage.Status);
        await f.Realtime.DisconnectAsync("b");
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.ReadHistoryAsync("b", ChatModule.Social, Conversation, 2)).Status);
        await f.ConnectAsync("new-b", f.B, false);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.ReadHistoryAsync("new-b", ChatModule.Social, Conversation, 2)).Status);
        Assert.Equal(ChatConversationStatus.Success, (await f.Realtime.SubscribeAsync("new-b", ChatModule.Social, Conversation)).Status);
        var recovered = await f.Realtime.ReadHistoryAsync("new-b", ChatModule.Social, Conversation, 2);
        Assert.Equal(sent.Storage.Value!.MessageId, Assert.Single(recovered.Value!.Items).MessageId);
        var ack = await f.Realtime.AcknowledgeAsync("new-b", ChatModule.Social, Conversation, sent.Storage.Value.MessageId);
        Assert.Equal(sent.Storage.Value.MessageId, ack.Value!.LastReconciledMessageId);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("unmatch")]
    [InlineData("profile-deactivated")]
    [InlineData("session-revoked")]
    [InlineData("read-revoked")]
    [InlineData("profile-switched")]
    [InlineData("authority-failure")]
    [InlineData("expired")]
    public async Task RevokedSubscriptionsReceiveNoFutureMessageAndCannotReconnect(string reason)
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        switch (reason)
        {
            case "block": case "unmatch": f.B.SubscriptionAllowed = false; break;
            case "profile-deactivated": f.B.Active = false; break;
            case "session-revoked": f.B.SessionActive = false; break;
            case "read-revoked": f.B.ReadAllowed = false; break;
            case "profile-switched": f.B.Scope = new(f.B.Scope.VerifiedSubjectId,
                ChatParticipantReference.Create(ChatModule.Social, f.B.Scope.Participant.AccountId, Key(999))); break;
            case "authority-failure": f.Grants.Failure = new IOException("private"); break;
            case "expired": f.B.Expires = f.Time.Now; break;
        }
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "not for B");
        Assert.Empty(b.Events);
        Assert.Single(b.Revocations);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.ReadHistoryAsync("b", ChatModule.Social, Conversation, 2)).Status);
        if (reason != "expired")
        {
            await f.ConnectAsync("b-reconnect", f.B, false);
            Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b-reconnect", ChatModule.Social, Conversation)).Status);
        }
        else Assert.Equal(ChatConversationStatus.Unavailable, await f.Realtime.ConnectAsync("expired", f.B.Scope.VerifiedSubjectId, f.B.Expires, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationDuringDeliveryCheckStopsProtectedEvent(bool changeProfile)
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        var checks = 0;
        f.Grants.BeforeSubscriptionCheck = actor =>
        {
            if (actor != f.B || ++checks != 2) return;
            if (changeProfile) actor.Scope = new(actor.Scope.VerifiedSubjectId,
                ChatParticipantReference.Create(ChatModule.Social, actor.Scope.Participant.AccountId, Key(999)));
            else actor.SubscriptionAllowed = false;
        };
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "private");
        Assert.Empty(b.Events);
        Assert.Single(b.Revocations);
    }

    [Fact]
    public async Task UnsubscriptionAndExplicitRefreshRemoveRouting()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var first = await f.ConnectAsync("b", f.B);
        var second = await f.ConnectAsync("b2", f.B);
        Assert.Equal(ChatConversationStatus.Success, await f.Realtime.UnsubscribeAsync("b", ChatModule.Social, Conversation));
        f.B.SubscriptionAllowed = false;
        await f.Realtime.RevalidateAsync();
        Assert.Single(second.Revocations);
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "not routed");
        Assert.Empty(first.Events);
        Assert.Empty(second.Events);
    }

    [Fact]
    public async Task OutOfOrderEventsAreReconciledUsingD9SequenceNotTimestamps()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        var first = (await f.A.Messages.SendMessageAsync(ChatModule.Social, Conversation, Key(1), "first")).Value!;
        var second = (await f.A.Messages.SendMessageAsync(ChatModule.Social, Conversation, Key(2), "second")).Value!;
        await f.Realtime.PublishMessageCreatedAsync(second, default);
        await f.Realtime.PublishMessageCreatedAsync(first, default);
        Assert.Equal(new[] { second.MessageId, first.MessageId }, b.Events.Select(value => value.MessageId));
        var page = (await f.Realtime.ReadHistoryAsync("b", ChatModule.Social, Conversation, 1)).Value!;
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(3), "later");
        var next = (await f.Realtime.ReadHistoryAsync("b", ChatModule.Social, Conversation, 1, page.NextCursor)).Value!;
        Assert.Equal(second.MessageId, Assert.Single(page.Items).MessageId);
        Assert.Equal(first.MessageId, Assert.Single(next.Items).MessageId);
        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
    }

    [Fact]
    public async Task CancellationBeforeStorageAndDuringPublishHaveDifferentOutcomes()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "none", cancellationToken: before.Token));
        using var after = new CancellationTokenSource();
        var sent = await f.Sender(publisher: new CancelPublisher(after)).SendAsync(ChatModule.Social, Conversation, Key(1), "stored", cancellationToken: after.Token);
        Assert.Equal(ChatConversationStatus.Success, sent.Storage.Status);
        Assert.Equal(ChatRealtimePublication.Uncertain, sent.Publication);
        Assert.Single((await f.B.Messages.GetMessageHistoryAsync(ChatModule.Social, Conversation, 2)).Value!.Items);
    }
    [Fact]
    public async Task ActualStorageExceptionNeverPublishesOrClaimsSuccess()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        var messages = new ChatMessageManagementService(f.A.ConversationService, new FailingStorage());
        var result = await new ChatRealtimeMessageSender(messages, f.Realtime).SendAsync(ChatModule.Social, Conversation, Key(1), "private");
        Assert.Equal(ChatConversationStatus.Unavailable, result.Storage.Status);
        Assert.Equal(ChatRealtimePublication.NotAttempted, result.Publication);
        Assert.Empty(b.Events);
        Assert.Empty((await f.B.Messages.GetMessageHistoryAsync(ChatModule.Social, Conversation, 2)).Value!.Items);
    }
    private sealed class FailingStorage : IChatMessageRepository
    {
        public Task<ChatConversationResult<ChatMessage>> AppendAsync(ChatConversationScope scope, ChatConversation conversation,
            Guid requestId, ChatTextMessageDraft content, CancellationToken cancellationToken) => throw new IOException("private storage failure");
        public Task<ChatConversationResult<ChatMessageHistoryPage>> ReadHistoryAsync(ChatConversationScope scope,
            ChatConversation conversation, int pageSize, string? cursor, CancellationToken cancellationToken) => throw new IOException();
        public Task<ChatConversationResult<ChatMessage>> FindAsync(ChatConversationScope scope, ChatConversation conversation,
            Guid messageId, CancellationToken cancellationToken) => throw new IOException();
    }

    [Fact]
    public async Task RenewedSessionDoesNotExtendAnExistingConnectionAuthenticationExpiry()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        var b = await f.ConnectAsync("b", f.B);
        f.Time.Now = f.B.Expires;
        f.B.Expires = f.Time.Now.AddHours(1);
        await f.Sender().SendAsync(ChatModule.Social, Conversation, Key(1), "expired connection");
        Assert.Empty(b.Events);
        Assert.Single(b.Revocations);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b", ChatModule.Social, Conversation)).Status);
    }

    [Fact]
    public async Task DefaultRealtimePermissionIsNonGrantingEvenWithAResolvedD9Session()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        using var realtime = new DevelopmentChatRealtimeService(new ResolveOnly(f.B), f.Time);
        var client = new RecordingClient();
        await realtime.ConnectAsync("b", f.B.Scope.VerifiedSubjectId, f.B.Expires, client);
        Assert.Equal(ChatConversationStatus.Unavailable, (await realtime.SubscribeAsync("b", ChatModule.Social, Conversation)).Status);
        Assert.Empty(client.Events);
    }
    private sealed class ResolveOnly(Actor actor) : IChatRealtimeDevelopmentAuthority
    {
        public Task<ChatRealtimeSession?> ResolveAsync(Guid verifiedSubject, ChatModule module, CancellationToken cancellationToken) =>
            Task.FromResult<ChatRealtimeSession?>(new(actor.Scope, actor.Messages, actor.Expires));
    }
    private sealed class CancelPublisher(CancellationTokenSource source) : IChatRealtimeEventPublisher
    {
        public Task<bool> PublishMessageCreatedAsync(ChatMessage canonical, CancellationToken cancellationToken)
        { source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    }

    [Fact]
    public async Task MissingAuthorityInvalidScopesAndProductionDenyAllNeverGrantRealtime()
    {
        using var f = await ChatRealtimeFixture.CreateAsync();
        using var missing = new DevelopmentChatRealtimeService(timeProvider: f.Time);
        var client = new RecordingClient();
        Assert.Equal(ChatConversationStatus.Success, await missing.ConnectAsync("b", f.B.Scope.VerifiedSubjectId, f.B.Expires, client));
        Assert.Equal(ChatConversationStatus.Unavailable, (await missing.SubscribeAsync("b", ChatModule.Social, Conversation)).Status);
        var conversations = new ChatConversationManagementService(f.B, new DenyAllChatAccessAuthorizer(), f.Conversations, f.B);
        var messages = new ChatMessageManagementService(conversations, f.Storage);
        var production = new ChatRealtimeMessageSender(messages, f.Realtime);
        Assert.Equal(ChatRealtimePublication.NotAttempted, (await production.SendAsync(ChatModule.Social, Conversation, Key(1), "denied")).Publication);
        await f.ConnectAsync("b", f.B, false);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b", ChatModule.Learning, Conversation)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, (await f.Realtime.SubscribeAsync("b", ChatModule.Social, Guid.Empty)).Status);
        Assert.Equal(ChatConversationStatus.Unavailable, await f.Realtime.ConnectAsync("unbounded", f.B.Scope.VerifiedSubjectId, DateTimeOffset.MaxValue, client));
        Assert.Empty(client.Events);
    }
}
