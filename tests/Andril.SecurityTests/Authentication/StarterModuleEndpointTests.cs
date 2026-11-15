using System.Net;
using System.Net.Http.Json;
using System.Text;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class StarterModuleEndpointTests
{
    private const string Path = "/api/v1/account/starter-module";
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    public async Task BothRoutesRequireAuthenticationAndNeverCache(string fault)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(fault));
        foreach (var response in new[] { await client.GetAsync(Path), await client.PostAsJsonAsync(Path, new { module = "learning" }) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(0, store.AccountReads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"module\":[\"learning\",\"hobbies\"]}")]
    [InlineData("{\"modules\":[\"learning\",\"hobbies\"]}")]
    [InlineData("{\"module\":\"other\"}")]
    [InlineData("{\"module\":\"learning\",\"module\":\"hobbies\"}")]
    [InlineData("{\"module\":\"learning\",\"Module\":\"hobbies\"}")]
    [InlineData("{\"module\":\"social\",\"socialOptIn\":false,\"socialOptIn\":true}")]
    [InlineData("{\"module\":\"social\"}")]
    [InlineData("{\"module\":\"social\",\"socialOptIn\":false}")]
    [InlineData("{\"module\":\"social\",\"socialOptIn\":\"true\"}")]
    [InlineData("{\"module\":\"learning\",\"socialOptIn\":true}")]
    [InlineData("{\"module\":\"learning\",\"accountId\":\"other\"}")]
    [InlineData("{\"module\":\"social\",\"socialOptIn\":true,\"age\":18}")]
    [InlineData("{\"module\":\"learning\",\"requiredPoliciesComplete\":true}")]
    [InlineData("{\"module\":\"social\",\"socialOptIn\":true,\"socialOptInAtUtc\":\"2000-01-01\"}")]
    public async Task TamperingMultipleSelectionsAndMissingOptInAreRejected(string body)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PostAsync(Path, Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Memberships);
    }

    [Theory]
    [InlineData(15, "learning", HttpStatusCode.Forbidden)]
    [InlineData(15, "hobbies", HttpStatusCode.Forbidden)]
    [InlineData(15, "social", HttpStatusCode.Forbidden)]
    [InlineData(16, "learning", HttpStatusCode.OK)]
    [InlineData(16, "hobbies", HttpStatusCode.OK)]
    [InlineData(16, "social", HttpStatusCode.Forbidden)]
    [InlineData(17, "learning", HttpStatusCode.OK)]
    [InlineData(17, "hobbies", HttpStatusCode.OK)]
    [InlineData(17, "social", HttpStatusCode.Forbidden)]
    [InlineData(18, "learning", HttpStatusCode.OK)]
    [InlineData(18, "hobbies", HttpStatusCode.OK)]
    [InlineData(18, "social", HttpStatusCode.OK)]
    public async Task AgeIsCalculatedByServer(int age, string module, HttpStatusCode expected)
    {
        var store = new Store(age);
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PostAsJsonAsync(Path, new { module, socialOptIn = module == "social" });
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, store.Memberships.Count);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task OwnerOnlyStatusAndSelection_IdempotentRetryAndChangedChoiceConflict(string module)
    {
        var store = new Store();
        var other = Guid.NewGuid();
        store.Memberships[other] = StarterMembership.Create(other, "learning", false, Clock.Now);
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var initial = await client.GetAsync(Path + "?accountId=" + other);
        Assert.True(initial.Headers.CacheControl?.NoStore);
        Assert.Null((await initial.Content.ReadFromJsonAsync<StarterModuleStatus>())!.Membership);
        var first = await client.PostAsJsonAsync(Path + "?accountId=" + other, new { module, socialOptIn = module == "social" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        foreach (var secret in new[] { "accountId", "supabase", "dateOfBirth", "gender", "private-test-gender", "profileId", other.ToString(), store.Account.Id.ToString() })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        var saved = (await first.Content.ReadFromJsonAsync<StarterModuleStatus>())!;
        var repeat = await client.PostAsJsonAsync(Path, new { module, socialOptIn = module == "social" });
        Assert.Equal(saved.Membership, (await repeat.Content.ReadFromJsonAsync<StarterModuleStatus>())!.Membership);
        var get = (await client.GetFromJsonAsync<StarterModuleStatus>(Path))!;
        Assert.Equal(saved.Membership, get.Membership);
        Assert.All(get.Modules, x => Assert.False(x.CanSelect));
        var conflict = await client.PostAsJsonAsync(Path, new { module = module == "learning" ? "hobbies" : "learning" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.True(conflict.Headers.CacheControl?.NoStore);
        Assert.Equal(2, store.Memberships.Count);
        Assert.Equal("learning", store.Memberships[other].Module);
        Assert.Equal(module, store.Memberships[store.Account.Id].Module);
    }

    [Theory]
    [InlineData("demographics", HttpStatusCode.Conflict)]
    [InlineData("terms", HttpStatusCode.Conflict)]
    [InlineData("privacy", HttpStatusCode.Conflict)]
    [InlineData("outdated", HttpStatusCode.Conflict)]
    [InlineData("account", HttpStatusCode.NotFound)]
    [InlineData("configuration", HttpStatusCode.ServiceUnavailable)]
    [InlineData("storage", HttpStatusCode.ServiceUnavailable)]
    public async Task PrerequisiteFailuresAreSafeAndDoNotWrite(string missing, HttpStatusCode expected)
    {
        var store = new Store();
        if (missing == "demographics") store.Demographics = null;
        if (missing == "terms") store.Policies.RemoveAll(x => x.Action == PolicyActions.TermsAgreed);
        if (missing == "privacy") store.Policies.RemoveAll(x => x.Action == PolicyActions.PrivacyAcknowledged);
        if (missing == "outdated") store.Catalog = store.Catalog with { Terms = store.Catalog.Terms with { Version = "test-new" } };
        store.Exists = missing != "account";
        store.Configured = missing != "configuration";
        store.Unavailable = missing == "storage";
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PostAsJsonAsync(Path, new { module = "learning" });
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Memberships);
        var get = await client.GetAsync(Path);
        Assert.True(get.Headers.CacheControl?.NoStore);
        if (expected == HttpStatusCode.Conflict)
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.All((await get.Content.ReadFromJsonAsync<StarterModuleStatus>())!.Modules, x => Assert.False(x.CanSelect));
        }
        else Assert.Equal(expected, get.StatusCode);
    }

    private static void Register(IServiceCollection services, Store store)
    {
        services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<IStarterModuleRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<IPolicyCatalogProvider>(store));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new Clock()));
    }
    private sealed class Clock : TimeProvider
    {
        internal static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAccountRepository, IStarterModuleRepository, IPolicyCatalogProvider
    {
        internal readonly Account Account = Account.Create(AuthenticationFactory.Subject, Clock.Now);
        internal AccountDemographics? Demographics;
        internal readonly List<AccountPolicyRecord> Policies = new();
        internal readonly Dictionary<Guid, StarterMembership> Memberships = new();
        internal bool Exists = true, Configured = true, Unavailable;
        internal int AccountReads;
        internal PolicyCatalog Catalog = new(new("test-terms", "https://policy.test/terms", "en"),
            new("test-privacy", "https://policy.test/privacy", "en"), new("test-guidelines", "https://policy.test/guidelines", "en"));
        internal Store(int age = 18)
        {
            Demographics = AccountDemographics.Create(Account.Id, new(2026 - age, 11, 15), "private-test-gender", Clock.Now);
            Policies.Add(AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "test-terms", Clock.Now));
            Policies.Add(AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "test-privacy", Clock.Now));
        }
        public PolicyCatalog GetCurrent() => Configured ? Catalog : throw new PolicyConfigurationException();
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            AccountReads++;
            if (Unavailable) throw new AccountStoreUnavailableException();
            return Task.FromResult<Account?>(Exists && subject == AuthenticationFactory.Subject ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No implicit account initialization.");
        public Task<StarterModuleState> ReadAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Account.Id, accountId);
            return Task.FromResult(new StarterModuleState(Demographics, Policies, Memberships.GetValueOrDefault(accountId)));
        }
        public async Task<StarterModuleState> SelectAsync(Guid accountId, Func<StarterModuleState, StarterMembership> select,
            CancellationToken cancellationToken = default)
        {
            var state = await ReadAsync(accountId, cancellationToken);
            Memberships[accountId] = select(state);
            return state with { Membership = Memberships[accountId] };
        }
    }
}
