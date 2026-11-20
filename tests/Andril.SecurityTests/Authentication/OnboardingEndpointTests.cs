using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class OnboardingEndpointTests
{
    private const string Route = "/api/v1/onboarding/status";

    [Fact]
    public async Task AccountAndOnboardingShareSubjectLimit_HealthRemainsPublic()
    {
        var handler = new IdentityHandler("unconfirmed");
        using var factory = new AuthenticationFactory(identityHandler: handler);
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["SupabaseIdentity:AccountRequestsPerMinute"] = "2" })));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
        using var limited = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.CacheControl?.NoStore);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(2, handler.Calls);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(subject: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task InvalidAuthenticationNeverReadsIdentityOrAccount(string fault)
    {
        var handler = new IdentityHandler();
        using var factory = new AuthenticationFactory(identityHandler: handler);
        using var client = factory.CreateClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(fault));
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("unconfirmed", 403)]
    [InlineData("unavailable", 503)]
    [InlineData("mismatch", 401)]
    public async Task LiveIdentityFailuresFailClosed(string mode, int status)
    {
        using var factory = new AuthenticationFactory(identityHandler: new IdentityHandler(mode));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await client.GetAsync(Route);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("onboardingComplete", body);
        if (mode == "unconfirmed") Assert.Contains("verify_identity", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task ResumeIsolationConcurrentReadsAndPrivateProfileDependency()
    {
        var store = new Store();
        using var factory = new AuthenticationFactory(identityHandler: new IdentityHandler());
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAccountRepository>(store);
            services.AddSingleton<Andril.Common.Accounts.Profiles.IPrivateProfileRepository>(new MissingProfiles());
            services.AddSingleton<IStarterModuleRepository>(store);
            services.AddSingleton<IPolicyCatalogProvider>(store);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var initial = await client.GetFromJsonAsync<OnboardingStatus>(Route);
        Assert.Equal("initialize_account", initial!.NextStep);
        store.Exists = true;
        Assert.Equal("complete_demographics", (await client.GetFromJsonAsync<OnboardingStatus>(Route))!.NextStep);
        store.Ready();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync(Route)));
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OnboardingStatus>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("profile_setup", result.NextStep);
            Assert.Equal("learning", result.SelectedModule);
            Assert.False(result.ModuleHomeAuthorized);
            Assert.False(result.OnboardingComplete);
            Assert.False(result.Progress.PrivateProfileComplete);
            Assert.DoesNotContain(store.Account.Id.ToString(), body);
            Assert.DoesNotContain("dateOfBirth", body);
            Assert.DoesNotContain("gender", body);
            response.Dispose();
        }
        using var restarted = app.CreateClient();
        restarted.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal("profile_setup", (await restarted.GetFromJsonAsync<OnboardingStatus>(Route))!.NextStep);
        store.Policies = [];
        Assert.Equal("review_policies", (await restarted.GetFromJsonAsync<OnboardingStatus>(Route))!.NextStep);
        restarted.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(subject: Guid.NewGuid()));
        var other = await restarted.GetFromJsonAsync<OnboardingStatus>(Route + "?accountId=" + store.Account.Id + "&completed=true&module=learning");
        Assert.Equal("initialize_account", other!.NextStep);
        Assert.Null(other.SelectedModule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPolicyOrDatabaseReturnsSafeUnavailable(bool databaseFailure)
    {
        var store = new Store { Exists = true, Configured = false, FailDatabase = databaseFailure };
        using var factory = new AuthenticationFactory();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAccountRepository>(store);
            services.AddSingleton<Andril.Common.Accounts.Profiles.IPrivateProfileRepository>(new MissingProfiles());
            services.AddSingleton<IStarterModuleRepository>(store);
            services.AddSingleton<IPolicyCatalogProvider>(store);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("onboardingComplete", await response.Content.ReadAsStringAsync());
    }

    private sealed class Store : IAccountRepository, IStarterModuleRepository, IPolicyCatalogProvider
    {
        internal readonly Account Account = Account.Create(AuthenticationFactory.Subject, DateTimeOffset.UtcNow);
        internal bool Exists, FailDatabase, Configured = true;
        private Andril.Common.Accounts.Eligibility.AccountDemographics? demographics;
        internal IReadOnlyList<AccountPolicyRecord> Policies = [];
        private StarterMembership? membership;
        public PolicyCatalog GetCurrent() => Configured ? new(new("v1", "https://policy.test/terms", "en"),
            new("v1", "https://policy.test/privacy", "en"), new("v1", "https://policy.test/community", "en")) : throw new PolicyConfigurationException();
        internal void Ready()
        {
            var now = DateTimeOffset.UtcNow;
            demographics = Andril.Common.Accounts.Eligibility.AccountDemographics.Create(Account.Id, new(2000, 1, 1), "other", now);
            Policies = [AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "v1", now),
                AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "v1", now)];
            membership = StarterMembership.Create(Account.Id, "learning", null, now);
        }
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            FailDatabase ? throw new AccountStoreUnavailableException() : Task.FromResult<Account?>(Exists && subject == Account.SupabaseUserId ? Account : null);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write");
        public Task<StarterModuleState> ReadAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            return Task.FromResult(new StarterModuleState(demographics, Policies, membership));
        }
        public Task<StarterModuleState> SelectAsync(Guid accountId, Func<StarterModuleState, StarterMembership> select, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected write");
    }

    private sealed class MissingProfiles : Andril.Common.Accounts.Profiles.IPrivateProfileRepository
    {
        public Task<Andril.Common.Accounts.Profiles.PrivateModuleProfile?> ReadAsync(Guid accountId, string module,
            Action<StarterModuleState> validate, CancellationToken cancellationToken = default) =>
            Task.FromResult<Andril.Common.Accounts.Profiles.PrivateModuleProfile?>(null);
        public Task<Andril.Common.Accounts.Profiles.PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module,
            Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Status must not create profiles.");
    }

    private sealed class IdentityHandler(string mode = "confirmed") : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (mode == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            // Decode only synthetic test tokens. Production verification uses Supabase's authenticated response.
            var subject = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter).Subject;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                id = mode == "mismatch" ? Guid.NewGuid().ToString() : subject,
                aud = "authenticated", role = "authenticated", is_anonymous = false, email = "synthetic@example.test",
                email_confirmed_at = mode == "unconfirmed" ? null : "2020-01-01T00:00:00Z"
            }) });
        }
    }
}
