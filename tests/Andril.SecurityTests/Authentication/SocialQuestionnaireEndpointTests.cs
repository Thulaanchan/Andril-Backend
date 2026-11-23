using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Andril.API.Authorization.Social;
using Andril.Common.Accounts.Profiles;
using Andril.Social.DTOs;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SocialQuestionnaireEndpointTests
{
    private const string Route = "/api/v1/social/questionnaires/active";

    [Fact]
    public async Task GetActiveDefinition_WhenEligibleAndAuthenticated_ReturnsV1DefinitionWithNoStore()
    {
        using var baseFactory = new AuthenticationFactory();
        using var app = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrivateProfileService>();
                services.AddScoped<IPrivateProfileService>(_ => new StubSocialProfileService());
            }));

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", baseFactory.Token());

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var dto = await response.Content.ReadFromJsonAsync<QuestionnaireDefinitionDto>();
        Assert.NotNull(dto);
        Assert.Equal("v1", dto.Version);
        Assert.Equal(12, dto.Questions.Count);

        // Security check: verify no private profile IDs, account IDs, raw responses, or match scores
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accountId", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profileId", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("answers", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetActiveDefinition_WithoutToken_ReturnsUnauthorized()
    {
        using var baseFactory = new AuthenticationFactory();
        using var client = baseFactory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task GetActiveDefinition_WithMalformedToken_ReturnsUnauthorized()
    {
        using var baseFactory = new AuthenticationFactory();
        using var client = baseFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "malformed.jwt.token");

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetActiveDefinition_WhenAuthorizationFails_FailsClosed()
    {
        using var baseFactory = new AuthenticationFactory();
        using var app = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrivateProfileService>();
                services.AddScoped<IPrivateProfileService>(_ => new ThrowingProfileService(new ProfileAccessDeniedException()));
            }));

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", baseFactory.Token());

        var response = await client.GetAsync(Route);

        Assert.True(response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.Forbidden,
            $"Expected denial status code, got {response.StatusCode}");
    }

    [Fact]
    public async Task GetActiveDefinition_WhenProfileMissing_FailsClosed()
    {
        using var baseFactory = new AuthenticationFactory();
        using var app = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPrivateProfileService>();
                services.AddScoped<IPrivateProfileService>(_ => new ThrowingProfileService(new ProfileMissingException()));
            }));

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", baseFactory.Token());

        var response = await client.GetAsync(Route);

        Assert.True(response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.NotFound,
            $"Expected denial status code, got {response.StatusCode}");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task QuestionnaireActiveEndpoint_RejectsNonGetMethods(string method)
    {
        using var baseFactory = new AuthenticationFactory();
        using var client = baseFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", baseFactory.Token());

        var request = new HttpRequestMessage(new HttpMethod(method), Route);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private sealed class StubSocialProfileService : IPrivateProfileService
    {
        private readonly PrivateProfileDto _profile = new(Guid.NewGuid(), "social", DateTimeOffset.UtcNow)
        {
            DisplayName = "ValidUser"
        };

        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken)
        {
            if (!string.Equals(module, "social", StringComparison.OrdinalIgnoreCase))
                throw new ProfileAccessDeniedException();

            return action(_profile);
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class ThrowingProfileService(Exception exception) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken) =>
            Task.FromException<T>(exception);

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromException<PrivateProfileDto>(exception);

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromException<PrivateProfileDto>(exception);
    }
}
