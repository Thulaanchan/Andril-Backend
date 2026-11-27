
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.API.Authentication;
using Andril.API.Controllers.Common;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.SecurityTests.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
    // This is NOT a full unconfirmed-user test. Notification
    // routes do not establish the confirmed identity feature.

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

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task Details_MissingConfirmedIdentityScope_ShouldDenyBeforeProtectedOperations(
        string module)
    {
        await AssertMissingConfirmedIdentityAsync("GET", module);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task MarkRead_MissingConfirmedIdentityScope_ShouldDenyWithoutReadStateMutation(
        string module)
    {
        await AssertMissingConfirmedIdentityAsync("PATCH", module);
    }

    private static async Task AssertMissingConfirmedIdentityAsync(
        string method,
        string module)
    {
        var accounts = new FakeAccountService();
        var operations = new NotificationOperationProbe(module);
        var identity = new NotificationIdentityProbe();
        using var baseFactory = new AuthenticationFactory();
        using var factory = baseFactory.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Scoped<IAccountService>(_ => accounts));
                services.Replace(ServiceDescriptor.Scoped<INotificationRecipientAccessReader>(_ => operations));
                services.Replace(ServiceDescriptor.Scoped<INotificationRepository>(_ => operations));
                services.Configure<MvcOptions>(options => options.Filters.Add(identity));
            }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", baseFactory.Token());

        Assert.False(operations.Entry.IsRead);
        Assert.Null(operations.Entry.ReadAtUtc);
        var path = $"/api/v1/notifications/{module}/{NotificationId}";
        if (method == "PATCH")
            path += "/read";

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, identity.Calls);
        Assert.Equal(0, accounts.Calls);
        Assert.Equal(0, operations.AccessCalls);
        Assert.Equal(0, operations.RepositoryCalls);
        Assert.False(operations.Entry.IsRead);
        Assert.Null(operations.Entry.ReadAtUtc);
    }

    // Observe the real request without replacing authentication or identity.
    private sealed class NotificationIdentityProbe : IActionFilter
    {
        public int Calls { get; private set; }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            Assert.IsType<NotificationsController>(context.Controller);
            Assert.True(context.HttpContext.User.Identity?.IsAuthenticated);
            Assert.Equal(AuthenticationFactory.Subject.ToString(),
                Assert.Single(context.HttpContext.User.FindAll("sub")).Value);
            var identity = context.HttpContext.RequestServices
                .GetRequiredService<IOnboardingIdentity>();
            Assert.IsType<OnboardingIdentity>(identity);
            Assert.False(identity.IsConfirmedFor(AuthenticationFactory.Subject));
            Assert.DoesNotContain(context.HttpContext.Features,
                feature => feature.Key.FullName ==
                    "Andril.API.Authentication.ConfirmedIdentityFeature");
            Calls++;
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    // Test-only sentinels: access must never reach the application-service
    // dependency or repository. The PATCH sentinel would mutate the entry.
    private sealed class NotificationOperationProbe :
        INotificationRecipientAccessReader, INotificationRepository
    {
        private int _accessCalls;
        private int _repositoryCalls;
        public int AccessCalls => Volatile.Read(ref _accessCalls);
        public int RepositoryCalls => Volatile.Read(ref _repositoryCalls);
        public NotificationEntry Entry { get; }

        public NotificationOperationProbe(string module)
        {
            Entry = new NotificationEntry(
                Guid.Parse(NotificationId),
                new NotificationModuleScope(
                    Enum.Parse<NotificationModule>(module, ignoreCase: true),
                    Guid.Parse("22222222-2222-2222-2222-222222222222")),
                "test", "Synthetic notification", null,
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }

        public Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
            Guid authenticatedAccountId, NotificationModule module,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _accessCalls);
            throw new InvalidOperationException("Denied request reached notification access.");
        }

        private void RejectRepositoryCall()
        {
            Interlocked.Increment(ref _repositoryCalls);
            throw new InvalidOperationException("Denied request reached notification persistence.");
        }

        public Task AddAsync(NotificationEntry notification,
            CancellationToken cancellationToken = default)
        {
            RejectRepositoryCall();
            return Task.CompletedTask;
        }

        public Task<NotificationEntry?> FindAsync(NotificationModuleScope recipientScope,
            Guid notificationId, CancellationToken cancellationToken = default)
        {
            RejectRepositoryCall();
            return Task.FromResult<NotificationEntry?>(null);
        }

        public Task<System.Collections.Generic.IReadOnlyList<NotificationEntry>> ListLatestAsync(
            NotificationModuleScope recipientScope, int maxItems,
            CancellationToken cancellationToken = default)
        {
            RejectRepositoryCall();
            return Task.FromResult<System.Collections.Generic.IReadOnlyList<NotificationEntry>>(
                Array.Empty<NotificationEntry>());
        }

        public Task<bool> TryMarkReadAsync(NotificationModuleScope recipientScope,
            Guid notificationId, DateTimeOffset readAtUtc,
            CancellationToken cancellationToken = default)
        {
            Entry.MarkAsRead(readAtUtc);
            RejectRepositoryCall();
            return Task.FromResult(false);
        }
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
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(
                "Account initialization is not allowed in this test.");
        }
    }
}
