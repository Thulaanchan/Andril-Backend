using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class AuthFlowIntegrationTests
{
    [Theory]
    [InlineData("/api/v1/account/initialize", "POST")]
    [InlineData("/api/v1/account/me", "GET")]
    [InlineData("/api/v1/account/preferences/language", "PATCH")]
    [InlineData("/api/v1/account/eligibility", "PUT")]
    [InlineData("/api/v1/account/policies/acknowledgments", "POST")]
    [InlineData("/api/v1/account/starter-module", "POST")]
    public async Task UnconfirmedIdentityCannotBypassViaAnyAccountFeature(string path, string method)
    {
        var handler = new UserHandler { Mode = "unconfirmed" };
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(extraClaims:
            [new Claim("email_verified", "true"), new Claim("user_metadata", "{\"email_verified\":true}", "JSON")]));
        using var request = new HttpRequestMessage(new HttpMethod(method), path + "?verified=true")
        {
            Content = JsonContent.Create(new { verified = true, email = "same@example.test", emailConfirmed = true })
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Email confirmation required", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, store.Calls);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unauthorized", HttpStatusCode.Unauthorized)]
    [InlineData("mismatch", HttpStatusCode.Unauthorized)]
    public async Task VerificationFailureCannotInitializeOrReadAccounts(string mode, HttpStatusCode expected)
    {
        var handler = new UserHandler { Mode = mode };
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        foreach (var response in new[] { await client.PostAsync("/api/v1/account/initialize", null), await client.GetAsync("/api/v1/account/me") })
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-upstream-detail", body);
            Assert.DoesNotContain("same@example.test", body);
            Assert.DoesNotContain(AuthenticationFactory.Subject.ToString(), body);
        }
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ConfirmationInitializesAndResumesSameAccount_WithoutEmailLinking()
    {
        var handler = new UserHandler();
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/account/me")).StatusCode);
        var created = await client.PostAsync("/api/v1/account/initialize", null);
        var first = await created.Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        handler.Provider = "google";
        var resumed = await client.PostAsync("/api/v1/account/initialize", null);
        Assert.Equal(first, await resumed.Content.ReadFromJsonAsync<AccountDto>());
        Assert.Equal(first, await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"));
        // Same email, different Supabase UUID: never merge ANDRIL accounts by email.
        var secondSubject = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(subject: secondSubject));
        var other = await client.PostAsync("/api/v1/account/initialize", null);
        Assert.NotEqual(first!.AccountId, (await other.Content.ReadFromJsonAsync<AccountDto>())!.AccountId);
        Assert.Equal(2, store.Rows.Count);
        Assert.True(other.Headers.CacheControl?.NoStore);
        handler.Mode = "unauthorized";
        var rejected = await client.GetAsync("/api/v1/account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("anonymous")]
    [InlineData("service-role")]
    public async Task JwtStillValidatedBeforeAnyUserLookup(string fault)
    {
        var handler = new UserHandler();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var client = auth.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(fault));
        var response = await client.PostAsync("/api/v1/account/initialize", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RateLimitStopsLookups_AndDoesNotAffectOtherSubjectsOrHealth()
    {
        var handler = new UserHandler();
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                { ["SupabaseIdentity:AccountRequestsPerMinute"] = "2" }));
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store)));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/account/initialize", null)).StatusCode);
        var limited = await client.GetAsync("/api/v1/account/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.CacheControl?.NoStore);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(subject: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/account/me")).StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task MissingPublishableConfigurationFailsClosedWithoutSendingToken()
    {
        var handler = new UserHandler();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["SupabaseIdentity:PublishableKey"] = "" })));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/v1/account/initialize", null)).StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SettingsAndAccountRoutesShareApprovedSubjectQuotaBeforeProviderLookup()
    {
        var handler = new UserHandler();
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                { ["SupabaseIdentity:AccountRequestsPerMinute"] = "2" }));
            b.ConfigureTestServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
                s.Replace(ServiceDescriptor.Singleton<ILanguagePreferenceRepository>(new UnusedLanguagePreferences()));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        using var account = await client.GetAsync("/api/v1/account/me");
        using var settings = await client.GetAsync("/api/v1/account/preferences/language");
        Assert.Equal(HttpStatusCode.NotFound, account.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, settings.StatusCode);
        using var denied = await client.PatchAsJsonAsync("/api/v1/account/preferences/language", new { language = "ta", mode = "settings" });
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore);
        Assert.NotNull(denied.Headers.RetryAfter);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, store.Calls);
        Assert.Empty(store.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierSettingsConfirmationCannotBypassLaterProviderRejection(bool patch)
    {
        var handler = new UserHandler();
        var store = new Accounts();
        using var auth = new AuthenticationFactory(identityHandler: handler);
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
            s.Replace(ServiceDescriptor.Singleton<ILanguagePreferenceRepository>(new UnusedLanguagePreferences()));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        using var initial = await client.GetAsync("/api/v1/account/preferences/language");
        Assert.Equal(HttpStatusCode.NotFound, initial.StatusCode);
        handler.Mode = "unauthorized";
        using var response = patch ? await client.PatchAsJsonAsync("/api/v1/account/preferences/language", new { language = "ta", mode = "settings" })
            : await client.GetAsync("/api/v1/account/preferences/language");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, store.Calls);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class UnusedLanguagePreferences : ILanguagePreferenceRepository
    {
        public Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Missing or denied accounts cannot read preferences.");
        public Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Missing or denied accounts cannot write preferences.");
    }

    private sealed class Accounts : IAccountRepository
    {
        internal readonly ConcurrentDictionary<Guid, Account> Rows = new();
        internal int Calls;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.FromResult(Rows.GetValueOrDefault(subject));
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.FromResult(Rows.GetOrAdd(subject, id => Account.Create(id, now)));
        }
    }
    private sealed class UserHandler : HttpMessageHandler
    {
        internal string Mode = "confirmed", Provider = "email";
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("https://andril-test.supabase.co/auth/v1/user", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("sb_publishable_synthetic_test_fixture", request.Headers.GetValues("apikey").Single());
            if (Mode is "unavailable" or "unauthorized")
                return Task.FromResult(new HttpResponseMessage(Mode == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized)
                    { Content = new StringContent("private-upstream-detail") });
            var subject = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter).Subject;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = Mode == "mismatch" ? Guid.NewGuid().ToString() : subject,
                    role = "authenticated", aud = "authenticated", is_anonymous = false, email = "same@example.test",
                    email_confirmed_at = Mode == "unconfirmed" ? null : "2020-01-01T00:00:00Z",
                    user_metadata = new { verified = true, email_verified = true }, app_metadata = new { provider = Provider }
                })
            });
        }
    }
}
