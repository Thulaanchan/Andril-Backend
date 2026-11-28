using System.Net;
using Andril.Common.Accounts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class AccountOwnershipEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignStoreResultReturnsSafeUnavailableWithoutReleasingIdentity(bool initialize)
    {
        var foreignSubject = Guid.NewGuid();
        var foreign = Account.Create(foreignSubject, DateTimeOffset.UtcNow);
        using var authentication = new AuthenticationFactory();
        using var host = authentication.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(new ForeignAccounts(foreign)))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", authentication.Token());

        using var response = initialize
            ? await client.PostAsync("/api/v1/account/initialize", null)
            : await client.GetAsync("/api/v1/account/me");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Account service unavailable", body);
        Assert.DoesNotContain(foreign.Id.ToString(), body);
        Assert.DoesNotContain(foreignSubject.ToString(), body);
        Assert.DoesNotContain(AuthenticationFactory.Subject.ToString(), body);
        Assert.DoesNotContain("Exception", body);
    }

    private sealed class ForeignAccounts(Account account) : IAccountRepository
    {
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(account);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);
    }
}
