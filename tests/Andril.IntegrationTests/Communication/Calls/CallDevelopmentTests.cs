using Andril.Communication.Calls.Development;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.IntegrationTests.Communication.Chat.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Andril.IntegrationTests.Communication.Chat.Realtime.ChatRealtimeFixture;

namespace Andril.IntegrationTests.Communication.Calls;

public sealed class CallDevelopmentTests
{
    [Theory]
    [InlineData(ChatModule.Learning, 1)] [InlineData(ChatModule.Learning, 2)]
    [InlineData(ChatModule.Hobbies, 1)] [InlineData(ChatModule.Hobbies, 2)]
    [InlineData(ChatModule.Social, 1)] [InlineData(ChatModule.Social, 2)]
    public async Task TwoUserLifecycleUsesRealStateMachineAndDistinctDevelopmentCredentials(ChatModule module, int type)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(module);
        var b = await f.ListenAsync("b", f.B);
        var callId = await f.RingAsync((CallType)type);
        Assert.Contains(b.Events, value => value.Kind == CallSignalKind.IncomingCall);
        var accepted = await f.Calls.AcceptCallAsync(f.B, module, callId);
        Assert.Equal(CallStatus.Connecting, accepted.Value!.Status);
        var first = await f.Calls.GetCredentialAsync(f.A, module, callId);
        var second = await f.Calls.GetCredentialAsync(f.B, module, callId);
        Assert.Equal(CallOutcome.Success, first.Outcome); Assert.Equal(CallOutcome.Success, second.Outcome);
        Assert.Equal(first.Value!.Room, second.Value!.Room);
        Assert.NotEqual(first.Value.Identity, second.Value.Identity); Assert.NotEqual(first.Value.Token, second.Value.Token);
        Assert.Null((await f.Calls.GetCredentialAsync(f.C, module, callId)).Value);
        Assert.Null((await f.Calls.GetCredentialAsync(f.A, module == ChatModule.Learning ? ChatModule.Hobbies : ChatModule.Learning, callId)).Value);
        Assert.DoesNotContain(first.Value.Token, first.ToString());
        await f.JoinAsync(callId, true, "a-joined");
        Assert.Equal(CallStatus.Connecting, f.Repository.Calls[callId].Status);
        await f.JoinAsync(callId, false, "b-joined");
        Assert.Equal(CallStatus.Active, f.Repository.Calls[callId].Status);
        Assert.Equal(CallStatus.Ended, (await f.Calls.EndCallAsync(f.A, module, callId)).Value!.Status);
        Assert.Empty(f.Provider.Rooms);
        Assert.Equal(CallOutcome.Unavailable, (await f.Calls.GetCredentialAsync(f.A, module, callId)).Outcome);
        Assert.Equal(new[] { CallStatus.Initiated, CallStatus.Ringing, CallStatus.Accepted, CallStatus.Connecting, CallStatus.Active, CallStatus.Ended },
            f.Repository.Transitions.Select(value => value.Status));
        Assert.Equal(CallOutcome.Conflict, (await f.Calls.AcceptCallAsync(f.B, module, callId)).Outcome);
    }

    [Theory] [InlineData(1)] [InlineData(2)]
    public async Task ExistingD10SignalRHubCarriesActualAudioAndVideoCallSignaling(int type)
    {
        using var f = await CallDevelopmentFixture.CreateAsync();
        await using var host = await ChatRealtimeTransportTests.DemoHost.StartAsync(f.Chat, configure: services =>
        { services.AddSingleton(f.Calls); services.AddSingleton(f.Signals); });
        await using var a = await ChatRealtimeTransportTests.WireClient.ConnectAsync(host.Url, host.TokenA);
        await using var b = await ChatRealtimeTransportTests.WireClient.ConnectAsync(host.Url, host.TokenB);
        await using var c = await ChatRealtimeTransportTests.WireClient.ConnectAsync(host.Url, host.TokenC);
        Assert.Equal((int)CallOutcome.Success, (await b.InvokeAsync("SubscribeCalls", f.Module, Conversation)).GetInt32());
        Assert.Equal((int)CallOutcome.Unavailable, (await c.InvokeAsync("SubscribeCalls", f.Module, Conversation)).GetInt32());
        var initiated = await a.InvokeAsync("InitiateCall", f.Module, Conversation, Key(900), type);
        Assert.Equal((int)CallOutcome.Success, initiated.GetProperty("outcome").GetInt32());
        var id = initiated.GetProperty("value").GetProperty("callId").GetGuid();
        Assert.Equal(id, (await b.NextEventAsync("IncomingCall")).GetProperty("callId").GetGuid());
        await b.NextEventAsync("CallRinging");
        Assert.Equal((int)CallOutcome.Unavailable, (await c.InvokeAsync("AcceptCall", f.Module, id)).GetProperty("outcome").GetInt32());
        Assert.Equal((int)CallOutcome.Success, (await b.InvokeAsync("AcceptCall", f.Module, id)).GetProperty("outcome").GetInt32());
        await b.NextEventAsync("CallAccepted"); await b.NextEventAsync("CallConnecting");
        var credentialA = await a.InvokeAsync("GetCallCredential", f.Module, id);
        var credentialB = await b.InvokeAsync("GetCallCredential", f.Module, id);
        Assert.NotEqual(credentialA.GetProperty("value").GetProperty("identity").GetString(), credentialB.GetProperty("value").GetProperty("identity").GetString());
        await f.JoinAsync(id, true, "a"); await f.JoinAsync(id, false, "b"); await b.NextEventAsync("CallActive");
        await b.DropAsync();
        await using var reconnected = await ChatRealtimeTransportTests.WireClient.ConnectAsync(host.Url, host.TokenB);
        Assert.Equal((int)CallOutcome.Success, (await reconnected.InvokeAsync("SubscribeCalls", f.Module, Conversation)).GetInt32());
        Assert.Equal((int)CallStatus.Active, (await reconnected.InvokeAsync("GetCall", f.Module, id)).GetProperty("value").GetProperty("status").GetInt32());
        Assert.Equal((int)CallOutcome.Success, (await a.InvokeAsync("EndCall", f.Module, id)).GetProperty("outcome").GetInt32());
        await reconnected.NextEventAsync("CallEnded");
        Assert.Equal((int)CallOutcome.Unavailable, (await reconnected.InvokeAsync("GetCallCredential", f.Module, id)).GetProperty("outcome").GetInt32());
        // Media observations and credentials here come from a deterministic fake provider, not a live LiveKit server.
    }

    [Theory] [InlineData("rejected")] [InlineData("cancelled")] [InlineData("ended")]
    public async Task TerminalCallsCannotBeAcceptedOrIssueCredentials(string terminal)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
        var result = terminal switch { "rejected" => await f.Calls.RejectCallAsync(f.B, f.Module, id),
            "cancelled" => await f.Calls.CancelCallAsync(f.A, f.Module, id), _ => await f.Calls.EndCallAsync(f.A, f.Module, id) };
        Assert.Equal(CallOutcome.Success, result.Outcome);
        Assert.Equal(CallOutcome.Conflict, (await f.Calls.AcceptCallAsync(f.B, f.Module, id)).Outcome);
        Assert.Equal(CallOutcome.Unavailable, (await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Outcome);
        Assert.Equal(0, f.Provider.Creates);
    }
    [Fact]
    public async Task InitiationAndConcurrentAcceptanceAreIdempotentAndRejectConflictingTypes()
    {
        using var f = await CallDevelopmentFixture.CreateAsync();
        var starts = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => f.Calls.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Audio))));
        Assert.All(starts, value => Assert.Equal(CallOutcome.Success, value.Outcome));
        var id = starts[0].Value!.CallId; Assert.Single(f.Repository.Calls);
        Assert.Equal(CallOutcome.Conflict, (await f.Calls.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Video)).Outcome);
        var accepts = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => f.Calls.AcceptCallAsync(f.B, f.Module, id))));
        Assert.All(accepts, value => Assert.Equal(CallOutcome.Success, value.Outcome)); Assert.Equal(1, f.Provider.Creates);
    }
    [Fact]
    public async Task ConcurrentEndAndAcceptCannotLeaveAnActiveOrphanRoom()
    {
        for (var index = 0; index < 8; index++)
        {
            using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = Task.Run(async () => { await start.Task; return await f.Calls.AcceptCallAsync(f.B, f.Module, id); });
            var end = Task.Run(async () => { await start.Task; return await f.Calls.EndCallAsync(f.A, f.Module, id); });
            start.SetResult(); await Task.WhenAll(accept, end);
            Assert.Equal(CallStatus.Ended, f.Repository.Calls[id].Status); Assert.Empty(f.Provider.Rooms);
            Assert.Equal(CallOutcome.Unavailable, (await f.Calls.GetCredentialAsync(f.A, f.Module, id)).Outcome);
        }
    }
    [Theory] [InlineData("third-party")] [InlineData("module")] [InlineData("profile")] [InlineData("caller-accept")]
    public async Task ExactParticipantAndOperationBindingsAreRequired(string mismatch)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
        var subject = mismatch == "third-party" ? f.C : mismatch == "caller-accept" ? f.A : f.B;
        var module = mismatch == "module" ? ChatModule.Learning : f.Module;
        if (mismatch == "profile") f.Chat.B.Scope = new(f.B, ChatParticipantReference.Create(f.Module, f.Chat.B.Scope.Participant.AccountId, Key(999)));
        Assert.Equal(CallOutcome.Unavailable, (await f.Calls.AcceptCallAsync(subject, module, id)).Outcome);
        if (mismatch != "caller-accept")
        {
            Assert.Null((await f.Calls.GetCallSessionAsync(subject, module, id)).Value);
            Assert.Null((await f.Calls.EndCallAsync(subject, module, id)).Value);
        }
        Assert.Equal(0, f.Provider.Creates);
    }
    [Fact]
    public async Task CallerRevokedDuringCalleeAuthorizationCannotCreateOrSignalACall()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var b = await f.ListenAsync("b", f.B);
        f.Grants.OnCheck = operation => { if (operation == CallOperation.Receive) f.Grants.Revoked.Add(f.A); };
        var result = await f.Calls.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Audio);
        Assert.Equal(CallOutcome.Unavailable, result.Outcome); Assert.Empty(f.Repository.Calls); Assert.Empty(b.Events);
    }
    [Fact]
    public async Task D8AndD10PositiveAccessDoesNotSupplyCallAuthority()
    {
        using var f = await CallDevelopmentFixture.CreateAsync();
        var denied = new DevelopmentCallManagementService(f.Repository, provider: f.Provider, timeProvider: f.Chat.Time);
        Assert.Equal(CallOutcome.Unavailable, (await denied.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Audio)).Outcome);
        f.Grants.Denied.Add(CallOperation.Receive);
        Assert.Equal(CallOutcome.Unavailable, (await f.Calls.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Audio)).Outcome);
        Assert.Empty(f.Repository.Calls);
    }
    [Theory] [InlineData("block")] [InlineData("unmatch")] [InlineData("deactivate")] [InlineData("session")] [InlineData("expiry")]
    public async Task RevocationPreventsBothCredentialsAndReconcilesProviderRemoval(string reason)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync();
        switch (reason)
        {
            case "block": case "unmatch": f.Grants.Revoked.Add(f.B); break;
            case "deactivate": f.Chat.B.Active = false; break;
            case "session": f.Chat.B.SessionActive = false; break;
            case "expiry": f.Chat.B.Expires = f.Chat.Time.Now; break;
        }
        Assert.Null((await f.Calls.GetCredentialAsync(f.A, f.Module, id)).Value);
        Assert.Null((await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Value);
        await f.Calls.ReconcileAsync(); Assert.Equal(CallStatus.Ended, f.Repository.Calls[id].Status); Assert.Empty(f.Provider.Rooms);
        Assert.Equal(0, f.Provider.Credentials);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ProviderFailureAndLostAcknowledgementProduceSafeTerminalState(bool lostAck)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
        f.Provider.FailCreate = !lostAck; f.Provider.LoseCreateAcknowledgement = lostAck; f.Provider.FailCleanup = true;
        var result = await f.Calls.AcceptCallAsync(f.B, f.Module, id);
        Assert.Equal(CallOutcome.ProviderUnavailable, result.Outcome); Assert.Equal(CallStatus.Failed, result.Value!.Status);
        Assert.True(result.Value.CleanupPending); Assert.Null((await f.Calls.GetCredentialAsync(f.A, f.Module, id)).Value);
        f.Provider.FailCleanup = false; await f.Calls.ReconcileAsync(); Assert.Empty(f.Provider.Rooms); Assert.False(f.Repository.Calls[id].CleanupPending);
        Assert.DoesNotContain("private", result.ToString());
    }
    [Fact]
    public async Task DisconnectRequiresAuthorizedReconnectAndFreshProviderObservations()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync();
        await f.JoinAsync(id, true, "a"); await f.JoinAsync(id, false, "b");
        Assert.Equal(CallStatus.Disconnected, (await f.Calls.DisconnectAsync(f.B, f.Module, id)).Value!.Status);
        Assert.Null((await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Value);
        Assert.Equal(CallStatus.Connecting, (await f.Calls.ReconnectAsync(f.B, f.Module, id)).Value!.Status);
        Assert.Equal(CallOutcome.Success, (await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Outcome);
        await f.JoinAsync(id, false, "b-again", f.Chat.Time.Now.AddSeconds(1)); Assert.Equal(CallStatus.Active, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task VerifiedWebhookReplayAndOldJoinCannotReopenOrUndoLeave()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync();
        await f.JoinAsync(id, true, "a"); await f.JoinAsync(id, false, "b");
        var call = f.Repository.Calls[id];
        var left = new VerifiedCallProviderEvent("left", "fingerprint", CallProviderEventKind.ParticipantLeft, call.Provider!.Room, call.Provider.CalleeIdentity, f.Chat.Time.Now.AddSeconds(2));
        var verifier = new CallDevelopmentFixture.VerifiedFixture(left);
        await f.Calls.HandleWebhookAsync(new byte[] { 1 }, "test", verifier);
        var version = f.Repository.Calls[id].Version;
        await f.Calls.HandleWebhookAsync(new byte[] { 1 }, "test", verifier); Assert.Equal(version, f.Repository.Calls[id].Version);
        Assert.Equal(CallOutcome.InvalidRequest, await f.Calls.HandleWebhookAsync(new byte[] { 1 }, "test", new CallDevelopmentFixture.VerifiedFixture(left with { Fingerprint = "different" })));
        await f.JoinAsync(id, false, "old-join", f.Chat.Time.Now.AddSeconds(1)); Assert.Equal(CallStatus.Disconnected, f.Repository.Calls[id].Status);
        await f.Calls.EndCallAsync(f.A, f.Module, id);
        await f.JoinAsync(id, true, "late-join", f.Chat.Time.Now.AddSeconds(3)); Assert.Equal(CallStatus.Ended, f.Repository.Calls[id].Status);
    }
    [Theory] [InlineData("unverified")] [InlineData("room")] [InlineData("identity")]
    public async Task UnverifiedOrForeignWebhooksCannotConnectParticipants(string invalid)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync(); var call = f.Repository.Calls[id];
        VerifiedCallProviderEvent? value = invalid == "unverified" ? null : new("event", "hash", CallProviderEventKind.ParticipantJoined,
            invalid == "room" ? "foreign" : call.Provider!.Room, "foreign-identity", f.Chat.Time.Now);
        Assert.Equal(CallOutcome.Unavailable, await f.Calls.HandleWebhookAsync(new byte[] { 1 }, "test", new CallDevelopmentFixture.VerifiedFixture(value)));
        Assert.Equal(CallStatus.Connecting, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task SignalingFailureAndReplayDoNotChangeStoredCallState()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var b = await f.ListenAsync("b", f.B); b.Fail = true;
        var id = await f.RingAsync(); var ringing = f.Repository.Calls[id]; Assert.True(ringing.SignalingPending);
        b.Fail = false; await f.Calls.ReconcileAsync(); Assert.Single(b.Events, value => value.Kind == CallSignalKind.IncomingCall);
        await f.Calls.EndCallAsync(f.A, f.Module, id); var count = b.Events.Count;
        await f.Signals.PublishAsync(ringing, default); Assert.Equal(count, b.Events.Count); Assert.Equal(CallStatus.Ended, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task CancellationBeforeMutationAndAfterProviderCreationAreDistinguished()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Calls.InitiateCallAsync(f.A, f.Module, Conversation, Key(900), CallType.Audio, before.Token));
        Assert.Empty(f.Repository.Calls); var id = await f.RingAsync(); using var after = new CancellationTokenSource();
        f.Provider.AfterCreate = () => after.Cancel();
        var accepted = await f.Calls.AcceptCallAsync(f.B, f.Module, id, after.Token);
        Assert.Equal(CallOutcome.ProviderUnavailable, accepted.Outcome); Assert.Equal(CallStatus.Failed, f.Repository.Calls[id].Status); Assert.Empty(f.Provider.Rooms);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RingTimeoutHasNoInventedDefault(bool configured)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(ringTimeout: configured ? TimeSpan.FromSeconds(30) : null); var id = await f.RingAsync();
        f.Chat.Time.Now = f.Chat.Time.Now.AddSeconds(31); await f.Calls.ReconcileAsync();
        Assert.Equal(configured ? CallStatus.Missed : CallStatus.Ringing, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task ReconciliationRecoversLostProviderEventsWithoutTreatingSignalingAsMedia()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync(); var call = f.Repository.Calls[id];
        var started = new VerifiedCallProviderEvent("room", "room", CallProviderEventKind.RoomStarted, call.Provider!.Room, null, f.Chat.Time.Now);
        await f.Calls.HandleWebhookAsync(new byte[] { 1 }, "test", new CallDevelopmentFixture.VerifiedFixture(started));
        Assert.Equal(CallStatus.Connecting, f.Repository.Calls[id].Status);
        f.Provider.Joined.Add(call.Provider.CallerIdentity); f.Provider.Joined.Add(call.Provider.CalleeIdentity);
        await f.Calls.ReconcileAsync(); Assert.Equal(CallStatus.Active, f.Repository.Calls[id].Status);
        f.Provider.Joined.Remove(call.Provider.CalleeIdentity); await f.Calls.ReconcileAsync();
        Assert.Equal(CallStatus.Disconnected, f.Repository.Calls[id].Status);
        f.Provider.Rooms.Clear(); await f.Calls.ReconcileAsync(); Assert.Equal(CallStatus.Failed, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task ExpiredSignalingAuthenticationCannotUseRenewedActorAuthority()
    {
        using var f = await CallDevelopmentFixture.CreateAsync();
        await using var host = await ChatRealtimeTransportTests.DemoHost.StartAsync(f.Chat, configure: services => { services.AddSingleton(f.Calls); services.AddSingleton(f.Signals); });
        await using var a = await ChatRealtimeTransportTests.WireClient.ConnectAsync(host.Url, host.TokenA);
        f.Chat.Time.Now = f.Chat.A.Expires;
        f.Chat.A.Expires = f.Chat.Time.Now.AddHours(1); f.Chat.B.Expires = f.Chat.Time.Now.AddHours(1);
        var denied = await a.InvokeAsync("InitiateCall", f.Module, Conversation, Key(900), CallType.Audio);
        Assert.Equal((int)CallOutcome.Unavailable, denied.GetProperty("outcome").GetInt32()); Assert.Empty(f.Repository.Calls);
    }
    [Theory] [InlineData("reject")] [InlineData("cancel")]
    public async Task RoleSpecificCommandsCannotBeSwapped(string command)
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
        var result = command == "reject" ? await f.Calls.RejectCallAsync(f.A, f.Module, id) : await f.Calls.CancelCallAsync(f.B, f.Module, id);
        Assert.Equal(CallOutcome.Unavailable, result.Outcome); Assert.Equal(CallStatus.Ringing, f.Repository.Calls[id].Status);
    }
    [Fact]
    public async Task IndependentCallSignalGrantAndMidDeliveryRevocationAreEnforced()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var b = await f.ListenAsync("b", f.B);
        b.AfterSignal = () => f.Grants.Revoked.Add(f.B);
        await f.RingAsync(); Assert.Equal(CallSignalKind.IncomingCall, Assert.Single(b.Events).Kind);
        Assert.Equal(CallOutcome.Unavailable, await f.Signals.SubscribeAsync("b", f.Module, Conversation, default));
    }
    [Fact]
    public async Task ConnectPermissionIsRequiredBeforeProviderCreationAndCredentials()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.RingAsync();
        f.Grants.Denied.Add(CallOperation.Connect);
        Assert.Equal(CallOutcome.Unavailable, (await f.Calls.AcceptCallAsync(f.B, f.Module, id)).Outcome); Assert.Equal(0, f.Provider.Creates);
        f.Grants.Denied.Clear(); await f.Calls.AcceptCallAsync(f.B, f.Module, id);
        f.Grants.Denied.Add(CallOperation.Connect);
        Assert.Null((await f.Calls.GetCredentialAsync(f.A, f.Module, id)).Value); Assert.Equal(0, f.Provider.Credentials);
    }
    [Fact]
    public async Task ShortenedAuthorityExpiryDuringTokenCreationPreventsCredentialDelivery()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync();
        f.Provider.AfterCredential = () => f.Chat.B.Expires = f.Chat.Time.Now.AddSeconds(30);
        Assert.Null((await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Value);
    }
    [Fact]
    public async Task RevocationDuringCredentialGenerationPreventsCredentialDelivery()
    {
        using var f = await CallDevelopmentFixture.CreateAsync(); var id = await f.ConnectingAsync();
        f.Provider.AfterCredential = () => f.Grants.Revoked.Add(f.B);
        Assert.Null((await f.Calls.GetCredentialAsync(f.B, f.Module, id)).Value);
        await f.Calls.ReconcileAsync(); Assert.Empty(f.Provider.Rooms);
    }
}
