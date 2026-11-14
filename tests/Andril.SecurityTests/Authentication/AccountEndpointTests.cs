using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Andril.Common.Accounts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class AccountEndpointTests
{
    [Theory]
    [InlineData("/api/v1/account/me", false)]
    [InlineData("/api/v1/account/initialize", true)]
    public async Task MissingAndInvalidAuthentication_DeniedBeforeRepositoryAccess(string path, bool post)
    {
        var repository = new MemoryAccounts();
        using var baseFactory = new AuthenticationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(repository))));
        using var client = factory.CreateClient();
        foreach (var token in new[] { "", "not-a-jwt", baseFactory.Token("signature") })
        {
            client.DefaultRequestHeaders.Authorization = token.Length == 0 ? null : new("Bearer", token);
            var response = post ? await client.PostAsync(path, null) : await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task ClientSuppliedIds_CannotSelectOrInitializeAnotherAccount()
    {
        var repository = new MemoryAccounts();
        var otherSubject = Guid.NewGuid();
        var other = await repository.GetOrCreateAsync(otherSubject, DateTimeOffset.UtcNow);
        using var baseFactory = new AuthenticationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.Replace(ServiceDescriptor.Singleton<IAccountRepository>(repository))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.Token());
        var query = $"?accountId={other.Id}&supabaseUserId={otherSubject}";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/account/me" + query)).StatusCode);
        // No request DTO binds these values: the server uses the validated subject exclusively.
        var response = await client.PostAsJsonAsync("/api/v1/account/initialize" + query,
            new { accountId = other.Id, supabaseUserId = otherSubject });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(created);
        Assert.NotEqual(other.Id, created.AccountId);
        Assert.Equal(repository.Rows[AuthenticationFactory.Subject].Id, created.AccountId);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AuthenticationFactory.Subject.ToString(), json);
        Assert.DoesNotContain(otherSubject.ToString(), json);
        var repeated = await client.PostAsync("/api/v1/account/initialize", null);
        Assert.Equal(created, await repeated.Content.ReadFromJsonAsync<AccountDto>());
        Assert.Equal(created, await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me" + query));
        Assert.Equal(2, repository.Rows.Count);
    }

    [Fact]
    public async Task MissingDatabaseConfiguration_IsSafe503_AndHealthStaysPublic()
    {
        using var baseFactory = new AuthenticationFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:AndrilAccounts"] = "" })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", baseFactory.Token());
        var response = await client.GetAsync("/api/v1/account/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Account service unavailable", body);
        Assert.DoesNotContain("ConnectionStrings", body);
        Assert.DoesNotContain("Exception", body);
    }

    private sealed class MemoryAccounts : IAccountRepository
    {
        internal ConcurrentDictionary<Guid, Account> Rows { get; } = new();
        internal int Calls;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Rows.GetValueOrDefault(subject));
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Rows.GetOrAdd(subject, id => Account.Create(id, now)));
        }
    }
}
