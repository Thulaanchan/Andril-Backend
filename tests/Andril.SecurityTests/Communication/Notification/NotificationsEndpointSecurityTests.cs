
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts;
using Andril.SecurityTests.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Communication.Notifications;

public sealed class NotificationsEndpointSecurityTests
{
    private const string NotificationId =
        "11111111-1111-1111-1111-111111111111";

    // SECTION A:
    // Missing and invalid JWT authentication.

    [Theory]
    [InlineData("GET", "/api/v1/notifications/learning", "missing")]
    [InlineData("GET", "/api/v1/notifications/learning", "malformed")]
    [InlineData("GET", "/api/v1/notifications/learning", "invalid-signature")]
    [InlineData("GET", "/api/v1/notifications/hobbies", "missing")]
    [InlineData("GET", "/api/v1/notifications/hobbies", "malformed")]
    [InlineData("GET", "/api/v1/notifications/hobbies", "invalid-signature")]
    [InlineData("GET", "/api/v1/notifications/social", "missing")]
    [InlineData("GET", "/api/v1/notifications/social", "malformed")]
    [InlineData("GET", "/api/v1/notifications/social", "invalid-signature")]
    public async Task List_RejectsMissingAndInvalidAuthentication(
        string method,
        string path,
        string tokenCase)
    {
        await AssertUnauthorizedAsync(method, path, tokenCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("invalid-signature")]
    public async Task Details_RejectsMissingAndInvalidAuthentication(
        string tokenCase)
    {
        await AssertUnauthorizedAsync(
            "GET",
            $"/api/v1/notifications/learning/{NotificationId}",
            tokenCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("invalid-signature")]
    public async Task MarkRead_RejectsMissingAndInvalidAuthentication(
        string tokenCase)
    {
        await AssertUnauthorizedAsync(
            "PATCH",
            $"/api/v1/notifications/learning/{NotificationId}/read",
            tokenCase);
    }

    private static async Task AssertUnauthorizedAsync(
        string method,
        string path,
        string tokenCase)
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();

        string? token = tokenCase switch
        {
            "missing" => null,
            "malformed" => "not-a-jwt",
            "invalid-signature" => factory.Token("signature"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(tokenCase))
        };

        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            path);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    // SECTION B:
    // Fail-closed behavior without a confirmed
    // identity context.
    //
    // This is NOT a full unconfirmed-user test.
    // M2 identity middleware integration remains pending.

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task MissingConfirmedIdentityScope_ShouldDenyBeforeAccountRead(
        string module)
    {
        var fakeAccounts = new FakeAccountService();

        using var baseFactory = new AuthenticationFactory();

        using var factory = baseFactory.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(
                services =>
                {
                    services.Replace(
                        ServiceDescriptor.Scoped<IAccountService>(
                            _ => fakeAccounts));
                }));

        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                baseFactory.Token());

        using var response = await client.GetAsync(
            $"/api/v1/notifications/{module}");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            response.StatusCode);

        // Account data must not be read
        // when confirmation context is missing.
        Assert.Equal(0, fakeAccounts.Calls);
    }

    // SECTION C:
    // Test-only account dependency.
    // No real PostgreSQL connection.

    private sealed class FakeAccountService : IAccountService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<AccountDto?> GetMeAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult<AccountDto?>(null);
        }

        public Task<AccountDto> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Account initialization is not allowed in this test.");
        }
    }
}
