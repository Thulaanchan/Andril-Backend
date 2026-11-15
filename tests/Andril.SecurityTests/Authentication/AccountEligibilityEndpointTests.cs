using System.Net;
using System.Net.Http.Json;
using System.Text;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class AccountEligibilityEndpointTests
{
    private const string Path = "/api/v1/account/eligibility";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticationRequiredBeforeAccess(bool put)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        foreach (var token in new[] { "", "invalid", auth.Token("expired") })
        {
            client.DefaultRequestHeaders.Authorization = token == "" ? null : new("Bearer", token);
            var response = put ? await client.PutAsJsonAsync(Path, new { dateOfBirth = "2000-01-01", gender = "private value" }) : await client.GetAsync(Path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"gender\":\"value\"}")]
    [InlineData("{\"dateOfBirth\":\"2000-01-01\"}")]
    [InlineData("{\"dateOfBirth\":\"2000-01-01\",\"gender\":\" \"}")]
    [InlineData("{\"dateOfBirth\":\"2001-02-29\",\"gender\":\"value\"}")]
    [InlineData("{\"dateOfBirth\":\"9999-12-31\",\"gender\":\"value\"}")]
    [InlineData("{\"dateOfBirth\":\"2000-01-01\",\"gender\":\"value\",\"age\":25}")]
    [InlineData("{\"dateOfBirth\":\"2000-01-01\",\"gender\":\"value\",\"accountId\":\"other\"}")]
    [InlineData("{\"dateOfBirth\":\"2000-01-01\",\"gender\":\"value\",\"socialEligible\":true}")]
    public async Task InvalidOrForgedInputRejected(string json)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PutAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Details);
    }

    [Theory]
    [InlineData("2011-11-15", false, false)]
    [InlineData("2010-11-15", true, false)]
    [InlineData("2008-11-15", true, true)]
    public async Task ResponsesContainOnlyAgeEligibility_NotPrivateDetails(string birth, bool learning, bool social)
    {
        var store = new Store();
        var other = Guid.NewGuid();
        store.Details[other] = AccountDemographics.Create(other, new(1990, 1, 1), "other private value", Clock.Now);
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var absent = await client.GetFromJsonAsync<AccountEligibilityDto>(Path + "?accountId=" + other);
        Assert.False(absent!.HasSubmission);
        Assert.Equal(new(false, false, false), absent.AgeEligibility);
        var response = await client.PutAsJsonAsync(Path + "?accountId=" + other,
            new { dateOfBirth = birth, gender = "my private value" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = await response.Content.ReadFromJsonAsync<AccountEligibilityDto>();
        Assert.True(result!.HasSubmission);
        Assert.Equal(new(learning, learning, social), result.AgeEligibility);
        var get = await client.GetAsync(Path + "?accountId=" + other);
        Assert.True(get.Headers.CacheControl?.NoStore);
        Assert.Equal(result, await get.Content.ReadFromJsonAsync<AccountEligibilityDto>());
        foreach (var body in new[] { await response.Content.ReadAsStringAsync(), await get.Content.ReadAsStringAsync() })
        {
            Assert.DoesNotContain("dateOfBirth", body);
            Assert.DoesNotContain("gender", body);
            Assert.DoesNotContain("private value", body);
            Assert.DoesNotContain(AuthenticationFactory.Subject.ToString(), body);
            Assert.DoesNotContain("accountId", body);
        }
        Assert.Equal(new(1990, 1, 1), store.Details[other].DateOfBirth);
        Assert.Equal(DateOnly.ParseExact(birth, "yyyy-MM-dd"), store.Details[store.Account.Id].DateOfBirth);
    }

    [Fact]
    public async Task MissingAccountIs404_AndHealthRemainsPublic()
    {
        var store = new Store { Exists = false };
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Path,
            new { dateOfBirth = "2000-01-01", gender = "value" })).StatusCode);
        Assert.Empty(store.Details);
    }

    private static void Register(IServiceCollection services, Store store)
    {
        services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<IAccountDemographicsRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new Clock()));
    }

    private sealed class Clock : TimeProvider
    {
        internal static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAccountRepository, IAccountDemographicsRepository
    {
        internal Account Account = Account.Create(AuthenticationFactory.Subject, Clock.Now);
        internal Dictionary<Guid, AccountDemographics> Details = new();
        internal int Calls;
        internal bool Exists = true;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Account?>(Exists && subject == AuthenticationFactory.Subject ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No implicit initialization.");
        public Task<AccountDemographics?> FindAsync(Guid accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Details.GetValueOrDefault(accountId));
        public Task<AccountDemographics> SaveAsync(Guid accountId, DateOnly dateOfBirth, string gender, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Details[accountId] = AccountDemographics.Create(accountId, dateOfBirth, gender, now);
            return Task.FromResult(Details[accountId]);
        }
    }
}
