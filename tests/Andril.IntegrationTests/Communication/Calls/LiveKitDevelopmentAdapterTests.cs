using Microsoft.Extensions.Configuration;
﻿using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andril.API.Development.Calls;
using Andril.Communication.Calls.Development;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Andril.IntegrationTests.Communication.Calls;

public sealed class LiveKitDevelopmentAdapterTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly CallProviderReference Room = new("cr_11111111111111111111111111111111", "cp_22222222222222222222222222222222", "cp_33333333333333333333333333333333");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "ChatRealtimeDevelopment";
        public string ApplicationName { get; set; } = "SyntheticD11";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Harness : IDisposable
    {
        internal string Secret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        internal Handler Http { get; } = new();
        internal HttpClient Client { get; }
        internal LiveKitDevelopmentOptions Options => new(new Uri("https://synthetic.invalid"), new Uri("wss://synthetic.invalid"), "synthetic-api-key", Secret, TimeSpan.FromMinutes(1));
        internal LiveKitDevelopmentAdapter Adapter { get; }
        internal Harness() { Client = new(Http); Adapter = new(Client, Options, new Environment(), new Clock()); }
        public void Dispose() => Client.Dispose();
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<(string Method, JsonElement Body, JwtSecurityToken Token)> Requests { get; } = [];
        internal bool Stall { get; set; }
        internal bool Fail { get; set; }
        internal bool WrongRoom { get; set; }
        internal bool Missing { get; set; }
        internal bool ProxyNotFound { get; set; }
        internal bool TwirpNotFound { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var method = request.RequestUri!.Segments.Last();
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            Requests.Add((method, body, new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter)));
            if (ProxyNotFound) return new(HttpStatusCode.NotFound) { Content = new StringContent("upstream route missing") };
            if (TwirpNotFound) return new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { code = "not_found" }) };
            if (Fail) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("private provider secret") };
            object response = method switch
            {
                "CreateRoom" => new { name = WrongRoom ? "wrong-room" : body.GetProperty("name").GetString() },
                "ListRooms" => new { rooms = Missing ? Array.Empty<object>() : new object[] { new { name = Room.Room } } },
                "ListParticipants" => new { participants = new[] { new { identity = Room.CallerIdentity, state = "ACTIVE" }, new { identity = Room.CalleeIdentity, state = "JOINED" } } },
                _ => new { }
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
    [Theory] [InlineData(1)] [InlineData(2)]
    public async Task ParticipantTokensAreLibrarySignedFiniteRoomBoundAndLeastPrivilege(int type)
    {
        using var h = new Harness(); await h.Adapter.CreateOrGetRoomAsync(Room, default);
        var a = await h.Adapter.IssueCredentialAsync(Room, Room.CallerIdentity, (CallType)type, TimeSpan.FromMinutes(5), default);
        var b = await h.Adapter.IssueCredentialAsync(Room, Room.CalleeIdentity, (CallType)type, TimeSpan.FromMinutes(5), default);
        Assert.NotEqual(a.Token, b.Token); Assert.DoesNotContain(a.Token, a.ToString());
        var parameters = new TokenValidationParameters { IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(h.Secret)),
            ValidateIssuerSigningKey = true, ValidIssuer = h.Options.ApiKey, ValidateIssuer = true, ValidateAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256], LifetimeValidator = (nbf, exp, _, _) => nbf <= Now.UtcDateTime && exp == Now.AddMinutes(5).UtcDateTime };
        new JwtSecurityTokenHandler().ValidateToken(a.Token, parameters, out _);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(a.Token);
        Assert.Equal(Room.CallerIdentity, jwt.Subject); Assert.Equal(Now.AddMinutes(5), a.ExpiresAtUtc);
        var grant = JsonSerializer.SerializeToElement(jwt.Payload["video"]);
        Assert.Equal(Room.Room, grant.GetProperty("room").GetString()); Assert.False(grant.GetProperty("roomAdmin").GetBoolean());
        Assert.False(grant.GetProperty("canPublishData").GetBoolean()); Assert.False(grant.TryGetProperty("roomCreate", out _));
        Assert.Equal(type == 1 ? new[] { "microphone" } : new[] { "microphone", "camera" }, grant.GetProperty("canPublishSources").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2, h.Http.Requests.Single().Body.GetProperty("maxParticipants").GetInt32());
        await h.Adapter.CloseRoomAsync(Room, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.IssueCredentialAsync(Room, Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
    }
    [Fact]
    public async Task RoomManagementUsesDocumentedScopedTwirpMethodsAndReconcilesMediaActiveState()
    {
        using var h = new Harness(); await h.Adapter.CreateOrGetRoomAsync(Room, default);
        var state = await h.Adapter.InspectAsync(Room, default); Assert.True(state.RoomExists); Assert.Equal(Room.CallerIdentity, Assert.Single(state.ConnectedIdentities));
        await h.Adapter.RemoveParticipantAsync(Room, Room.CalleeIdentity, default); await h.Adapter.CloseRoomAsync(Room, default);
        Assert.Equal(new[] { "CreateRoom", "ListRooms", "ListParticipants", "RemoveParticipant", "DeleteRoom" }, h.Http.Requests.Select(value => value.Method));
        foreach (var request in h.Http.Requests)
        {
            var grant = JsonSerializer.SerializeToElement(request.Token.Payload["video"]);
            Assert.False(grant.TryGetProperty("roomJoin", out _));
            if (request.Method != "ListRooms") Assert.Equal(Room.Room, grant.GetProperty("room").GetString());
        }
    }
    [Theory] [InlineData("room_started")] [InlineData("participant_joined")] [InlineData("participant_left")] [InlineData("room_finished")]
    public async Task SupportedSignedWebhooksValidateRawPayloadBeforeDecoding(string kind)
    {
        using var h = new Harness(); var body = Body(kind); var authorization = Sign(h, body);
        var verified = await h.Adapter.VerifyAsync(body, authorization, default);
        Assert.NotNull(verified); Assert.Equal("provider-event", verified.EventId); Assert.Equal(Room.Room, verified.Room);
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body) + " ");
        Assert.Null(await h.Adapter.VerifyAsync(changed, authorization, default));
    }
    [Theory] [InlineData("signature")] [InlineData("issuer")] [InlineData("expired")] [InlineData("hash")] [InlineData("algorithm")] [InlineData("unsigned")]
    public async Task InvalidWebhookAuthenticationNeverProducesVerifiedEvent(string invalid)
    {
        using var h = new Harness(); var body = Body("participant_joined");
        var auth = Sign(h, body, invalid);
        Assert.Null(await h.Adapter.VerifyAsync(body, auth, default));
    }
    private static byte[] Body(string kind) => JsonSerializer.SerializeToUtf8Bytes(new { id = "provider-event", @event = kind,
        createdAt = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), room = new { name = Room.Room }, participant = new { identity = Room.CallerIdentity } });
    private static string Sign(Harness h, byte[] body, string? invalid = null)
    {
        var secret = invalid == "signature" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) : h.Secret;
        var jwt = new JwtSecurityToken(issuer: invalid == "issuer" ? "foreign" : h.Options.ApiKey,
            claims: [new Claim("sha256", invalid == "hash" ? Convert.ToBase64String(new byte[32]) : Convert.ToBase64String(SHA256.HashData(body)))],
            notBefore: Now.AddMinutes(-2).UtcDateTime, expires: (invalid == "expired" ? Now.AddMinutes(-1) : Now.AddMinutes(1)).UtcDateTime,
            signingCredentials: invalid == "unsigned" ? null : new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), invalid == "algorithm" ? SecurityAlgorithms.HmacSha512 : SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
    [Fact]
    public async Task CleanupAcceptsVerifiedTwirpAbsenceButNotAnArbitrary404()
    {
        using var h = new Harness(); await h.Adapter.CreateOrGetRoomAsync(Room, default);
        h.Http.ProxyNotFound = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.CloseRoomAsync(Room, default));
        h.Http.ProxyNotFound = false; h.Http.TwirpNotFound = true;
        await h.Adapter.CloseRoomAsync(Room, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.IssueCredentialAsync(Room, Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
    }
    [Fact]
    public async Task UnboundForeignRoomsProviderErrorsAndProductionConfigurationFailClosed()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.IssueCredentialAsync(Room, Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
        h.Http.WrongRoom = true; await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.CreateOrGetRoomAsync(Room, default));
        h.Http.Fail = true; var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.CreateOrGetRoomAsync(Room, default));
        Assert.DoesNotContain("secret", failure.Message);
        Assert.Throws<ArgumentException>(() => new LiveKitDevelopmentAdapter(h.Client, h.Options, new Environment { EnvironmentName = Environments.Production }));
        Assert.Throws<ArgumentException>(() => new LiveKitDevelopmentAdapter(h.Client, h.Options with { ApiEndpoint = new Uri("http://foreign.invalid") }, new Environment()));
    }
    [Fact]
    public async Task D12ProviderDeadlineCancelsStalledRequestAndAllowsRecovery()
    {
        using var h = new Harness();
        var adapter = new LiveKitDevelopmentAdapter(h.Client, h.Options with { RequestTimeout = TimeSpan.FromMilliseconds(50) }, new Environment(), new Clock());
        h.Http.Stall = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.CreateOrGetRoomAsync(Room, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.IssueCredentialAsync(Room, Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
        h.Http.Stall = false;
        await adapter.CreateOrGetRoomAsync(Room, default);
        Assert.NotNull(await adapter.IssueCredentialAsync(Room, Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
        await adapter.CloseRoomAsync(Room, default);
    }
    [Theory]
    [InlineData("Production", "true")]
    [InlineData("ChatRealtimeDevelopment", "false")]
    [InlineData("Development", "true")]
    public void D12ConfigurationRequiresExactEnvironmentAndExplicitOptIn(string environment, string enabled)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["LiveKitSandbox:Enabled"] = enabled }).Build();
        Assert.Throws<InvalidOperationException>(() => LiveKitSandboxConfiguration.Load(config, new Environment { EnvironmentName = environment }));
    }
    [Fact]
    public void D12ConfigurationValidatesSecretsAndEndpointsWithoutNetwork()
    {
        using var h = new Harness();
        var values = new Dictionary<string, string?> { ["LiveKitSandbox:Enabled"] = "true",
            ["LiveKitSandbox:ApiEndpoint"] = "https://synthetic.invalid", ["LiveKitSandbox:RtcEndpoint"] = "wss://synthetic.invalid",
            ["LiveKitSandbox:ApiKey"] = "synthetic", ["LiveKitSandbox:ApiSecret"] = h.Secret };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Equal(h.Secret, LiveKitSandboxConfiguration.Load(config, new Environment()).ApiSecret);
        Assert.DoesNotContain(h.Secret, LiveKitSandboxConfiguration.Load(config, new Environment()).ToString());
        values["LiveKitSandbox:ApiSecret"] = "";
        config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<ArgumentException>(() => LiveKitSandboxConfiguration.Load(config, new Environment()));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(31000)]
    public void D12ProviderDeadlineMustBeFiniteAndBounded(int milliseconds)
    {
        using var h = new Harness();
        Assert.Throws<ArgumentException>(() => new LiveKitDevelopmentAdapter(h.Client,
            h.Options with { RequestTimeout = TimeSpan.FromMilliseconds(milliseconds) }, new Environment()));
    }
    [Fact]
    public async Task D12CallerCancellationDoesNotCreateAProviderBinding()
    {
        using var h = new Harness();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Adapter.CreateOrGetRoomAsync(Room, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.IssueCredentialAsync(Room,
            Room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default));
    }
    [Theory]
    [InlineData("")] [InlineData("Bearer ")] [InlineData("Basic invalid")] [InlineData("not-a-token")]
    public async Task D13MissingOrMalformedAuthorizationFailsClosed(string authorization)
    {
        using var h = new Harness();
        Assert.Null(await h.Adapter.VerifyAsync(Body("participant_joined"), authorization, default));
    }
}
