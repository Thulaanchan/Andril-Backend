using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Andril.API.Authentication;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SupabaseAuthenticationTests
{
    private const string EventLogProviderName = "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider";

    [Fact]
    public void LoggingRegression_EventLogFilterIsProviderSpecificAndDoesNotChangeProductionHost()
    {
        // Use the real API entry point without AuthenticationFactory overrides. No requests,
        // credentials, external services or database operations are needed for this comparison.
        using var production = new WebApplicationFactory<Program>();
        var productionRules = production.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        var originalRules = productionRules.ToArray();
        using var authentication = new AuthenticationFactory();
        var testRules = authentication.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        var eventLogFilters = testRules.Where(rule => rule.ProviderName == EventLogProviderName && rule.Filter is not null).ToArray();
        var productionEventLogFilters = originalRules.Where(rule => rule.ProviderName == EventLogProviderName && rule.Filter is not null).ToArray();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(productionEventLogFilters.Length + 1, eventLogFilters.Length);
            var rule = Assert.Single(eventLogFilters, candidate =>
                !candidate.Filter!(EventLogProviderName, "Microsoft.AspNetCore.Diagnostics", LogLevel.Critical));
            Assert.Null(rule.CategoryName);
            foreach (var level in Enum.GetValues<LogLevel>().Where(level => level != LogLevel.None))
                Assert.False(rule.Filter!(EventLogProviderName, "Microsoft.AspNetCore.Diagnostics", level));
        }
        else
        {
            Assert.Equal(productionEventLogFilters.Length, eventLogFilters.Length);
        }

        Assert.DoesNotContain(productionEventLogFilters, rule =>
            !rule.Filter!(EventLogProviderName, "Microsoft.AspNetCore.Diagnostics", LogLevel.Critical));
        Assert.Equal(originalRules, productionRules.ToArray());
        Assert.NotSame(productionRules, testRules);
        Assert.DoesNotContain(testRules, rule => rule.ProviderName is null && rule.Filter is not null);
    }

    [Fact]
    public void LoggingRegression_OtherProviderStillReceivesDiagnosticEvents()
    {
        using var capture = new DiagnosticCapture();
        using var authentication = new AuthenticationFactory();
        using var host = authentication.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(capture)));
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("M1.LoggingRegression");
        logger.LogWarning(new EventId(7101, "SyntheticDiagnostic"), "Synthetic diagnostic event");

        var entry = Assert.Single(capture.Snapshot(), entry => entry.EventId.Id == 7101);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("M1.LoggingRegression", entry.Category);
        Assert.Equal("Synthetic diagnostic event", entry.Message);
        Assert.Equal(0, capture.Dropped);
    }

    [Fact]
    public async Task LoggingRegression_PrivateRequestAndBearerAreNotExposedInDiagnosticsOrResponse()
    {
        const string privateMarker = "synthetic-private-profile-payload-7102";
        using var capture = new DiagnosticCapture();
        var profiles = new UnexpectedProfileCalls();
        using var authentication = new AuthenticationFactory();
        using var host = authentication.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(capture));
            builder.ConfigureTestServices(services => services.AddSingleton<IPrivateProfileService>(profiles));
        });
        using var client = host.CreateClient();
        var token = authentication.Token();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await client.PostAsJsonAsync("/api/v1/account/profiles/learning", new { accountId = privateMarker });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, profiles.Calls);

        // Prove capture is live before inspecting it; never print captured content on failure.
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("M1.LoggingRegression")
            .LogWarning(new EventId(7102, "CaptureVerified"), "Synthetic capture verification");
        var entries = capture.Snapshot();
        Assert.Contains(entries, entry => entry.EventId.Id == 7102);
        Assert.Equal(0, capture.Dropped);
        var diagnostics = string.Join("\n", entries.Select(entry => entry.Category + "\n" + entry.Message + "\n" + entry.Exception));
        var body = await response.Content.ReadAsStringAsync();
        foreach (var sensitive in new[] { privateMarker, token, AuthenticationFactory.Subject.ToString() })
        {
            Assert.True(!diagnostics.Contains(sensitive, StringComparison.Ordinal), "Private data appeared in captured diagnostics.");
            Assert.True(!body.Contains(sensitive, StringComparison.Ordinal), "Private data appeared in the HTTP error response.");
        }
    }

    // Bounded, thread-safe, per-host capture. Nothing is written to test output or disk.
    private sealed class DiagnosticCapture : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly Queue<Entry> entries = new();
        private int dropped;
        internal int Dropped { get { lock (gate) return dropped; } }
        internal Entry[] Snapshot() { lock (gate) return entries.ToArray(); }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void Dispose() { lock (gate) entries.Clear(); }

        private void Add(Entry entry)
        {
            lock (gate)
            {
                if (entries.Count == 512) { dropped++; return; }
                entries.Enqueue(entry);
            }
        }

        internal sealed record Entry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)
        {
            public override string ToString() => "Captured diagnostic [content omitted]";
        }

        private sealed class CaptureLogger(DiagnosticCapture capture, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) capture.Add(new(category, logLevel, eventId, formatter(state, exception), exception));
            }
        }
    }

    private sealed class UnexpectedProfileCalls : IPrivateProfileService
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => Unexpected();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => Unexpected();
        private Task<PrivateProfileDto> Unexpected()
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("Invalid request must not reach profile operations.");
        }
    }

    [Fact]
    public async Task Health_IsPublic_AndProtectedRouteRequiresAuthentication()
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var response = await client.GetAsync("/auth-test/identity");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ES256")]
    [InlineData("RS256")]
    public async Task ValidToken_ExposesOnlyVerifiedSubject(string algorithm)
    {
        using var factory = new AuthenticationFactory(algorithm);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var response = await client.GetAsync("/auth-test/identity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AuthenticationFactory.Subject, await response.Content.ReadFromJsonAsync<Guid>());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("audience-slash")]
    [InlineData("missing-subject")]
    [InlineData("invalid-subject")]
    [InlineData("empty-subject")]
    [InlineData("service-role")]
    [InlineData("anonymous")]
    [InlineData("missing-anonymous")]
    [InlineData("no-expiry")]
    [InlineData("unsigned")]
    [InlineData("hs256")]
    [InlineData("wrong-algorithm")]
    public async Task InvalidTokens_AreRejectedWithoutDetails(string fault)
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(fault));
        var response = await client.GetAsync("/auth-test/identity");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AuthenticatedUserWithoutRequiredRole_GetsForbidden()
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var response = await client.GetAsync("/auth-test/restricted");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("HS256")]
    [InlineData("unknown")]
    public async Task UnconfirmedSigningConfiguration_FailsClosed_ButHealthWorks(string algorithm)
    {
        using var factory = new AuthenticationFactory(algorithm);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth-test/identity")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.supabase.co")]
    [InlineData("https://example.supabase.co.evil.test")]
    [InlineData("https://example.supabase.co/auth/v1")]
    [InlineData("https://user@example.supabase.co")]
    [InlineData("https://example.supabase.co?redirect=evil")]
    [InlineData("https://localhost")]
    public void UnsafeProjectUrls_AreRejected(string url)
    {
        Assert.False(new SupabaseAuthOptions { ProjectUrl = url, SigningAlgorithm = "ES256" }
            .TryGetIssuer(out _));
    }

    [Fact]
    public async Task JwksFailure_RejectsToken()
    {
        using var factory = new AuthenticationFactory(unavailableKeys: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/auth-test/identity")).StatusCode);
    }

    [Fact]
    public void CurrentUser_DoesNotTrustUnauthenticatedClaims()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", AuthenticationFactory.Subject.ToString()),
                new Claim("role", "authenticated"), new Claim("is_anonymous", "false")
            }))
        };
        var user = new HttpCurrentUser(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = context });
        Assert.False(user.IsAuthenticated);
        Assert.Null(user.UserId);
    }
}

