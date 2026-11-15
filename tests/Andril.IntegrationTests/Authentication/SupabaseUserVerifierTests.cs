using System.Net;
using System.Text;
using System.Text.Json;
using Andril.API.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andril.IntegrationTests.Authentication;

// HTTP contract tests use synthetic responses only; no user, key or remote connection is real.
public sealed class SupabaseUserVerifierTests
{
    private static readonly Guid Subject = Guid.Parse("917d7ec0-7143-4824-a358-4f3b8b481100");
    private static Dictionary<string, object?> User() => new()
    {
        ["id"] = Subject, ["aud"] = "authenticated", ["role"] = "authenticated", ["is_anonymous"] = false,
        ["email"] = "fixture@example.test", ["email_confirmed_at"] = "2020-01-01T00:00:00Z"
    };
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["SupabaseAuth:ProjectUrl"] = "https://andril-test.supabase.co",
        ["SupabaseAuth:SigningAlgorithm"] = "ES256",
        ["SupabaseIdentity:PublishableKey"] = "sb_publishable_synthetic_test_fixture"
    }).Build();

    [Theory]
    [InlineData("email")]
    [InlineData("google")]
    public async Task SameConfirmedSubjectAcceptedRegardlessOfProvider(string provider)
    {
        var user = User();
        user["app_metadata"] = new { provider };
        var handler = new Handler(JsonSerializer.Serialize(user));
        using var http = new HttpClient(handler);
        Assert.Equal(SupabaseUserVerification.Confirmed, await new SupabaseUserVerifier(http, Config(), TimeProvider.System)
            .VerifyAsync(Subject, "synthetic-access-token", default));
        Assert.Equal("https://andril-test.supabase.co/auth/v1/user", handler.Url);
        Assert.Equal("Bearer synthetic-access-token", handler.Authorization);
        Assert.Equal("sb_publishable_synthetic_test_fixture", handler.ApiKey);
    }

    [Theory]
    [InlineData("id", SupabaseUserVerification.Unauthorized)]
    [InlineData("is_anonymous", SupabaseUserVerification.Unauthorized)]
    [InlineData("role", SupabaseUserVerification.Unauthorized)]
    [InlineData("aud", SupabaseUserVerification.Unauthorized)]
    [InlineData("email", SupabaseUserVerification.Unconfirmed)]
    [InlineData("email_confirmed_at", SupabaseUserVerification.Unconfirmed)]
    public async Task MissingTopLevelEvidenceFailsClosed_EvenWithForgedMetadata(string missing, SupabaseUserVerification expected)
    {
        var user = User();
        user.Remove(missing);
        user["confirmed_at"] = "2020-01-01T00:00:00Z"; // Phone confirmation is insufficient.
        user["user_metadata"] = new { email_verified = true, verified = true, email_confirmed_at = "2020-01-01T00:00:00Z", provider = "google" };
        using var http = new HttpClient(new Handler(JsonSerializer.Serialize(user)));
        Assert.Equal(expected, await new SupabaseUserVerifier(http, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
    }

    [Theory]
    [InlineData("id", "00000000-0000-0000-0000-000000000001", SupabaseUserVerification.Unauthorized)]
    [InlineData("role", "service_role", SupabaseUserVerification.Unauthorized)]
    [InlineData("email", "", SupabaseUserVerification.Unconfirmed)]
    [InlineData("email_confirmed_at", null, SupabaseUserVerification.Unconfirmed)]
    [InlineData("email_confirmed_at", "9999-01-01T00:00:00Z", SupabaseUserVerification.Unconfirmed)]
    [InlineData("email_confirmed_at", "true", SupabaseUserVerification.Unavailable)]
    public async Task InvalidOrMismatchedEvidenceRejected(string field, string? value, SupabaseUserVerification expected)
    {
        var user = User(); user[field] = value;
        using var http = new HttpClient(new Handler(JsonSerializer.Serialize(user)));
        Assert.Equal(expected, await new SupabaseUserVerifier(http, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
    }

    [Theory]
    [InlineData(401, SupabaseUserVerification.Unauthorized)]
    [InlineData(403, SupabaseUserVerification.Unauthorized)]
    [InlineData(404, SupabaseUserVerification.Unauthorized)]
    [InlineData(429, SupabaseUserVerification.Unavailable)]
    [InlineData(500, SupabaseUserVerification.Unavailable)]
    [InlineData(302, SupabaseUserVerification.Unavailable)]
    public async Task UpstreamFailuresNeverReturnConfirmation(int code, SupabaseUserVerification expected)
    {
        var handler = new Handler("private upstream failure", (HttpStatusCode)code);
        using var http = new HttpClient(handler);
        Assert.Equal(expected, await new SupabaseUserVerifier(http, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"id\":\"first\",\"id\":\"second\"}")]
    public async Task MalformedOrAmbiguousResponseNeverPasses(string body)
    {
        using var http = new HttpClient(new Handler(body));
        Assert.NotEqual(SupabaseUserVerification.Confirmed,
            await new SupabaseUserVerifier(http, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
    }

    [Theory]
    [InlineData("SupabaseIdentity:PublishableKey", "")]
    [InlineData("SupabaseIdentity:PublishableKey", "sb_secret_not_allowed")]
    [InlineData("SupabaseAuth:ProjectUrl", "https://attacker.test")]
    [InlineData("SupabaseAuth:ProjectUrl", "https://andril-test.supabase.co/path")]
    public async Task InvalidConfigurationMakesNoHttpRequest(string key, string value)
    {
        var config = Config(); config[key] = value;
        var handler = new Handler(JsonSerializer.Serialize(User()));
        using var http = new HttpClient(handler);
        Assert.Equal(SupabaseUserVerification.Unavailable,
            await new SupabaseUserVerifier(http, config, TimeProvider.System).VerifyAsync(Subject, "fixture", default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkTimeoutAndOversizeFailuresFailClosed()
    {
        foreach (var fault in new Exception[] { new HttpRequestException("private details"), new TaskCanceledException() })
        {
            using var http = new HttpClient(new Handler("", fault: fault));
            Assert.Equal(SupabaseUserVerification.Unavailable,
                await new SupabaseUserVerifier(http, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
        }
        using var oversized = new HttpClient(new Handler(new string('x', 65537))) { MaxResponseContentBufferSize = 65536 };
        Assert.Equal(SupabaseUserVerification.Unavailable,
            await new SupabaseUserVerifier(oversized, Config(), TimeProvider.System).VerifyAsync(Subject, "fixture", default));
    }
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK, Exception? fault = null) : HttpMessageHandler
    {
        internal string? Url, Authorization, ApiKey;
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.GetValues("apikey").Single();
            if (fault is not null) return Task.FromException<HttpResponseMessage>(fault);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
