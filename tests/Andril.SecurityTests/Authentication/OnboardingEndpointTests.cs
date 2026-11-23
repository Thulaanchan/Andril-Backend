using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class OnboardingEndpointTests
{
    private const string Route = "/api/v1/onboarding/status";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnboardingWireShapeAndPublishedCompletionConstraintsRemainCompatible(bool complete)
    {
        // Published YAML is maintained by hand; Program does not generate it from C#.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", "andril-api.v1.yaml")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var contract = File.ReadAllText(Path.Combine(directory.FullName, "contracts", "openapi", "andril-api.v1.yaml"));
        var schema = contract.Split("    OnboardingStatus:")[1].Split("    StarterModuleSelection:")[0];
        string Field(string name) => System.Text.RegularExpressions.Regex.Match(schema,
            @"(?m)^\s+" + name + @":\r?\n(?:(?:[ ]{10,}[^\r\n]*)\r?\n)+").Value;
        var profileField = Field("privateProfileComplete");
        Assert.Contains("type: boolean", profileField);
        Assert.DoesNotContain("enum:", profileField);
        var onboardingField = Field("onboardingComplete");
        Assert.Contains("type: boolean", onboardingField);
        Assert.DoesNotContain("enum:", onboardingField);
        Assert.Contains("enum: [false]", Field("moduleHomeAuthorized"));
        Assert.Contains("  " + Route + ":", contract);
        Assert.Contains("$ref: '#/components/schemas/OnboardingStatus'", contract);

        var status = new OnboardingStatus("profile_setup", new(true, true, true, true, true)
            { PrivateProfileComplete = complete }, "social", complete ? null : "social_display_name");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.Equal(new[] { "blockingDependency", "moduleHomeAuthorized", "nextStep", "onboardingComplete", "progress", "selectedModule" },
            root.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(new[] { "accountInitialized", "currentPoliciesAccepted", "demographicsComplete", "identityVerified", "privateProfileComplete", "starterModuleSelected" },
            root.GetProperty("progress").EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(complete, root.GetProperty("progress").GetProperty("privateProfileComplete").GetBoolean());
        Assert.Equal(complete, root.GetProperty("onboardingComplete").GetBoolean());
        Assert.False(root.GetProperty("moduleHomeAuthorized").GetBoolean());
    }

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
        Assert.False(initial.OnboardingComplete);
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

    [Theory]
    [InlineData("ready", true, 200)]
    [InlineData("missing-profile", false, 200)]
    [InlineData("missing-name", false, 200)]
    [InlineData("invalid-name", false, 200)]
    [InlineData("terms", false, 200)]
    [InlineData("privacy", false, 200)]
    [InlineData("outdated", false, 200)]
    [InlineData("underage", false, 200)]
    [InlineData("membership", false, 200)]
    [InlineData("opt-in", false, 503)]
    [InlineData("foreign-profile", false, 503)]
    [InlineData("wrong-module", false, 503)]
    [InlineData("profile-store", false, 503)]
    [InlineData("learning", false, 200)]
    [InlineData("hobbies", false, 200)]
    public async Task SocialCompletionUsesOwnerEvidenceAndNeverClientFlagsOrEntryAuthorization(string fault, bool complete, int statusCode)
    {
        var store = new Store { Exists = true };
        var module = fault is "learning" or "hobbies" ? fault : "social";
        store.Ready(module);
        store.Profile = PrivateModuleProfile.Create(store.Account.Id, module, DateTimeOffset.UtcNow);
        store.Profile.SetDraftFields(new("Social Person", null, "private"));
        if (fault == "missing-profile") store.Profile = null;
        if (fault is "missing-name" or "invalid-name")
            typeof(PrivateModuleProfile).GetProperty(nameof(PrivateModuleProfile.DisplayName))!.SetValue(store.Profile, fault == "missing-name" ? null : "Admin");
        if (fault is "terms" or "privacy") store.Policies = store.Policies.Where(x => x.Action != (fault == "terms" ? PolicyActions.TermsAgreed : PolicyActions.PrivacyAcknowledged)).ToArray();
        if (fault == "outdated") store.Version = "v2";
        if (fault == "underage") store.demographics = Andril.Common.Accounts.Eligibility.AccountDemographics.Create(store.Account.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-17), "other", DateTimeOffset.UtcNow);
        if (fault == "membership") store.membership = null;
        if (fault == "opt-in") typeof(StarterMembership).GetProperty(nameof(StarterMembership.SocialOptInAtUtc))!.SetValue(store.membership, null);
        if (fault == "foreign-profile") store.Profile = PrivateModuleProfile.Create(Guid.NewGuid(), "social", DateTimeOffset.UtcNow);
        if (fault == "wrong-module") store.Profile = PrivateModuleProfile.Create(store.Account.Id, "learning", DateTimeOffset.UtcNow);
        store.FailProfile = fault == "profile-store";
        using var factory = new AuthenticationFactory(identityHandler: new IdentityHandler());
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAccountRepository>(store);
            services.AddSingleton<IStarterModuleRepository>(store);
            services.AddSingleton<IPrivateProfileRepository>(store);
            services.AddSingleton<IPolicyCatalogProvider>(store);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await client.GetAsync(Route + "?accountId=" + Guid.NewGuid() + "&module=learning&privateProfileComplete=true&onboardingComplete=true&moduleHomeAuthorized=true");
        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(store.Account.Id.ToString(), json);
        Assert.DoesNotContain("Social Person", json);
        Assert.DoesNotContain("dateOfBirth", json);
        if (statusCode != 200)
        {
            Assert.DoesNotContain("privateProfileComplete", json);
            Assert.DoesNotContain("onboardingComplete", json);
            return;
        }
        using var body = JsonDocument.Parse(json);
        Assert.Equal(complete, body.RootElement.GetProperty("progress").GetProperty("privateProfileComplete").GetBoolean());
        Assert.Equal(complete, body.RootElement.GetProperty("onboardingComplete").GetBoolean());
        Assert.False(body.RootElement.GetProperty("moduleHomeAuthorized").GetBoolean());
        if (complete)
        {
            Assert.Equal("social", body.RootElement.GetProperty("selectedModule").GetString());
            Assert.Equal("profile_setup", body.RootElement.GetProperty("nextStep").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("blockingDependency").ValueKind);
            var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
            Assert.DoesNotContain(routes, endpoint =>
                endpoint.RoutePattern.PathSegments.Any(segment => segment.Parts.OfType<RoutePatternLiteralPart>()
                    .Any(part => part.Content.Equals("social", StringComparison.OrdinalIgnoreCase)
                        || part.Content.Equals("chat", StringComparison.OrdinalIgnoreCase))));
            // A new HTTP request scope must re-read the same persisted owner evidence.
            using var resumed = app.CreateClient();
            resumed.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
            var repeat = await resumed.GetFromJsonAsync<OnboardingStatus>(Route);
            Assert.True(repeat!.Progress.PrivateProfileComplete);
            Assert.True(repeat.OnboardingComplete);
            Assert.False(repeat.ModuleHomeAuthorized);
            store.Version = "v2";
            await AssertIncompleteAsync("review_policies");
            store.Version = "v1";
            store.demographics = Andril.Common.Accounts.Eligibility.AccountDemographics.Create(
                store.Account.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-17), "other", DateTimeOffset.UtcNow);
            await AssertIncompleteAsync("age_ineligible");

            async Task AssertIncompleteAsync(string nextStep)
            {
                var status = await resumed.GetFromJsonAsync<OnboardingStatus>(Route);
                Assert.Equal(nextStep, status!.NextStep);
                Assert.False(status.Progress.PrivateProfileComplete);
                Assert.False(status.OnboardingComplete);
                Assert.False(status.ModuleHomeAuthorized);
            }
        }
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    public async Task ModuleProfileAbsenceIncompleteSetupAndUnavailableLookupRemainDistinct(string module)
    {
        const string displayName = "Synthetic Draft";
        var store = new Store { Exists = true };
        store.Ready(module);
        using var factory = new AuthenticationFactory(identityHandler: new IdentityHandler());
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAccountRepository>(store);
            services.AddSingleton<IStarterModuleRepository>(store);
            services.AddSingleton<IPrivateProfileRepository>(store);
            services.AddSingleton<IPolicyCatalogProvider>(store);
        }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());

        await AssertIncompleteAsync("private_profile_missing");

        var now = DateTimeOffset.UtcNow;
        var ownedProfile = PrivateModuleProfile.Create(store.Account.Id, module, now);
        ownedProfile.SetDraftFields(new(displayName, null, "private"));
        store.Profile = ownedProfile;
        await AssertIncompleteAsync("approved_profile_contract");

        store.FailProfile = true;
        await AssertUnavailableAsync("Account service unavailable");
        store.FailProfile = false;

        store.Profile = PrivateModuleProfile.Create(store.Account.Id, "social", now);
        store.Profile.SetDraftFields(new(displayName, null, "private"));
        await AssertUnavailableAsync("Onboarding status unavailable");

        store.Profile = null;
        await AssertIncompleteAsync("private_profile_missing");

        async Task AssertIncompleteAsync(string blocker)
        {
            using var response = await client.GetAsync(Route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(displayName, json);
            Assert.DoesNotContain(store.Account.Id.ToString(), json);
            if (store.Profile is { } profile) Assert.DoesNotContain(profile.Id.ToString(), json);
            var status = await response.Content.ReadFromJsonAsync<OnboardingStatus>();
            Assert.NotNull(status);
            Assert.Equal(module, status.SelectedModule);
            Assert.Equal("profile_setup", status.NextStep);
            Assert.Equal(blocker, status.BlockingDependency);
            Assert.False(status.Progress.PrivateProfileComplete);
            Assert.False(status.OnboardingComplete);
            Assert.False(status.ModuleHomeAuthorized);
        }

        async Task AssertUnavailableAsync(string title)
        {
            using var response = await client.GetAsync(Route);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal(title, root.GetProperty("title").GetString());
            Assert.False(root.TryGetProperty("progress", out _));
            Assert.False(root.TryGetProperty("selectedModule", out _));
            Assert.False(root.TryGetProperty("moduleHomeAuthorized", out _));
        }
    }

    private sealed class Store : IAccountRepository, IStarterModuleRepository, IPolicyCatalogProvider, IPrivateProfileRepository
    {
        internal readonly Account Account = Account.Create(AuthenticationFactory.Subject, DateTimeOffset.UtcNow);
        internal bool Exists, FailDatabase, Configured = true;
        internal Andril.Common.Accounts.Eligibility.AccountDemographics? demographics;
        internal IReadOnlyList<AccountPolicyRecord> Policies = [];
        internal StarterMembership? membership;
        internal PrivateModuleProfile? Profile;
        internal bool FailProfile;
        internal string Version = "v1";
        public PolicyCatalog GetCurrent() => Configured ? new(new(Version, "https://policy.test/terms", "en"),
            new(Version, "https://policy.test/privacy", "en"), new("v1", "https://policy.test/community", "en")) : throw new PolicyConfigurationException();
        internal void Ready(string module = "learning")
        {
            var now = DateTimeOffset.UtcNow;
            demographics = Andril.Common.Accounts.Eligibility.AccountDemographics.Create(Account.Id, new(2000, 1, 1), "other", now);
            Policies = [AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "v1", now),
                AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "v1", now)];
            membership = StarterMembership.Create(Account.Id, module, module == "social", now);
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
        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            if (FailProfile) throw new AccountStoreUnavailableException();
            validate(new(demographics, Policies, membership));
            return Task.FromResult(Profile);
        }
        public Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Status must not create profiles.");
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
