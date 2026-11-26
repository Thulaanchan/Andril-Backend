using System.Collections.Concurrent;
using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Chat.Development.Realtime;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat.Realtime;

// Synthetic authorities are confined to this test assembly. Real D8/D9 repositories and services are used.
internal sealed class ChatRealtimeFixture : IDisposable
{
    internal static Guid Key(int value) => new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
    internal static readonly Guid Conversation = Key(400);
    internal Clock Time { get; } = new();
    internal DevelopmentChatConversationRepository Conversations { get; } = new();
    internal DevelopmentChatMessageRepository Storage { get; }
    internal Actor A { get; }
    internal Actor B { get; }
    internal Actor C { get; }
    internal Authority Grants { get; }
    internal DevelopmentChatRealtimeService Realtime { get; }
    private int nextMessage = 1000;

    private ChatRealtimeFixture(ChatModule module)
    {
        Storage = new(Time, () => Key(Interlocked.Increment(ref nextMessage)));
        A = new(Key(1), Key(101), Key(201), module, this);
        B = new(Key(2), Key(102), Key(202), module, this);
        C = new(Key(3), Key(103), Key(203), module, this);
        Grants = new([A, B, C]);
        Realtime = new(Grants, Time);
    }
    internal static async Task<ChatRealtimeFixture> CreateAsync(ChatModule module = ChatModule.Social)
    {
        var value = new ChatRealtimeFixture(module);
        var candidate = ChatConversation.Create(module, Conversation, [value.A.Scope.Participant, value.B.Scope.Participant]);
        Assert.Equal(ChatConversationStatus.Success, (await value.A.ConversationService.CreateAsync(module, Key(700), candidate)).Status);
        return value;
    }
    internal ChatRealtimeMessageSender Sender(Actor? actor = null, IChatRealtimeEventPublisher? publisher = null) =>
        new((actor ?? A).Messages, publisher ?? Realtime);
    internal async Task<RecordingClient> ConnectAsync(string id, Actor actor, bool subscribe = true)
    {
        var client = new RecordingClient();
        Assert.Equal(ChatConversationStatus.Success, await Realtime.ConnectAsync(id, actor.Scope.VerifiedSubjectId, actor.Expires, client));
        if (subscribe) Assert.Equal(ChatConversationStatus.Success,
            (await Realtime.SubscribeAsync(id, actor.Scope.Participant.Module, Conversation)).Status);
        return client;
    }
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class Actor : ICurrentUser, IChatAccessAuthorizer, IChatConversationDevelopmentAuthority
    {
        internal ChatConversationScope Scope { get; set; }
        internal bool Active { get; set; } = true;
        internal bool SessionActive { get; set; } = true;
        internal bool SubscriptionAllowed { get; set; } = true;
        internal bool ReadAllowed { get; set; } = true;
        internal bool SendAllowed { get; set; } = true;
        internal DateTimeOffset Expires { get; set; }
        internal ChatConversationManagementService ConversationService { get; }
        internal ChatMessageManagementService Messages { get; }
        public Guid? UserId => Scope.VerifiedSubjectId;
        public bool IsAuthenticated => SessionActive;
        internal Actor(Guid subject, Guid account, Guid profile, ChatModule module, ChatRealtimeFixture fixture)
        {
            Scope = new(subject, ChatParticipantReference.Create(module, account, profile));
            Expires = fixture.Time.Now.AddHours(1);
            ConversationService = new(this, this, fixture.Conversations, this);
            Messages = new(ConversationService, fixture.Storage);
        }
        public Task<ChatConversationScope?> ReadScopeAsync(Guid subject, ChatModule module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Active && SessionActive && subject == UserId && module == Scope.Participant.Module ? Scope : null);
        }
        public Task<ChatAuthorizationOutcome> AuthorizeCreationAsync(ChatConversationScope actor, ChatConversation candidate,
            Guid requestId, CancellationToken cancellationToken) => Task.FromResult(ChatAuthorizationOutcome.Allowed);
        public Task<ChatAuthorizationOutcome> AuthorizeAsync(ChatAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allowed = Active && SessionActive && (request.Operation == ChatOperation.SubmitMessage ? SendAllowed : ReadAllowed);
            return Task.FromResult(allowed ? ChatAuthorizationOutcome.Allowed : ChatAuthorizationOutcome.Denied);
        }
        public Task<ChatAuthorizedActor?> AuthorizeConversationAsync(ChatModule module, Guid conversationId,
            CancellationToken cancellationToken = default) => Task.FromResult<ChatAuthorizedActor?>(null);
    }
    internal sealed class Authority(Actor[] actors) : IChatRealtimeDevelopmentAuthority
    {
        internal Exception? Failure { get; set; }
        internal Action<Actor>? BeforeSubscriptionCheck { get; set; }
        public Task<ChatRealtimeSession?> ResolveAsync(Guid subject, ChatModule module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            var actor = actors.SingleOrDefault(value => value.UserId == subject && value.Scope.Participant.Module == module && value.Active && value.SessionActive);
            return Task.FromResult(actor is null ? null : new ChatRealtimeSession(actor.Scope, actor.Messages, actor.Expires));
        }
        public Task<bool> AuthorizeSubscriptionAsync(ChatRealtimeSession session, Guid conversationId, CancellationToken cancellationToken)
        {
            var actor = actors.Single(value => value.UserId == session.Scope.VerifiedSubjectId);
            BeforeSubscriptionCheck?.Invoke(actor);
            return Task.FromResult(actor.SubscriptionAllowed && actor.Active && actor.SessionActive && conversationId == Conversation);
        }
    }
    internal sealed class RecordingClient : IChatRealtimeClient
    {
        internal ConcurrentQueue<ChatMessageCreatedEvent> Events { get; } = new();
        internal ConcurrentQueue<ChatConversationAccessRevokedEvent> Revocations { get; } = new();
        internal bool Fail { get; set; }
        internal bool LoseAcknowledgement { get; set; }
        internal int Attempts { get; private set; }
        private readonly HashSet<Guid> seen = [];
        public Task MessageCreatedAsync(ChatMessageCreatedEvent value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Fail) throw new IOException("synthetic private failure");
            if (seen.Add(value.EventId)) Events.Enqueue(value);
            if (LoseAcknowledgement) throw new IOException("synthetic lost acknowledgement");
            return Task.CompletedTask;
        }
        public Task AccessRevokedAsync(ChatConversationAccessRevokedEvent value, CancellationToken cancellationToken)
        { Revocations.Enqueue(value); return Task.CompletedTask; }
    }
    public void Dispose() { Realtime.Dispose(); Storage.Dispose(); Conversations.Dispose(); }
}
