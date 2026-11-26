using Andril.API.Authentication;
using Andril.Communication.Calls.Development;
using Andril.Communication.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Andril.API.Development.Chat;

// D11 methods on the SAME unmapped, authenticated, environment-guarded D10 hub.
internal sealed partial class DevelopmentChatHub
{
    private Guid Subject
    {
        get
        {
            var subject = VerifiedUserSubject.GetId(Context.User);
            var claims = Context.User?.FindAll("exp").ToArray();
            if (subject is null || calls is null || claims?.Length != 1 || !long.TryParse(claims[0].Value,
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return Guid.Empty;
            try { return calls.AuthenticationCurrent(DateTimeOffset.FromUnixTimeSeconds(seconds)) ? subject.Value : Guid.Empty; }
            catch (ArgumentOutOfRangeException) { return Guid.Empty; }
        }
    }
    public Task<CallOutcome> SubscribeCalls(ChatModule module, Guid conversationId) => callSignals is null
        ? Task.FromResult(CallOutcome.Unavailable) : callSignals.SubscribeAsync(Context.ConnectionId, module, conversationId, Context.ConnectionAborted);
    public Task<CallOutcome> UnsubscribeCalls(ChatModule module, Guid conversationId) => callSignals is null
        ? Task.FromResult(CallOutcome.Unavailable) : callSignals.UnsubscribeAsync(Context.ConnectionId, module, conversationId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> InitiateCall(ChatModule module, Guid conversationId, Guid requestId, CallType type) => calls is null
        ? Missing() : calls.InitiateCallAsync(Subject, module, conversationId, requestId, type, Context.ConnectionAborted);
    public Task<CallResult<CallView>> AcceptCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.AcceptCallAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> RejectCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.RejectCallAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> CancelCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.CancelCallAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> EndCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.EndCallAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> GetCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.GetCallSessionAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> DisconnectCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.DisconnectAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallView>> ReconnectCall(ChatModule module, Guid callId) => calls is null ? Missing() : calls.ReconnectAsync(Subject, module, callId, Context.ConnectionAborted);
    public Task<CallResult<CallCredential>> GetCallCredential(ChatModule module, Guid callId) => calls is null
        ? Task.FromResult(new CallResult<CallCredential>(CallOutcome.Unavailable)) : calls.GetCredentialAsync(Subject, module, callId, Context.ConnectionAborted);
    private static Task<CallResult<CallView>> Missing() => Task.FromResult(new CallResult<CallView>(CallOutcome.Unavailable));
    private sealed class CallClient(IHubContext<DevelopmentChatHub> hub, string id) : ICallSignalClient
    {
        public Task ReceiveAsync(CallSignal signal, CancellationToken cancellationToken) => hub.Clients.Client(id).SendAsync(signal.Kind.ToString(), signal, cancellationToken);
    }
}
