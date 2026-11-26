using System.Collections.Concurrent;
using Andril.Communication.Calls.Development;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.IntegrationTests.Communication.Chat.Realtime;
using Xunit;
using static Andril.IntegrationTests.Communication.Chat.Realtime.ChatRealtimeFixture;

namespace Andril.IntegrationTests.Communication.Calls;

internal sealed class CallDevelopmentFixture : IDisposable
{
    internal ChatRealtimeFixture Chat { get; }
    internal DevelopmentCallRepository Repository { get; } = new();
    internal FakeProvider Provider { get; } = new();
    internal Authority Grants { get; }
    internal DevelopmentCallManagementService Calls { get; }
    internal DevelopmentCallSignalRouter Signals { get; }
    internal Guid A => Chat.A.Scope.VerifiedSubjectId;
    internal Guid B => Chat.B.Scope.VerifiedSubjectId;
    internal Guid C => Chat.C.Scope.VerifiedSubjectId;
    internal ChatModule Module => Chat.A.Scope.Participant.Module;
    private int next = 5000;
    private CallDevelopmentFixture(ChatRealtimeFixture chat, TimeSpan? ringTimeout)
    {
        Chat = chat; Grants = new(chat); Provider.Time = chat.Time;
        Calls = new(Repository, Grants, Provider, new(TimeSpan.FromMinutes(5), ringTimeout), Chat.Time, () => Key(Interlocked.Increment(ref next)));
        Signals = new(Calls, Chat.Time); Calls.Signals = Signals;
    }
    internal static async Task<CallDevelopmentFixture> CreateAsync(ChatModule module = ChatModule.Social, TimeSpan? ringTimeout = null) =>
        new(await ChatRealtimeFixture.CreateAsync(module), ringTimeout);
    internal async Task<Guid> RingAsync(CallType type = CallType.Audio)
    {
        var result = await Calls.InitiateCallAsync(A, Module, Conversation, Key(900), type);
        Assert.Equal(CallOutcome.Success, result.Outcome); return result.Value!.CallId;
    }
    internal async Task<Guid> ConnectingAsync(CallType type = CallType.Audio)
    {
        var id = await RingAsync(type);
        Assert.Equal(CallStatus.Connecting, (await Calls.AcceptCallAsync(B, Module, id)).Value!.Status); return id;
    }
    internal async Task<RecordingSink> ListenAsync(string connection, Guid subject)
    {
        var sink = new RecordingSink();
        await Signals.ConnectAsync(connection, subject, Chat.Time.Now.AddHours(1), sink, default);
        Assert.Equal(CallOutcome.Success, await Signals.SubscribeAsync(connection, Module, Conversation, default));
        return sink;
    }
    internal async Task JoinAsync(Guid id, bool caller, string eventId, DateTimeOffset? stamp = null)
    {
        var call = Repository.Calls[id]; var identity = caller ? call.Provider!.CallerIdentity : call.Provider!.CalleeIdentity;
        Provider.Joined.Add(identity);
        var value = new VerifiedCallProviderEvent(eventId, eventId, CallProviderEventKind.ParticipantJoined, call.Provider.Room, identity, stamp ?? Chat.Time.Now);
        Assert.Equal(CallOutcome.Success, await Calls.HandleWebhookAsync(new byte[] { 1 }, "synthetic", new VerifiedFixture(value)));
    }
    internal sealed class VerifiedFixture(VerifiedCallProviderEvent? value) : ICallWebhookVerifier
    {
        public Task<VerifiedCallProviderEvent?> VerifyAsync(ReadOnlyMemory<byte> rawBody, string authorization, CancellationToken cancellationToken) => Task.FromResult(value);
    }
    internal sealed class Authority(ChatRealtimeFixture fixture) : ICallDevelopmentAuthority
    {
        internal HashSet<Guid> Revoked { get; } = [];
        internal HashSet<CallOperation> Denied { get; } = [];
        internal Exception? Failure { get; set; }
        internal Action<CallOperation>? OnCheck { get; set; }
        private Actor[] Actors => [fixture.A, fixture.B, fixture.C];
        public Task<CallActorContext?> ResolveAsync(Guid verifiedSubject, ChatModule module, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure;
            var actor = Actors.SingleOrDefault(value => value.UserId == verifiedSubject && value.Scope.Participant.Module == module && value.Active && value.SessionActive);
            return Task.FromResult(actor is null ? null : new CallActorContext(actor.Scope, actor.ConversationService, actor.Expires));
        }
        public Task<CallActorContext?> ResolvePeerAsync(ChatParticipantReference participant, CancellationToken cancellationToken)
        {
            var actor = Actors.SingleOrDefault(value => value.Scope.Participant.Module == participant.Module && value.Scope.Participant.AccountId == participant.AccountId && value.Scope.Participant.ModuleProfileId == participant.ModuleProfileId);
            return actor is null ? Task.FromResult<CallActorContext?>(null) : ResolveAsync(actor.Scope.VerifiedSubjectId, participant.Module, cancellationToken);
        }
        public Task<bool> AuthorizeAsync(CallActorContext actor, CallOperation operation, Guid conversationId, CallSession? call, CancellationToken cancellationToken)
        {
            OnCheck?.Invoke(operation);
            // Explicit synthetic permission; no Social rule is inferred from Learning/Hobbies access.
            return Task.FromResult(!Revoked.Contains(actor.Scope.VerifiedSubjectId) && !Denied.Contains(operation) && conversationId == Conversation);
        }
    }
    internal sealed class FakeProvider : ICallMediaProvider
    {
        internal HashSet<string> Rooms { get; } = [];
        internal HashSet<string> Joined { get; } = [];
        internal int Creates { get; private set; }
        internal int Credentials { get; private set; }
        internal int Closes { get; private set; }
        internal bool FailCreate { get; set; }
        internal bool FailCleanup { get; set; }
        internal bool LoseCreateAcknowledgement { get; set; }
        internal Action? AfterCreate { get; set; }
        internal Action? AfterCredential { get; set; }
        internal TimeProvider Time { get; set; } = TimeProvider.System;
        public Task CreateOrGetRoomAsync(CallProviderReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailCreate) throw new IOException("private provider configuration");
            if (Rooms.Add(reference.Room)) Creates++;
            AfterCreate?.Invoke();
            if (LoseCreateAcknowledgement) throw new IOException("uncertain provider acknowledgement");
            return Task.CompletedTask;
        }
        public Task<CallCredential> IssueCredentialAsync(CallProviderReference reference, string identity, CallType type, TimeSpan lifetime, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (!Rooms.Contains(reference.Room)) throw new IOException();
            Credentials++; AfterCredential?.Invoke();
            return Task.FromResult(new CallCredential(new Uri("wss://synthetic.invalid"), reference.Room, identity, "synthetic-not-a-livekit-token-" + identity, Time.GetUtcNow().Add(lifetime)));
        }
        public Task RemoveParticipantAsync(CallProviderReference reference, string identity, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailCleanup) throw new IOException(); Joined.Remove(identity); return Task.CompletedTask; }
        public Task CloseRoomAsync(CallProviderReference reference, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailCleanup) throw new IOException(); Rooms.Remove(reference.Room); Closes++; return Task.CompletedTask; }
        public Task<CallProviderState> InspectAsync(CallProviderReference reference, CancellationToken token) => Task.FromResult(new CallProviderState(Rooms.Contains(reference.Room), new HashSet<string>(Joined)));
    }
    internal sealed class RecordingSink : ICallSignalClient
    {
        internal ConcurrentQueue<CallSignal> Events { get; } = new();
        internal bool Fail { get; set; }
        internal Action? AfterSignal { get; set; }
        public Task ReceiveAsync(CallSignal signal, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (Fail) throw new IOException("private"); Events.Enqueue(signal); AfterSignal?.Invoke(); return Task.CompletedTask; }
    }
    public void Dispose() { Signals.Dispose(); Repository.Dispose(); Chat.Dispose(); }
}
