using Andril.Communication.Interfaces;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Andril.API.Development.Chat;
using Andril.Communication.Chat;
using Andril.Communication.Chat.Development;
using Andril.Communication.Chat.Development.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Andril.IntegrationTests.Communication.Chat.Realtime.ChatRealtimeFixture;

namespace Andril.IntegrationTests.Communication.Chat.Realtime;

/// <summary>Actual loopback Kestrel/SignalR JSON protocol demo. No extra client NuGet package or production configuration.</summary>
public sealed class ChatRealtimeTransportTests
{
    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task LiveTwoClientDeliveryAutomaticReconnectRecoveryAndRevocation(ChatModule module)
    {
        using var fixture = await ChatRealtimeFixture.CreateAsync(module);
        await using var host = await DemoHost.StartAsync(fixture);
        await using var a = await WireClient.ConnectAsync(host.Url, host.TokenA);
        await using var b = await WireClient.ConnectAsync(host.Url, host.TokenB);
        await using var b2 = await WireClient.ConnectAsync(host.Url, host.TokenB);
        await using var c = await WireClient.ConnectAsync(host.Url, host.TokenC);
        AssertSuccess(await a.InvokeAsync("SubscribeConversation", module, Conversation));
        AssertSuccess(await b.InvokeAsync("SubscribeConversation", module, Conversation));
        AssertSuccess(await b2.InvokeAsync("SubscribeConversation", module, Conversation));
        AssertUnavailable(await c.InvokeAsync("SubscribeConversation", module, Conversation));
        var foreign = module == ChatModule.Learning ? ChatModule.Hobbies : ChatModule.Learning;
        AssertUnavailable(await c.InvokeAsync("SubscribeConversation", foreign, Conversation));

        var first = await fixture.Sender().SendAsync(module, Conversation, Key(1), "A to B live");
        Assert.Equal(ChatConversationStatus.Success, first.Storage.Status);
        var created = await b.NextEventAsync("ConversationMessageCreated");
        Assert.Equal(first.Storage.Value!.MessageId, created.GetProperty("messageId").GetGuid());
        Assert.Equal(first.Storage.Value.CreatedAtUtc, created.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.False(created.TryGetProperty("text", out _));
        Assert.False(created.TryGetProperty("sender", out _));
        Assert.Equal(created.GetProperty("eventId").GetGuid(),
            (await b2.NextEventAsync("ConversationMessageCreated")).GetProperty("eventId").GetGuid());
        var liveHistory = await b.InvokeAsync("ReadHistory", module, Conversation, 1, null);
        AssertSuccess(liveHistory);
        Assert.Equal("A to B live", liveHistory.GetProperty("value").GetProperty("items")[0].GetProperty("text").GetString());
        var ack = await b.InvokeAsync("Acknowledge", module, Conversation, first.Storage.Value.MessageId);
        AssertSuccess(ack);

        // Start an automatic reconnect observer BEFORE the socket drops. A gate makes the offline interval deterministic.
        var permitReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredTask = b.ReconnectAndRecoverWhenClosedAsync(host.Url, host.TokenB, module, permitReconnect.Task);
        await b.DropAsync();
        var missed = await fixture.Sender().SendAsync(module, Conversation, Key(2), "while disconnected");
        permitReconnect.SetResult();
        var recovered = await recoveredTask.WaitAsync(TimeSpan.FromSeconds(15));
        await using var reconnected = recovered.Client;
        var knownIds = new HashSet<Guid> { first.Storage.Value.MessageId };
        foreach (var id in recovered.MessageIds) knownIds.Add(id);
        Assert.Equal(2, knownIds.Count);
        Assert.Contains(missed.Storage.Value!.MessageId, knownIds);
        Assert.Equal(new[] { missed.Storage.Value.MessageId, first.Storage.Value.MessageId }, recovered.MessageIds);
        var retry = await fixture.Sender().SendAsync(module, Conversation, Key(2), "while disconnected");
        Assert.Equal(missed.Storage.Value.MessageId, retry.Storage.Value!.MessageId);
        var overlapping = await reconnected.NextEventAsync("ConversationMessageCreated");
        Assert.False(knownIds.Add(overlapping.GetProperty("messageId").GetGuid()));
        Assert.Equal(2, knownIds.Count);

        fixture.B.SubscriptionAllowed = false;
        await fixture.Sender().SendAsync(module, Conversation, Key(3), "after revocation");
        await reconnected.NextEventAsync("ConversationAccessRevoked");
        AssertUnavailable(await reconnected.InvokeAsync("ReadHistory", module, Conversation, 2, null));
        AssertUnavailable(await reconnected.InvokeAsync("SubscribeConversation", module, Conversation));
        await using var revokedReconnect = await WireClient.ConnectAsync(host.Url, host.TokenB);
        AssertUnavailable(await revokedReconnect.InvokeAsync("SubscribeConversation", module, Conversation));
        AssertUnavailable(await c.InvokeAsync("ReadHistory", module, Conversation, 2, null));
    }

    [Fact]
    public async Task AnonymousAndUnknownBearerConnectionsAreRejectedByAspNetAuthentication()
    {
        using var fixture = await ChatRealtimeFixture.CreateAsync();
        await using var host = await DemoHost.StartAsync(fixture);
        using var client = new HttpClient { BaseAddress = host.Url };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/__development/chat/negotiate?negotiateVersion=1", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-fixture-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/__development/chat/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task CandidateHubRejectsConnectionsOutsideItsExplicitDevelopmentEnvironment()
    {
        using var fixture = await ChatRealtimeFixture.CreateAsync();
        await using var host = await DemoHost.StartAsync(fixture, candidateEnvironment: false);
        await using var client = await WireClient.ConnectAsync(host.Url, host.TokenB);
        await client.WaitForCloseAsync();
    }
    private static void AssertSuccess(JsonElement value) => Assert.Equal((int)ChatConversationStatus.Success, value.GetProperty("status").GetInt32());
    private static void AssertUnavailable(JsonElement value) => Assert.Equal((int)ChatConversationStatus.Unavailable, value.GetProperty("status").GetInt32());

    private sealed class DemoHost(IHost host, Uri url, Credentials credentials) : IAsyncDisposable
    {
        internal Uri Url { get; } = url;
        internal string TokenA => credentials.ForSubject[0].Token;
        internal string TokenB => credentials.ForSubject[1].Token;
        internal string TokenC => credentials.ForSubject[2].Token;
        internal static async Task<DemoHost> StartAsync(ChatRealtimeFixture fixture, bool candidateEnvironment = true)
        {
            var credentials = new Credentials([fixture.A, fixture.B, fixture.C]);
            // Never invoke production Program, load production config, or bind beyond loopback.
            var host = new HostBuilder().UseEnvironment(candidateEnvironment ? "ChatRealtimeDevelopment" : Environments.Production).ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHost(web => web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0))
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(credentials);
                        services.AddAuthentication("SyntheticD10Only").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("SyntheticD10Only", _ => { });
                        services.AddAuthorization();
                        services.AddRouting();
                        services.AddSignalR(options => options.EnableDetailedErrors = false);
                        services.AddSingleton(fixture.Realtime);
                    }).Configure(app =>
                    {
                        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapHub<DevelopmentChatHub>("/__development/chat",
                            options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization());
                    })).Build();
            try
            {
                await host.StartAsync();
                var server = host.Services.GetRequiredService<IServer>();
                var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new(host, new Uri(address), credentials);
            }
            catch { host.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { await host.StopAsync(); host.Dispose(); }
    }
    private sealed class Credentials(Actor[] actors)
    {
        internal (string Token, Actor Actor)[] ForSubject { get; } = actors.Select(actor => (Guid.NewGuid().ToString("N"), actor)).ToArray();
    }
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, Credentials credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            var candidate = credentials.ForSubject.FirstOrDefault(value => header == "Bearer " + value.Token);
            if (candidate.Actor is null || !candidate.Actor.SessionActive)
                return Task.FromResult(AuthenticateResult.NoResult());
            var actor = candidate.Actor;
            var identity = new ClaimsIdentity([
                new("sub", actor.Scope.VerifiedSubjectId.ToString()), new("role", "authenticated"),
                new("is_anonymous", "false"), new("exp", actor.Expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),
                new AuthenticationProperties { ExpiresUtc = actor.Expires }, Scheme.Name)));
        }
    }

    // Minimal test-only SignalR JSON/WebSocket client. This exercises real frames, not an IHubContext mock.
    private sealed class WireClient : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
        private readonly TaskCompletionSource handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task reader = Task.CompletedTask;
        private int invocation;
        private int disposed;
        internal static async Task<WireClient> ConnectAsync(Uri origin, string token)
        {
            using var http = new HttpClient { BaseAddress = origin };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.PostAsync("/__development/chat/negotiate?negotiateVersion=1", null);
            response.EnsureSuccessStatusCode();
            var negotiation = await response.Content.ReadFromJsonAsync<JsonElement>();
            var connectionToken = negotiation.GetProperty("connectionToken").GetString();
            var url = new UriBuilder(origin) { Scheme = "ws", Path = "/__development/chat", Query = "id=" + Uri.EscapeDataString(connectionToken!) }.Uri;
            var client = new WireClient();
            try
            {
                client.socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await client.socket.ConnectAsync(url, CancellationToken.None);
                client.reader = client.ReadAsync();
                await client.SendAsync(new { protocol = "json", version = 1 });
                await client.handshake.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return client;
            }
            catch { await client.DisposeAsync(); throw; }
        }
        internal async Task<JsonElement> InvokeAsync(string target, params object?[] arguments)
        {
            var id = Interlocked.Increment(ref invocation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            await SendAsync(new { type = 1, invocationId = id, target, arguments });
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { pending.TryRemove(id, out _); }
        }
        internal async Task<JsonElement> NextEventAsync(string target)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var value = await events.Reader.ReadAsync(timeout.Token);
            Assert.Equal(target, value.GetProperty("target").GetString());
            return value.GetProperty("arguments")[0];
        }
        internal async Task<(WireClient Client, Guid[] MessageIds)> ReconnectAndRecoverWhenClosedAsync(Uri origin,
            string token, ChatModule module, Task reconnectAllowed)
        {
            await closed.Task;
            await reconnectAllowed;
            var replacement = await ConnectAsync(origin, token);
            try
            {
                AssertSuccess(await replacement.InvokeAsync("SubscribeConversation", module, Conversation));
                var ids = new List<Guid>();
                string? cursor = null;
                do
                {
                    var page = await replacement.InvokeAsync("ReadHistory", module, Conversation, 1, cursor);
                    AssertSuccess(page);
                    var value = page.GetProperty("value");
                    ids.AddRange(value.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("messageId").GetGuid()));
                    cursor = value.GetProperty("nextCursor").GetString();
                } while (cursor is not null);
                return (replacement, ids.ToArray());
            }
            catch { await replacement.DisposeAsync(); throw; }
        }
        private Task SendAsync<T>(T value)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + '\u001e');
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stop.Token);
        }
        private async Task ReadAsync()
        {
            var buffer = new byte[4096];
            using var message = new MemoryStream();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    for (var index = 0; index < received.Count; index++)
                    {
                        if (buffer[index] != 0x1e) { message.WriteByte(buffer[index]); continue; }
                        using var document = JsonDocument.Parse(message.ToArray());
                        var frame = document.RootElement.Clone(); message.SetLength(0);
                        if (!frame.TryGetProperty("type", out var type))
                        {
                            if (frame.TryGetProperty("error", out _)) handshake.TrySetException(new InvalidOperationException("Handshake rejected"));
                            else handshake.TrySetResult();
                        }
                        else if (type.GetInt32() == 1) await events.Writer.WriteAsync(frame, stop.Token);
                        else if (type.GetInt32() == 3 && pending.TryGetValue(frame.GetProperty("invocationId").GetString()!, out var completion))
                        {
                            if (frame.TryGetProperty("error", out _)) completion.TrySetException(new InvalidOperationException("Hub invocation failed"));
                            else completion.TrySetResult(frame.TryGetProperty("result", out var result) ? result : default);
                        }
                        else if (type.GetInt32() == 7) return;
                    }
                }
            }
            catch (Exception) when (stop.IsCancellationRequested || socket.State != WebSocketState.Open) { }
            finally { closed.TrySetResult(); events.Writer.TryComplete(); }
        }
        internal Task WaitForCloseAsync() => closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task DropAsync() { socket.Abort(); stop.Cancel(); await reader; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await DropAsync(); socket.Dispose(); stop.Dispose();
        }
    }
}
