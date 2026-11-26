using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Andril.Communication.Calls.Development;
using Microsoft.IdentityModel.Tokens;

namespace Andril.API.Development.Calls;

internal sealed record LiveKitDevelopmentOptions(Uri ApiEndpoint, Uri RtcEndpoint, string ApiKey, string ApiSecret, TimeSpan ServerTokenLifetime)
{
    public override string ToString() => nameof(LiveKitDevelopmentOptions);
}

/// <summary>Unregistered development Twirp/JWT candidate. Uses existing Microsoft token-library signing/validation,
/// not a community SDK or custom signing implementation. No implicit environment-secret loading or production route.</summary>
internal sealed class LiveKitDevelopmentAdapter : ICallMediaProvider, ICallWebhookVerifier
{
    private readonly HttpClient http;
    private readonly LiveKitDevelopmentOptions options;
    private readonly TimeProvider clock;
    private readonly SymmetricSecurityKey key;
    private readonly ConcurrentDictionary<string, CallProviderReference> rooms = new(StringComparer.Ordinal);
    internal LiveKitDevelopmentAdapter(HttpClient http, LiveKitDevelopmentOptions options, IHostEnvironment environment, TimeProvider? clock = null)
    {
        if (environment.EnvironmentName != "ChatRealtimeDevelopment" || !Endpoint(options.ApiEndpoint, "https", "http") ||
            !Endpoint(options.RtcEndpoint, "wss", "ws") || string.IsNullOrWhiteSpace(options.ApiKey) || Encoding.UTF8.GetByteCount(options.ApiSecret) < 32 ||
            options.ServerTokenLifetime < TimeSpan.FromSeconds(1) || options.ServerTokenLifetime == TimeSpan.MaxValue)
            throw new ArgumentException("LiveKit development configuration unavailable.");
        this.http = http; this.options = options; this.clock = clock ?? TimeProvider.System;
        key = new(Encoding.UTF8.GetBytes(options.ApiSecret));
    }
    private static bool Endpoint(Uri value, string secure, string local) => value.IsAbsoluteUri && string.IsNullOrEmpty(value.UserInfo) &&
        string.IsNullOrEmpty(value.Query) && string.IsNullOrEmpty(value.Fragment) && value.AbsolutePath == "/" &&
        (value.Scheme == secure || value.Scheme == local && value.IsLoopback);
    private static void Validate(CallProviderReference reference)
    {
        if (!Regex.IsMatch(reference.Room, "^cr_[0-9a-f]{32}$") || !Regex.IsMatch(reference.CallerIdentity, "^cp_[0-9a-f]{32}$") ||
            !Regex.IsMatch(reference.CalleeIdentity, "^cp_[0-9a-f]{32}$") || reference.CallerIdentity == reference.CalleeIdentity)
            throw new InvalidOperationException("Invalid provider binding.");
    }
    public async Task CreateOrGetRoomAsync(CallProviderReference reference, CancellationToken cancellationToken)
    {
        Validate(reference);
        if (rooms.TryGetValue(reference.Room, out var existing) && existing != reference) throw new InvalidOperationException("Provider room conflict.");
        var result = await RpcAsync("CreateRoom", new { name = reference.Room, maxParticipants = 2 },
            new() { ["roomCreate"] = true, ["room"] = reference.Room }, cancellationToken);
        if (!result.TryGetProperty("name", out var name) || name.GetString() != reference.Room) throw new InvalidOperationException("Provider room mismatch.");
        rooms[reference.Room] = reference;
    }
    public Task<CallCredential> IssueCredentialAsync(CallProviderReference reference, string identity, CallType type,
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Validate(reference);
        if (!rooms.TryGetValue(reference.Room, out var bound) || bound != reference || identity != reference.CallerIdentity && identity != reference.CalleeIdentity ||
            !Enum.IsDefined(type) || lifetime < TimeSpan.FromSeconds(1) || lifetime == TimeSpan.MaxValue)
            throw new InvalidOperationException("Provider credential unavailable.");
        var now = clock.GetUtcNow();
        var expires = DateTimeOffset.FromUnixTimeSeconds(now.Add(lifetime).ToUnixTimeSeconds());
        if (expires <= now) throw new InvalidOperationException("Finite expiry required.");
        var grant = new Dictionary<string, object> { ["room"] = reference.Room, ["roomJoin"] = true,
            ["canPublish"] = true, ["canSubscribe"] = true, ["canPublishData"] = false, ["roomAdmin"] = false,
            ["canPublishSources"] = type == CallType.Audio ? new[] { "microphone" } : new[] { "microphone", "camera" } };
        return Task.FromResult(new CallCredential(options.RtcEndpoint, reference.Room, identity, Token(identity, grant, now, expires), expires));
    }
    public async Task RemoveParticipantAsync(CallProviderReference reference, string identity, CancellationToken cancellationToken)
    {
        Validate(reference);
        if (identity != reference.CallerIdentity && identity != reference.CalleeIdentity) throw new InvalidOperationException("Provider identity mismatch.");
        await RpcAsync("RemoveParticipant", new { room = reference.Room, identity }, new() { ["roomAdmin"] = true, ["room"] = reference.Room }, cancellationToken, allowNotFound: true);
    }
    public async Task CloseRoomAsync(CallProviderReference reference, CancellationToken cancellationToken)
    {
        Validate(reference);
        await RpcAsync("DeleteRoom", new { room = reference.Room }, new() { ["roomCreate"] = true, ["room"] = reference.Room }, cancellationToken, allowNotFound: true);
        rooms.TryRemove(reference.Room, out _);
    }
    public async Task<CallProviderState> InspectAsync(CallProviderReference reference, CancellationToken cancellationToken)
    {
        Validate(reference);
        var response = await RpcAsync("ListRooms", new { names = new[] { reference.Room } }, new() { ["roomList"] = true }, cancellationToken);
        if (!response.TryGetProperty("rooms", out var listed) || !listed.EnumerateArray().Any(room => room.GetProperty("name").GetString() == reference.Room))
            return new(false, new HashSet<string>());
        var participants = await RpcAsync("ListParticipants", new { room = reference.Room }, new() { ["roomAdmin"] = true, ["room"] = reference.Room }, cancellationToken);
        // LiveKit participant state ACTIVE = 2; a signaling-only JOINING/JOINED participant is not media-active.
        var identities = new HashSet<string>(StringComparer.Ordinal);
        if (participants.TryGetProperty("participants", out var values))
            foreach (var value in values.EnumerateArray())
                if (value.TryGetProperty("state", out var state) && (state.ValueKind == JsonValueKind.Number && state.GetInt32() == 2 || state.ValueKind == JsonValueKind.String && state.GetString() == "ACTIVE"))
                    if (value.GetProperty("identity").GetString() is { } identity) identities.Add(identity);
        return new(true, identities);
    }
    private async Task<JsonElement> RpcAsync<T>(string method, T body, Dictionary<string, object> grant, CancellationToken token, bool allowNotFound = false)
    {
        var now = clock.GetUtcNow();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiEndpoint, "twirp/livekit.RoomService/" + method));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(null, grant, now, now.Add(options.ServerTokenLifetime)));
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.GetString() == "not_found")
                    return JsonSerializer.SerializeToElement(new { });
            }
            catch (JsonException) { }
            // A proxy/misconfigured-route 404 is not evidence that the room or participant is absent.
            throw new InvalidOperationException("LiveKit operation unavailable.");
        }
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("LiveKit operation unavailable.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
    }
    private string Token(string? identity, Dictionary<string, object> grant, DateTimeOffset now, DateTimeOffset expires)
    {
        var value = new JwtSecurityToken(issuer: options.ApiKey, notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        if (identity is not null) value.Payload[JwtRegisteredClaimNames.Sub] = identity;
        value.Payload[JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds(); value.Payload["video"] = grant;
        return new JwtSecurityTokenHandler().WriteToken(value);
    }
    public Task<VerifiedCallProviderEvent?> VerifyAsync(ReadOnlyMemory<byte> rawBody, string authorization, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : authorization;
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var parameters = new TokenValidationParameters { ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                ValidIssuer = options.ApiKey, ValidateIssuer = true, ValidateAudience = false, RequireSignedTokens = true,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                LifetimeValidator = (nbf, exp, _, _) => nbf.HasValue && exp.HasValue && nbf <= clock.GetUtcNow().UtcDateTime && exp > clock.GetUtcNow().UtcDateTime };
            var principal = handler.ValidateToken(token, parameters, out _);
            var hashes = principal.FindAll("sha256").ToArray();
            if (hashes.Length != 1 || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(hashes[0].Value), SHA256.HashData(rawBody.Span)))
                return Task.FromResult<VerifiedCallProviderEvent?>(null);
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var kind = root.GetProperty("event").GetString() switch
            {
                "room_started" => CallProviderEventKind.RoomStarted, "participant_joined" => CallProviderEventKind.ParticipantJoined,
                "participant_left" => CallProviderEventKind.ParticipantLeft, "room_finished" => CallProviderEventKind.RoomEnded,
                _ => (CallProviderEventKind)(-1)
            };
            if (!Enum.IsDefined(kind)) return Task.FromResult<VerifiedCallProviderEvent?>(null);
            var created = root.GetProperty("createdAt");
            var seconds = created.ValueKind == JsonValueKind.String ? long.Parse(created.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : created.GetInt64();
            var result = new VerifiedCallProviderEvent(root.GetProperty("id").GetString()!, Convert.ToHexString(SHA256.HashData(rawBody.Span)), kind,
                root.GetProperty("room").GetProperty("name").GetString()!, root.TryGetProperty("participant", out var participant) ? participant.GetProperty("identity").GetString() : null,
                DateTimeOffset.FromUnixTimeSeconds(seconds));
            return Task.FromResult<VerifiedCallProviderEvent?>(result);
        }
        catch (Exception) { return Task.FromResult<VerifiedCallProviderEvent?>(null); }
    }
}
