using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Andril.API.Authentication;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SocialIdentityBoundaryTests
{
    [Fact]
    public async Task SocialRoute_WithoutAuthenticationHeader_ReturnsUnauthorized()
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/social/questionnaires/active");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task SocialRoute_WithMalformedToken_ReturnsUnauthorized()
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "malformed.jwt.payload");

        var response = await client.GetAsync("/api/v1/social/questionnaires/active");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SocialRoute_WithUnconfirmedUser_ReturnsForbiddenWithNoStore()
    {
        using var factory = new AuthenticationFactory(identityHandler: new UnconfirmedUserHandler());
        using var client = factory.CreateClient();
        var token = factory.Token();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/social/questionnaires/active");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SocialRoute_WhenVerifierUnavailable_ReturnsServiceUnavailable()
    {
        using var factory = new AuthenticationFactory(identityHandler: new UnavailableHandler());
        using var client = factory.CreateClient();
        var token = factory.Token();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/v1/social/questionnaires/active");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/v1/socialfake")]
    [InlineData("/api/v1/social-fake")]
    [InlineData("/health")]
    public async Task UnprotectedRoutes_DoNotTriggerConfirmedIdentityMiddleware(string path)
    {
        using var factory = new AuthenticationFactory(identityHandler: new UnconfirmedUserHandler());
        using var client = factory.CreateClient();
        var token = factory.Token();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(path);
        // Lookalike routes are not matched by IdentityProtectedRoutes, so they do not return 403 (Email confirmation required)
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PublicHealthEndpoint_RemainsOpenWithoutAuthentication()
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class UnconfirmedUserHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = AuthenticationFactory.Subject,
                    aud = "authenticated",
                    role = "authenticated",
                    is_anonymous = false,
                    email = "unconfirmed@example.test",
                    email_confirmed_at = (string?)null
                })
            });
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Test identity verifier network failure."));
    }
}
