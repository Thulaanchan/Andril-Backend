using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Andril.API.Authentication;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SupabaseAuthenticationTests
{
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

    internal AuthenticationFactory(string algorithm = "ES256", bool unavailableKeys = false)
    {
        this.algorithm = algorithm;
        this.unavailableKeys = unavailableKeys;
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SupabaseAuth:ProjectUrl"] = "https://andril-test.supabase.co",
            ["SupabaseAuth:SigningAlgorithm"] = algorithm,
            ["SupabaseAuth:Audience"] = "authenticated"
        }));
        builder.ConfigureTestServices(services =>
        {
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

    internal string Token(string fault = "")
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
                fault == "empty-subject" ? Guid.Empty.ToString() : Subject.ToString()));
        if (fault != "missing-anonymous")
            claims.Add(new("is_anonymous", fault == "anonymous" ? "true" : "false", ClaimValueTypes.Boolean));
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
}
