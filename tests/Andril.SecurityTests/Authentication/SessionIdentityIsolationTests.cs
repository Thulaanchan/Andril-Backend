using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Andril.Common.Accounts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class SessionIdentityIsolationTests
{
    [Fact]
    public async Task EarlierConfirmationCannotBypassLaterProviderDenial()
    {
        var handler = new IdentityHandler();
        var accounts = new Accounts();
        using var authentication = new AuthenticationFactory(identityHandler: handler);
        using var host = authentication.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(accounts))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", authentication.Token());

        using var initialized = await client.PostAsync("/api/v1/account/initialize", null);
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        var original = await initialized.Content.ReadFromJsonAsync<AccountDto>();
        handler.DenyAll = true;
        foreach (var initialize in new[] { false, true })
        {
            using var denied = initialize ? await client.PostAsync("/api/v1/account/initialize", null)
                : await client.GetAsync("/api/v1/account/me");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore);
            Assert.NotNull(denied.Headers.WwwAuthenticate);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, accounts.Calls);
        handler.DenyAll = false;
        Assert.Equal(original, await client.GetFromJsonAsync<AccountDto>("/api/v1/account/me"));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(2, accounts.Calls);
        Assert.Single(accounts.Rows);
    }

    [Fact]
    public async Task ConcurrentSubjectsHaveIndependentProviderEvidenceAndAccountAccess()
    {
        var deniedSubject = Guid.NewGuid();
        var handler = new IdentityHandler { DeniedSubject = deniedSubject, CoordinateTwoRequests = true };
        var accounts = new Accounts();
        using var authentication = new AuthenticationFactory(identityHandler: handler);
        using var host = authentication.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(accounts))));
        using var allowed = host.CreateClient();
        using var denied = host.CreateClient();
        allowed.DefaultRequestHeaders.Authorization = new("Bearer", authentication.Token());
        denied.DefaultRequestHeaders.Authorization = new("Bearer", authentication.Token(subject: deniedSubject));

        var results = await Task.WhenAll(allowed.PostAsync("/api/v1/account/initialize", null),
            denied.PostAsync("/api/v1/account/initialize", null));
        using var first = results[0];
        using var second = results[1];
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.True(second.Headers.CacheControl?.NoStore);
        Assert.Empty(await second.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, accounts.Calls);
        Assert.Equal(AuthenticationFactory.Subject, Assert.Single(accounts.Rows).Key);
        Assert.False(accounts.Rows.ContainsKey(deniedSubject));
        var body = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain(deniedSubject.ToString(), body);
        Assert.DoesNotContain(AuthenticationFactory.Subject.ToString(), body);
    }

    private sealed class IdentityHandler : HttpMessageHandler
    {
        internal bool DenyAll;
        internal Guid? DeniedSubject;
        internal bool CoordinateTwoRequests;
        internal int Calls;
        private readonly TaskCompletionSource bothRequests = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Only locally generated test JWTs reach this handler; no provider request is made.
            var subject = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter!).Subject);
            var call = Interlocked.Increment(ref Calls);
            if (CoordinateTwoRequests)
            {
                if (call == 2) bothRequests.TrySetResult();
                await bothRequests.Task.WaitAsync(cancellationToken);
            }
            if (DenyAll || subject == DeniedSubject) return new(HttpStatusCode.Unauthorized);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                id = subject, aud = "authenticated", role = "authenticated", is_anonymous = false,
                email = "synthetic@example.test", email_confirmed_at = "2020-01-01T00:00:00Z"
            }) };
        }
    }

    private sealed class Accounts : IAccountRepository
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