// These routes are registered only by the test host, never by the production API.
[ApiController]
[Route("auth-test")]
public sealed class AuthenticationProbeController : ControllerBase
{
    [HttpGet("identity")]
    public IActionResult Identity([FromServices] ICurrentUser user) => Ok(user.UserId);

    [HttpGet("restricted")]
    [Authorize(Roles = "administrator")]
    public IActionResult Restricted() => Ok();
}

internal sealed class AuthenticationFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid Subject = Guid.Parse("7a87c427-8db1-4b93-b94b-dbb59495bc60");
    private const string Issuer = "https://andril-test.supabase.co/auth/v1";
    private readonly ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly RSA rsa = RSA.Create(2048);
    private readonly string algorithm;
    private readonly bool unavailableKeys;
    private readonly HttpMessageHandler? identityHandler;

    internal AuthenticationFactory(string algorithm = "ES256", bool unavailableKeys = false, HttpMessageHandler? identityHandler = null)
    {
        this.algorithm = algorithm;
        this.unavailableKeys = unavailableKeys;
        this.identityHandler = identityHandler;
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Test hosts must not depend on Windows EventLog permissions. Keep every other
        // provider and its diagnostic output, including framework exception logging.
        builder.ConfigureLogging(logging =>
        {
            if (OperatingSystem.IsWindows())
                logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(static (_, _) => false);
        });
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:ProjectUrl"] = "https://andril-test.supabase.co",
            ["SupabaseAuth:SigningAlgorithm"] = algorithm,
            ["SupabaseAuth:Audience"] = "authenticated",
            ["SupabaseIdentity:PublishableKey"] = "sb_publishable_synthetic_test_fixture"
        }));
        builder.ConfigureTestServices(services =>
        {
            // Exercise real Feature 07 verification against synthetic HTTP evidence; never contact Supabase.
            services.AddHttpClient<ISupabaseUserVerifier, SupabaseUserVerifier>()
                .ConfigurePrimaryHttpMessageHandler(() => identityHandler ?? new ConfirmedUserHandler());
            services.AddControllers().AddApplicationPart(typeof(AuthenticationProbeController).Assembly);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                if (algorithm is not ("ES256" or "RS256")) return;
                SecurityKey publicKey = algorithm == "RS256"
                    ? new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "test-key" }
                    : new ECDsaSecurityKey(ECDsa.Create(ec.ExportParameters(false))) { KeyId = "test-key" };
                var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(publicKey);
                jwk.Alg = algorithm;
                jwk.Use = "sig";
                var document = System.Text.Json.JsonSerializer.Serialize(new { keys = new[] { jwk } });
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    Issuer + "/.well-known/jwks.json", new SupabaseJwksRetriever(Issuer, algorithm),
                    new TestDocumentRetriever(document, unavailableKeys)) { UseLastKnownGoodConfiguration = false };
            });
        });
    }

    internal string Token(string fault = "", Guid? subject = null, IEnumerable<Claim>? extraClaims = null)
    {
        if (fault == "malformed") return "not-a-jwt";
        SecurityKey key = algorithm == "RS256" || fault == "wrong-algorithm"
            ? new RsaSecurityKey(rsa) { KeyId = "test-key" }
            : new ECDsaSecurityKey(ec) { KeyId = "test-key" };
        var signingAlgorithm = key is RsaSecurityKey ? "RS256" : "ES256";
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (fault == "signature") key = new ECDsaSecurityKey(other) { KeyId = "test-key" };
        if (fault == "hs256")
        {
            key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" };
            signingAlgorithm = "HS256";
        }
        var claims = new List<Claim>
        {
            new("role", fault == "service-role" ? "service_role" : "authenticated")
        };
        if (fault != "missing-subject")
            claims.Add(new("sub", fault == "invalid-subject" ? "invalid" :
                fault == "empty-subject" ? Guid.Empty.ToString() : (subject ?? Subject).ToString()));
        if (fault != "missing-anonymous")
            claims.Add(new("is_anonymous", fault == "anonymous" ? "true" : "false", ClaimValueTypes.Boolean));
        if (extraClaims is not null) claims.AddRange(extraClaims);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            fault == "issuer" ? "https://another.supabase.co/auth/v1" : Issuer,
            fault == "audience" ? "other-api" : fault == "audience-slash" ? "authenticated/" : "authenticated",
            claims, fault == "future" ? now.AddMinutes(5) : now.AddMinutes(-10),
            fault == "no-expiry" ? null : fault == "expired" ? now.AddMinutes(-1) : now.AddMinutes(10),
            fault == "unsigned" ? null : new SigningCredentials(key, signingAlgorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { ec.Dispose(); rsa.Dispose(); }
    }

    private sealed class TestDocumentRetriever(string document, bool unavailable) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            Assert.Equal(Issuer + "/.well-known/jwks.json", address);
            if (unavailable) throw new HttpRequestException("Test key service unavailable.");
            return Task.FromResult(document);
        }
    }

    private sealed class ConfirmedUserHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = Subject, aud = "authenticated", role = "authenticated", is_anonymous = false,
                    email = "synthetic@example.test", email_confirmed_at = "2020-01-01T00:00:00Z" })
            });
    }
}
