using System.Net;
using System.Net.Http.Json;
using System.Text;
using Andril.API.Configuration;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class AccountPolicyEndpointTests
{
    private const string StatusPath = "/api/v1/account/policies/status";
    private const string SubmitPath = "/api/v1/account/policies/acknowledgments";
    // Deliberately synthetic test fixtures, never production policy metadata.
    private const string ValidBody = """{"actions":[{"action":"terms_agreed","version":"test-terms"},{"action":"privacy_acknowledged","version":"test-privacy"}]}""";
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    [Fact]
    public async Task SuccessfulSubmission_RetryPreservesReceipts_AndNewVersionRequiresReview()
    {
        var store = new Store();
        var config = Config();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, config)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var initial = await client.GetFromJsonAsync<PolicyStatus>(StatusPath);
        Assert.False(initial!.RequiredActionsComplete);
        Assert.Empty(initial.History);
        Assert.Equal("en", initial.Documents.CommunityGuidelines.Language);
        var first = await client.PostAsync(SubmitPath, Json(ValidBody));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var accepted = (await first.Content.ReadFromJsonAsync<PolicyStatus>())!;
        Assert.True(accepted.RequiredActionsComplete);
        Assert.Equal(2, accepted.History.Count);
        Assert.All(accepted.History, r => Assert.Equal(TimeSpan.Zero, r.RecordedAtUtc.Offset));
        var repeat = await client.PostAsync(SubmitPath, Json(ValidBody));
        Assert.Equal(accepted.History, (await repeat.Content.ReadFromJsonAsync<PolicyStatus>())!.History);
        Assert.Equal(2, store.Records.Count);
        config["AccountPolicies:Terms:Version"] = "test-terms-next";
        var changed = (await client.GetFromJsonAsync<PolicyStatus>(StatusPath))!;
        Assert.False(changed.TermsAgreed);
        Assert.True(changed.PrivacyAcknowledged);
        Assert.False(changed.RequiredActionsComplete);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(SubmitPath, Json(ValidBody))).StatusCode);
        var newer = await client.PostAsync(SubmitPath, Json(ValidBody.Replace("test-terms", "test-terms-next")));
        Assert.True((await newer.Content.ReadFromJsonAsync<PolicyStatus>())!.RequiredActionsComplete);
        Assert.Equal(3, store.Records.Count);
        Assert.Contains(store.Records, x => x.Action == PolicyActions.TermsAgreed && x.Version == "test-terms"
            && x.RecordedAtUtc == accepted.History.Single(r => r.Action == PolicyActions.TermsAgreed).RecordedAtUtc);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"actions\":[]}")]
    [InlineData("{\"actions\":[null,null]}")]
    [InlineData("{\"actions\":[{\"action\":\"terms_agreed\",\"version\":\"test-terms\"}]}")]
    [InlineData("{\"actions\":[{\"action\":\"privacy_acknowledged\",\"version\":\"test-privacy\"}]}")]
    [InlineData("{\"actions\":[{\"action\":\"terms_agreed\",\"version\":\"test-terms\"},{\"action\":\"terms_agreed\",\"version\":\"test-terms\"}]}")]
    [InlineData("{\"accountId\":\"other\",\"actions\":[]}")]
    [InlineData("{\"actions\":[{\"action\":\"terms_agreed\",\"version\":\"test-terms\",\"recordedAtUtc\":\"2000-01-01\"},{\"action\":\"privacy_acknowledged\",\"version\":\"test-privacy\"}]}")]
    [InlineData("{\"actions\":[{\"action\":\"community_agreed\",\"version\":\"test-guidelines\"},{\"action\":\"privacy_acknowledged\",\"version\":\"test-privacy\"}]}")]
    [InlineData("{\"actions\":[{\"action\":\"terms_agreed\",\"version\":\" \"},{\"action\":\"privacy_acknowledged\",\"version\":\"test-privacy\"}]}")]
    public async Task MissingInvalidOrTamperedActions_RejectWithoutWriting(string body)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, Config())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PostAsync(SubmitPath, Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Records);
        Assert.False((await client.GetFromJsonAsync<PolicyStatus>(StatusPath))!.RequiredActionsComplete);
    }

    [Theory]
    [InlineData("test-terms")]
    [InlineData("test-privacy")]
    public async Task IncorrectVersionsAre409(string version)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, Config())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PostAsync(SubmitPath, Json(ValidBody.Replace(version, "unapproved")));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    public async Task AuthenticationRequiredForBothEndpoints(string fault)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, Config())));
        using var client = factory.CreateClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token(fault));
        foreach (var response in new[] { await client.GetAsync(StatusPath), await client.PostAsync(SubmitPath, Json(ValidBody)) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(0, store.AccountReads);
    }

    [Fact]
    public async Task OwnerIsResolvedFromToken_OtherAccountRecordsNeverReadOrWritten()
    {
        var store = new Store();
        var other = Guid.NewGuid();
        store.Records.Add(AccountPolicyRecord.Create(other, PolicyActions.TermsAgreed, "test-terms", DateTimeOffset.UtcNow));
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, Config())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var initial = (await client.GetFromJsonAsync<PolicyStatus>(StatusPath + "?accountId=" + other))!;
        Assert.Empty(initial.History);
        var response = await client.PostAsync(SubmitPath + "?accountId=" + other, Json(ValidBody));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(store.Records, x => x.AccountId == other);
        Assert.Equal(2, store.Records.Count(x => x.AccountId == store.Account.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(SubmitPath,
            Json(ValidBody.Replace("{\"actions\"", "{\"accountId\":\"" + other + "\",\"actions\"")))).StatusCode);
    }

    [Theory]
    [InlineData("Terms", "Version", "")]
    [InlineData("Privacy", "Url", "")]
    [InlineData("Terms", "Url", "http://policy.test/document")]
    [InlineData("Privacy", "Url", "javascript:alert(1)")]
    [InlineData("Terms", "Url", "https://user:password@policy.test/document")]
    [InlineData("CommunityGuidelines", "Url", "")]
    [InlineData("Terms", "Language", "ta")]
    public async Task InvalidCatalogFailsClosed(string document, string field, string value)
    {
        var store = new Store();
        var config = Config();
        config[$"AccountPolicies:{document}:{field}"] = value;
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, config)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        foreach (var response in new[] { await client.GetAsync(StatusPath), await client.PostAsync(SubmitPath, Json(ValidBody)) })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain("policy.test", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(store.Records);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task MissingAccountOrUnavailableStoreHasSafeResponse(bool unavailable, HttpStatusCode expected)
    {
        var store = new Store { Exists = false, Unavailable = unavailable };
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store, Config())));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        foreach (var response in new[] { await client.GetAsync(StatusPath), await client.PostAsync(SubmitPath, Json(ValidBody)) })
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Empty(store.Records);
    }

    private static IConfigurationRoot Config()
    {
        var values = new Dictionary<string, string?>();
        foreach (var (name, version) in new[] { ("Terms", "test-terms"), ("Privacy", "test-privacy"), ("CommunityGuidelines", "test-guidelines") })
        {
            values[$"AccountPolicies:{name}:Version"] = version;
            values[$"AccountPolicies:{name}:Url"] = "https://policy.test/" + version;
            values[$"AccountPolicies:{name}:Language"] = "en";
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static void Register(IServiceCollection services, Store store, IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<IAccountPolicyRepository>(store));
        services.Replace(ServiceDescriptor.Scoped<IPolicyCatalogProvider>(_ => new ConfiguredPolicyCatalog(configuration)));
    }
    private sealed class Store : IAccountRepository, IAccountPolicyRepository
    {
        internal Account Account = Account.Create(AuthenticationFactory.Subject, DateTimeOffset.UtcNow);
        internal List<AccountPolicyRecord> Records = new();
        internal bool Exists = true, Unavailable;
        internal int AccountReads;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            AccountReads++;
            if (Unavailable) throw new AccountStoreUnavailableException();
            return Task.FromResult<Account?>(Exists && subject == AuthenticationFactory.Subject ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No implicit account initialization.");
        public Task<IReadOnlyList<AccountPolicyRecord>> ReadAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountPolicyRecord>>(Records.Where(x => x.AccountId == accountId).ToArray());
        public Task AppendAsync(Guid accountId, IReadOnlyList<PolicyActionSubmission> actions, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            foreach (var action in actions)
                if (!Records.Any(x => x.AccountId == accountId && x.Action == action.Action && x.Version == action.Version))
                    Records.Add(AccountPolicyRecord.Create(accountId, action.Action, action.Version, now));
            return Task.CompletedTask;
        }
    }
}
