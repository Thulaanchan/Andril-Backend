using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Calls.Development;

internal enum CallType { Audio = 1, Video = 2 }
internal enum CallStatus { Initiated, Ringing, Accepted, Connecting, Active, Ended, Rejected, Cancelled, Missed, Failed, Disconnected }
internal enum CallOperation { Initiate, Receive, Inspect, Accept, Reject, Cancel, End, Credentials, Reconnect, Disconnect, Signal, Connect }
internal enum CallOutcome { Unavailable, Success, InvalidRequest, Conflict, ProviderUnavailable }
internal sealed record CallResult<T>(CallOutcome Outcome, T? Value = default)
{
    public override string ToString() => $"{nameof(CallResult<T>)}({Outcome})";
}
internal sealed record CallActorContext(ChatConversationScope Scope, ChatConversationManagementService Conversations, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => nameof(CallActorContext);
}
internal interface ICallDevelopmentAuthority
{
    Task<CallActorContext?> ResolveAsync(Guid verifiedSubject, ChatModule module, CancellationToken cancellationToken);
    Task<CallActorContext?> ResolvePeerAsync(ChatParticipantReference participant, CancellationToken cancellationToken);
    Task<bool> AuthorizeAsync(CallActorContext actor, CallOperation operation, Guid conversationId,
        CallSession? call, CancellationToken cancellationToken) => Task.FromResult(false);
}
internal sealed record CallParticipant(Guid Subject, ChatParticipantReference Profile)
{
    public override string ToString() => nameof(CallParticipant);
    internal bool Matches(ChatConversationScope scope) => Subject == scope.VerifiedSubjectId &&
        Profile.Module == scope.Participant.Module && Profile.AccountId == scope.Participant.AccountId && Profile.ModuleProfileId == scope.Participant.ModuleProfileId;
}
internal sealed record CallProviderReference(string Room, string CallerIdentity, string CalleeIdentity)
{
    public override string ToString() => nameof(CallProviderReference);
}
internal sealed record CallView(Guid CallId, ChatModule Module, Guid ConversationId, CallType Type,
    CallStatus Status, long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool CleanupPending, bool SignalingPending)
{
    public override string ToString() => $"{nameof(CallView)}({Status}, {Version})";
}
internal sealed record CallSession(Guid CallId, ChatModule Module, Guid ConversationId, CallType Type,
    CallParticipant Caller, CallParticipant Callee, CallStatus Status, long Version, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, CallProviderReference? Provider = null, bool CallerConnected = false,
    bool CalleeConnected = false, bool CleanupPending = false, bool SignalingPending = true, CallStatus? PreviousStatus = null)
{
    internal bool Terminal => Status is CallStatus.Ended or CallStatus.Rejected or CallStatus.Cancelled or CallStatus.Missed or CallStatus.Failed;
    internal CallView View => new(CallId, Module, ConversationId, Type, Status, Version, CreatedAtUtc, UpdatedAtUtc, CleanupPending, SignalingPending);
    internal CallSession Next(CallStatus next, DateTimeOffset now)
    {
        var allowed = Status switch
        {
            CallStatus.Initiated => next is CallStatus.Ringing or CallStatus.Cancelled or CallStatus.Failed,
            CallStatus.Ringing => next is CallStatus.Accepted or CallStatus.Rejected or CallStatus.Cancelled or CallStatus.Missed or CallStatus.Ended or CallStatus.Failed,
            CallStatus.Accepted => next is CallStatus.Connecting or CallStatus.Ended or CallStatus.Failed,
            CallStatus.Connecting => next is CallStatus.Active or CallStatus.Disconnected or CallStatus.Ended or CallStatus.Failed,
            CallStatus.Active => next is CallStatus.Disconnected or CallStatus.Ended or CallStatus.Failed,
            CallStatus.Disconnected => next is CallStatus.Connecting or CallStatus.Ended or CallStatus.Failed,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Invalid call transition.");
        return this with { PreviousStatus = Status, Status = next, Version = checked(Version + 1), UpdatedAtUtc = now < UpdatedAtUtc ? UpdatedAtUtc : now, SignalingPending = true };
    }
    public override string ToString() => $"{nameof(CallSession)}({Status}, {Version})";
}
internal sealed record CallStateTransition(Guid CallId, CallStatus Status, long Version);
internal sealed record CallDevelopmentOptions(TimeSpan TokenLifetime, TimeSpan? RingTimeout = null);
internal sealed record CallCredential(Uri Endpoint, string Room, string Identity, string Token, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => nameof(CallCredential);
}
internal sealed record CallProviderState(bool RoomExists, IReadOnlySet<string> ConnectedIdentities);
internal interface ICallMediaProvider
{
    Task CreateOrGetRoomAsync(CallProviderReference reference, CancellationToken cancellationToken);
    Task<CallCredential> IssueCredentialAsync(CallProviderReference reference, string identity, CallType type,
        TimeSpan lifetime, CancellationToken cancellationToken);
    Task RemoveParticipantAsync(CallProviderReference reference, string identity, CancellationToken cancellationToken);
    Task CloseRoomAsync(CallProviderReference reference, CancellationToken cancellationToken);
    Task<CallProviderState> InspectAsync(CallProviderReference reference, CancellationToken cancellationToken);
}
internal enum CallProviderEventKind { RoomStarted, ParticipantJoined, ParticipantLeft, RoomEnded }
internal sealed record VerifiedCallProviderEvent(string EventId, string Fingerprint, CallProviderEventKind Kind,
    string Room, string? Identity, DateTimeOffset CreatedAtUtc);
internal interface ICallWebhookVerifier
{
    Task<VerifiedCallProviderEvent?> VerifyAsync(ReadOnlyMemory<byte> rawBody, string authorization, CancellationToken cancellationToken);
}
internal enum CallSignalKind { IncomingCall, CallRinging, CallAccepted, CallConnecting, CallActive, CallRejected,
    CallCancelled, CallEnded, CallFailed, CallDisconnected, CallMissed }
internal sealed record CallSignal(int Version, string EventId, Guid CallId, ChatModule Module, Guid ConversationId,
    CallStatus Status, long StateVersion, CallSignalKind Kind, DateTimeOffset CreatedAtUtc)
{
    internal static CallSignal Create(CallSession call, CallSignalKind kind) => new(1,
        $"{call.CallId:N}/{call.Version}/{kind}", call.CallId, call.Module, call.ConversationId, call.Status, call.Version, kind, call.UpdatedAtUtc);
    public override string ToString() => $"{nameof(CallSignal)}({Kind}, {StateVersion})";
}
internal interface ICallSignalPublisher { Task<bool> PublishAsync(CallSession call, CancellationToken cancellationToken); }
internal interface ICallSignalClient { Task ReceiveAsync(CallSignal signal, CancellationToken cancellationToken); }
