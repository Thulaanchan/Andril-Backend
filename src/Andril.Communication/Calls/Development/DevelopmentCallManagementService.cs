using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Calls.Development;

/// <summary>Isolated one-to-one calling state machine. All positive authorities/providers must be explicitly supplied.
/// Local serialization is not distributed provider/revocation atomicity; no production registration exists.</summary>
internal sealed class DevelopmentCallManagementService(DevelopmentCallRepository repository,
    ICallDevelopmentAuthority? authority = null, ICallMediaProvider? provider = null,
    CallDevelopmentOptions? options = null, TimeProvider? timeProvider = null, Func<Guid>? newId = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<Guid> id = newId ?? Guid.NewGuid;
    internal ICallSignalPublisher? Signals { get; set; }
    private DateTimeOffset Now => clock.GetUtcNow();
    private bool Future(DateTimeOffset time) => time > Now && time != DateTimeOffset.MaxValue;
    internal bool AuthenticationCurrent(DateTimeOffset expires) => Future(expires);
    private static bool Same(ChatParticipantReference a, ChatParticipantReference b) => a.Module == b.Module && a.AccountId == b.AccountId && a.ModuleProfileId == b.ModuleProfileId;

    internal async Task<CallResult<CallView>> InitiateCallAsync(Guid subject, ChatModule module, Guid conversationId,
        Guid requestId, CallType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(type) || !Enum.IsDefined(module) || conversationId == Guid.Empty || requestId == Guid.Empty)
            return new(CallOutcome.InvalidRequest);
        CallSession? saved = null;
        await repository.Gate.WaitAsync(cancellationToken);
        try
        {
            var actor = await ActorAsync(subject, module, CallOperation.Initiate, conversationId, null, cancellationToken);
            if (actor is null) return new(CallOutcome.Unavailable);
            var details = await actor.Conversations.GetDetailsAsync(module, conversationId, cancellationToken);
            if (details.Status != ChatConversationStatus.Success || details.Value?.Participants.Count != 2) return new(CallOutcome.Unavailable);
            var peerReference = details.Value.Participants.SingleOrDefault(value => !Same(value, actor.Scope.Participant));
            var peer = peerReference is null || authority is null ? null : await authority.ResolvePeerAsync(peerReference, cancellationToken);
            if (peer is null || !Same(peer.Scope.Participant, peerReference!) || !Future(peer.ExpiresAtUtc) || peer.Scope.VerifiedSubjectId == subject ||
                await ActorAsync(subject, module, CallOperation.Initiate, conversationId, null, cancellationToken) is not { } current ||
                !new CallParticipant(subject, actor.Scope.Participant).Matches(current.Scope)) return new(CallOutcome.Unavailable);
            var receiving = await ActorAsync(peer.Scope.VerifiedSubjectId, module, CallOperation.Receive, conversationId, null, cancellationToken);
            if (receiving is null || !new CallParticipant(peer.Scope.VerifiedSubjectId, peer.Scope.Participant).Matches(receiving.Scope)) return new(CallOutcome.Unavailable);
            var finalCaller = await ActorAsync(subject, module, CallOperation.Initiate, conversationId, null, cancellationToken);
            if (finalCaller is null || !new CallParticipant(subject, actor.Scope.Participant).Matches(finalCaller.Scope) || !Future(receiving.ExpiresAtUtc))
                return new(CallOutcome.Unavailable);
            var participant = actor.Scope.Participant;
            var key = (subject, module, participant.AccountId, participant.ModuleProfileId, conversationId, requestId);
            if (repository.Requests.TryGetValue(key, out var existing))
            {
                saved = repository.Calls[existing];
                if (saved.Type != type || !saved.Callee.Matches(peer.Scope)) return new(CallOutcome.Conflict);
            }
            else
            {
                var callId = id(); var now = Now;
                if (callId == Guid.Empty || repository.Calls.ContainsKey(callId) || now == DateTimeOffset.MaxValue) return new(CallOutcome.Unavailable);
                cancellationToken.ThrowIfCancellationRequested();
                saved = new(callId, module, conversationId, type, new(subject, participant), new(peer.Scope.VerifiedSubjectId, peer.Scope.Participant),
                    CallStatus.Initiated, 1, now, now);
                repository.Save(saved);
                saved = saved.Next(CallStatus.Ringing, now); repository.Save(saved);
                repository.Requests.Add(key, callId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CallOutcome.Unavailable); }
        finally { repository.Gate.Release(); }
        return new(CallOutcome.Success, (await PublishAsync(saved!, cancellationToken)).View);
    }

    internal Task<CallResult<CallView>> GetCallSessionAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        RunAsync(subject, module, callId, CallOperation.Inspect, (actor, call, ct) => Task.FromResult((CallOutcome.Success, call)), false, token);

    internal Task<CallResult<CallView>> AcceptCallAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        RunAsync(subject, module, callId, CallOperation.Accept, async (actor, call, ct) =>
        {
            if (!call.Callee.Matches(actor.Scope)) return (CallOutcome.Unavailable, call);
            if (call.Status is CallStatus.Connecting or CallStatus.Active) return (CallOutcome.Success, call);
            if (call.Status != CallStatus.Ringing) return (CallOutcome.Conflict, call);
            if (provider is null) return (CallOutcome.ProviderUnavailable, call);
            if (await ActorAsync(subject, module, CallOperation.Connect, call.ConversationId, call, ct) is null) return (CallOutcome.Unavailable, call);
            if (await ActorAsync(call.Caller.Subject, module, CallOperation.Connect, call.ConversationId, call, ct) is null)
                return (CallOutcome.Unavailable, call);
            var reference = new CallProviderReference("cr_" + id().ToString("N"), "cp_" + id().ToString("N"), "cp_" + id().ToString("N"));
            if (reference.CallerIdentity == reference.CalleeIdentity || reference.Room.EndsWith(Guid.Empty.ToString("N"), StringComparison.Ordinal) ||
                reference.CallerIdentity.EndsWith(Guid.Empty.ToString("N"), StringComparison.Ordinal) || reference.CalleeIdentity.EndsWith(Guid.Empty.ToString("N"), StringComparison.Ordinal) ||
                repository.Calls.Values.Any(value => value.Provider?.Room == reference.Room)) return (CallOutcome.ProviderUnavailable, call);
            call = call.Next(CallStatus.Accepted, Now) with { Provider = reference, CleanupPending = true }; repository.Save(call);
            try
            {
                await provider.CreateOrGetRoomAsync(reference, ct);
                if (await ActorAsync(subject, module, CallOperation.Accept, call.ConversationId, call, ct) is null ||
                    await ActorAsync(subject, module, CallOperation.Connect, call.ConversationId, call, ct) is null ||
                    await ActorAsync(call.Caller.Subject, module, CallOperation.Connect, call.ConversationId, call, ct) is null)
                    throw new InvalidOperationException("Call authority changed.");
                call = call.Next(CallStatus.Connecting, Now) with { CleanupPending = false }; repository.Save(call);
                return (CallOutcome.Success, call);
            }
            catch (Exception)
            {
                // Provider side effects may exist even when acknowledgement/cancellation is lost.
                call = call.Next(CallStatus.Failed, Now) with { CleanupPending = true }; repository.Save(call);
                call = await CleanupAsync(call, CancellationToken.None);
                return (CallOutcome.ProviderUnavailable, call);
            }
        }, true, token);

    internal Task<CallResult<CallView>> RejectCallAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        TerminalCommandAsync(subject, module, callId, CallOperation.Reject, CallStatus.Rejected, token);
    internal Task<CallResult<CallView>> CancelCallAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        TerminalCommandAsync(subject, module, callId, CallOperation.Cancel, CallStatus.Cancelled, token);
    internal Task<CallResult<CallView>> EndCallAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        TerminalCommandAsync(subject, module, callId, CallOperation.End, CallStatus.Ended, token);
    private Task<CallResult<CallView>> TerminalCommandAsync(Guid subject, ChatModule module, Guid callId, CallOperation operation,
        CallStatus status, CancellationToken token) => RunAsync(subject, module, callId, operation, async (actor, call, ct) =>
        {
            if (operation == CallOperation.Reject && !call.Callee.Matches(actor.Scope) || operation == CallOperation.Cancel && !call.Caller.Matches(actor.Scope))
                return (CallOutcome.Unavailable, call);
            if (call.Status == status) return (CallOutcome.Success, call);
            if (call.Terminal || operation != CallOperation.End && call.Status != CallStatus.Ringing) return (CallOutcome.Conflict, call);
            call = call.Next(status, Now) with { CleanupPending = call.Provider is not null }; repository.Save(call);
            return (CallOutcome.Success, await CleanupAsync(call, ct));
        }, true, token);

    internal Task<CallResult<CallView>> DisconnectAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        RunAsync(subject, module, callId, CallOperation.Disconnect, (actor, call, ct) =>
        {
            if (call.Status == CallStatus.Disconnected) return Task.FromResult((CallOutcome.Success, call));
            if (call.Status is not (CallStatus.Connecting or CallStatus.Active)) return Task.FromResult((CallOutcome.Conflict, call));
            call = call.Next(CallStatus.Disconnected, Now) with { CallerConnected = call.Caller.Matches(actor.Scope) ? false : call.CallerConnected,
                CalleeConnected = call.Callee.Matches(actor.Scope) ? false : call.CalleeConnected };
            repository.Save(call); return Task.FromResult((CallOutcome.Success, call));
        }, true, token);
    internal Task<CallResult<CallView>> ReconnectAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default) =>
        RunAsync(subject, module, callId, CallOperation.Reconnect, (actor, call, ct) =>
        {
            if (call.Status != CallStatus.Disconnected) return Task.FromResult((CallOutcome.Conflict, call));
            call = call.Next(CallStatus.Connecting, Now); repository.Save(call); return Task.FromResult((CallOutcome.Success, call));
        }, true, token);

    internal async Task<CallResult<CallCredential>> GetCredentialAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token = default)
    {
        await repository.Gate.WaitAsync(token);
        try
        {
            if (!repository.Calls.TryGetValue(callId, out var call) || call.Module != module || call.Provider is null ||
                call.Status is not (CallStatus.Connecting or CallStatus.Active) || provider is null || options is null ||
                options.TokenLifetime <= TimeSpan.Zero || options.TokenLifetime == TimeSpan.MaxValue) return new(CallOutcome.Unavailable);
            var actor = await ActorAsync(subject, module, CallOperation.Credentials, call.ConversationId, call, token);
            if (actor is null || await ActorAsync(subject, module, CallOperation.Connect, call.ConversationId, call, token) is null) return new(CallOutcome.Unavailable);
            var peer = await ActorAsync(call.Caller.Matches(actor.Scope) ? call.Callee.Subject : call.Caller.Subject, module, CallOperation.Connect, call.ConversationId, call, token);
            if (peer is null) return new(CallOutcome.Unavailable);
            var lifetime = new[] { options.TokenLifetime, actor.ExpiresAtUtc - Now, peer.ExpiresAtUtc - Now }.Min();
            if (lifetime < TimeSpan.FromSeconds(1)) return new(CallOutcome.Unavailable);
            var identity = call.Caller.Matches(actor.Scope) ? call.Provider.CallerIdentity : call.Provider.CalleeIdentity;
            var credential = await provider.IssueCredentialAsync(call.Provider, identity, call.Type, lifetime, token);
            var currentActor = await ActorAsync(subject, module, CallOperation.Credentials, call.ConversationId, call, token);
            var currentPeer = await ActorAsync(peer.Scope.VerifiedSubjectId, module, CallOperation.Connect, call.ConversationId, call, token);
            var currentConnect = await ActorAsync(subject, module, CallOperation.Connect, call.ConversationId, call, token);
            if (credential.Room != call.Provider.Room || credential.Identity != identity || !Future(credential.ExpiresAtUtc) ||
                credential.ExpiresAtUtc > Now.Add(lifetime) || string.IsNullOrWhiteSpace(credential.Token) ||
                currentActor is null || currentPeer is null || currentConnect is null ||
                credential.ExpiresAtUtc > currentActor.ExpiresAtUtc || credential.ExpiresAtUtc > currentPeer.ExpiresAtUtc || credential.ExpiresAtUtc > currentConnect.ExpiresAtUtc)
                return new(CallOutcome.Unavailable);
            return new(CallOutcome.Success, credential);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CallOutcome.ProviderUnavailable); }
        finally { repository.Gate.Release(); }
    }

    internal async Task<CallActorContext?> AuthorizeSignalConversationAsync(Guid subject, ChatModule module, Guid conversationId, CancellationToken token) =>
        await ActorAsync(subject, module, CallOperation.Signal, conversationId, null, token);
    internal async Task<(CallActorContext Actor, CallSession Call)?> ReadForSignalAsync(Guid subject, ChatModule module, Guid callId, CancellationToken token)
    {
        await repository.Gate.WaitAsync(token);
        try
        {
            if (!repository.Calls.TryGetValue(callId, out var call) || call.Module != module) return null;
            var actor = await ActorAsync(subject, module, CallOperation.Signal, call.ConversationId, call, token);
            return actor is null ? null : (actor, call);
        }
        finally { repository.Gate.Release(); }
    }

    private async Task<CallResult<CallView>> RunAsync(Guid subject, ChatModule module, Guid callId, CallOperation operation,
        Func<CallActorContext, CallSession, CancellationToken, Task<(CallOutcome Outcome, CallSession Call)>> action, bool publish, CancellationToken token)
    {
        CallSession? saved = null; CallOutcome outcome;
        await repository.Gate.WaitAsync(token);
        try
        {
            if (!repository.Calls.TryGetValue(callId, out var call) || call.Module != module) return new(CallOutcome.Unavailable);
            var actor = await ActorAsync(subject, module, operation, call.ConversationId, call, token);
            if (actor is null) return new(CallOutcome.Unavailable);
            token.ThrowIfCancellationRequested();
            (outcome, saved) = await action(actor, call, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(CallOutcome.Unavailable); }
        finally { repository.Gate.Release(); }
        if (saved is not null && publish && outcome is CallOutcome.Success or CallOutcome.ProviderUnavailable)
            saved = await PublishAsync(saved, token);
        return new(outcome, outcome is CallOutcome.Success or CallOutcome.ProviderUnavailable ? saved?.View : null);
    }
    private async Task<CallActorContext?> ActorAsync(Guid subject, ChatModule module, CallOperation operation,
        Guid conversationId, CallSession? call, CancellationToken token)
    {
        try
        {
            if (authority is null || subject == Guid.Empty || !Enum.IsDefined(module) || conversationId == Guid.Empty) return null;
            var actor = await authority.ResolveAsync(subject, module, token);
            if (actor is null || actor.Scope.VerifiedSubjectId != subject || actor.Scope.Participant.Module != module || !Future(actor.ExpiresAtUtc) ||
                call is not null && !call.Caller.Matches(actor.Scope) && !call.Callee.Matches(actor.Scope)) return null;
            if (!await authority.AuthorizeAsync(actor, operation, conversationId, call, token)) return null;
            var details = await actor.Conversations.GetDetailsAsync(module, conversationId, token);
            if (details.Status != ChatConversationStatus.Success || details.Value is null ||
                !DevelopmentChatConversationRepository.Contains(details.Value, actor.Scope.Participant)) return null;
            var current = await authority.ResolveAsync(subject, module, token);
            if (current is null || !new CallParticipant(subject, actor.Scope.Participant).Matches(current.Scope) || !Future(current.ExpiresAtUtc) ||
                !await authority.AuthorizeAsync(current, operation, conversationId, call, token)) return null;
            var verified = await authority.ResolveAsync(subject, module, token);
            token.ThrowIfCancellationRequested();
            return verified is not null && new CallParticipant(subject, current.Scope.Participant).Matches(verified.Scope) && Future(verified.ExpiresAtUtc) ? verified : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
    private async Task<CallSession> CleanupAsync(CallSession call, CancellationToken token)
    {
        if (call.Provider is null) return call;
        var pending = false;
        foreach (var identity in new[] { call.Provider.CallerIdentity, call.Provider.CalleeIdentity })
            try { if (provider is null) throw new InvalidOperationException(); await provider.RemoveParticipantAsync(call.Provider, identity, token); }
            catch (Exception) { pending = true; }
        try { if (provider is null) throw new InvalidOperationException(); await provider.CloseRoomAsync(call.Provider, token); }
        catch (Exception) { pending = true; }
        call = call with { CleanupPending = pending }; repository.Save(call); return call;
    }
    private async Task<CallSession> PublishAsync(CallSession call, CancellationToken token)
    {
        var delivered = false;
        try { if (Signals is not null) delivered = await Signals.PublishAsync(call, token); }
        catch (Exception) { /* Persisted state is authoritative; signaling may require reconciliation. */ }
        call = call with { SignalingPending = !delivered };
        await repository.Gate.WaitAsync(CancellationToken.None);
        try { if (repository.Calls.TryGetValue(call.CallId, out var current) && current.Version == call.Version) repository.Save(call); }
        finally { repository.Gate.Release(); }
        return call;
    }

    internal async Task ReconcileAsync(CancellationToken token = default)
    {
        var changed = new List<CallSession>();
        await repository.Gate.WaitAsync(token);
        try
        {
            foreach (var original in repository.Calls.Values.ToArray())
            {
                var call = original;
                if (call.Terminal) { if (call.CleanupPending) call = await CleanupAsync(call, token); }
                else if (await ActorAsync(call.Caller.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null ||
                    await ActorAsync(call.Callee.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null)
                { call = call.Next(CallStatus.Ended, Now) with { CleanupPending = call.Provider is not null }; repository.Save(call); call = await CleanupAsync(call, token); }
                else if (call.Status == CallStatus.Ringing && options?.RingTimeout is { } timeout && timeout > TimeSpan.Zero && timeout != TimeSpan.MaxValue && Now - call.CreatedAtUtc >= timeout)
                { call = call.Next(CallStatus.Missed, Now); repository.Save(call); }
                else if (call.Provider is not null && provider is not null)
                {
                    try
                    {
                        var state = await provider.InspectAsync(call.Provider, token);
                        if (!state.RoomExists) { call = call.Next(CallStatus.Failed, Now) with { CleanupPending = false }; repository.Save(call); }
                        else if (await ActorAsync(call.Caller.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null ||
                            await ActorAsync(call.Callee.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null)
                        { call = call.Next(CallStatus.Ended, Now) with { CleanupPending = true }; repository.Save(call); call = await CleanupAsync(call, token); }
                        else
                        {
                            call = call with { CallerConnected = state.ConnectedIdentities.Contains(call.Provider.CallerIdentity),
                                CalleeConnected = state.ConnectedIdentities.Contains(call.Provider.CalleeIdentity) };
                            if (call.Status == CallStatus.Connecting && call.CallerConnected && call.CalleeConnected) call = call.Next(CallStatus.Active, Now);
                            else if (call.Status == CallStatus.Active && (!call.CallerConnected || !call.CalleeConnected)) call = call.Next(CallStatus.Disconnected, Now);
                            repository.Save(call);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { /* Unknown provider state never advances to Active. */ }
                }
                if (call.Version != original.Version || call.SignalingPending) changed.Add(call);
            }
        }
        finally { repository.Gate.Release(); }
        foreach (var call in changed) await PublishAsync(call, token);
    }

    internal async Task<CallOutcome> HandleWebhookAsync(ReadOnlyMemory<byte> rawBody, string authorization,
        ICallWebhookVerifier verifier, CancellationToken token = default)
    {
        VerifiedCallProviderEvent? value;
        try { value = await verifier.VerifyAsync(rawBody, authorization, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return CallOutcome.Unavailable; }
        if (value is null || string.IsNullOrWhiteSpace(value.EventId) || string.IsNullOrWhiteSpace(value.Fingerprint) ||
            !Enum.IsDefined(value.Kind)) return CallOutcome.Unavailable;
        CallSession? changed = null;
        await repository.Gate.WaitAsync(token);
        try
        {
            if (repository.Webhooks.TryGetValue(value.EventId, out var prior)) return prior == value.Fingerprint ? CallOutcome.Success : CallOutcome.InvalidRequest;
            var call = repository.Calls.Values.SingleOrDefault(item => item.Provider?.Room == value.Room);
            if (call is null || call.Provider is null) return CallOutcome.Unavailable;
            if (value.Kind is CallProviderEventKind.ParticipantJoined or CallProviderEventKind.ParticipantLeft &&
                value.Identity != call.Provider.CallerIdentity && value.Identity != call.Provider.CalleeIdentity) return CallOutcome.Unavailable;
            var watermark = (call.CallId, value.Identity ?? "room");
            if (!call.Terminal && (!repository.ProviderWatermarks.TryGetValue(watermark, out var last) || value.CreatedAtUtc > last))
            {
                if (value.Kind == CallProviderEventKind.RoomEnded)
                { call = call.Next(CallStatus.Ended, Now) with { CleanupPending = true }; repository.Save(call); call = await CleanupAsync(call, token); changed = call; }
                else if (value.Kind is CallProviderEventKind.ParticipantJoined or CallProviderEventKind.ParticipantLeft)
                {
                    var caller = value.Identity == call.Provider.CallerIdentity;
                    var joined = value.Kind == CallProviderEventKind.ParticipantJoined;
                    var participant = caller ? call.Caller : call.Callee;
                    if (joined && (await ActorAsync(call.Caller.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null ||
                        await ActorAsync(call.Callee.Subject, call.Module, CallOperation.Connect, call.ConversationId, call, token) is null))
                    { call = call.Next(CallStatus.Ended, Now) with { CleanupPending = true }; repository.Save(call); call = await CleanupAsync(call, token); changed = call; }
                    else if (call.Status is CallStatus.Connecting or CallStatus.Active or CallStatus.Disconnected)
                    {
                        call = caller ? call with { CallerConnected = joined } : call with { CalleeConnected = joined };
                        if (joined && call.Status == CallStatus.Connecting && call.CallerConnected && call.CalleeConnected)
                        { call = call.Next(CallStatus.Active, Now); changed = call; }
                        else if (!joined && call.Status is CallStatus.Active or CallStatus.Connecting)
                        { call = call.Next(CallStatus.Disconnected, Now); changed = call; }
                        repository.Save(call);
                    }
                }
                repository.ProviderWatermarks[watermark] = value.CreatedAtUtc;
            }
            repository.Webhooks.Add(value.EventId, value.Fingerprint);
        }
        finally { repository.Gate.Release(); }
        if (changed is not null) await PublishAsync(changed, token);
        return CallOutcome.Success;
    }
}
