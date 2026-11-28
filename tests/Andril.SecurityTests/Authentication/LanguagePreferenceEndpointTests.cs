using System.Net;
using System.Net.Http.Json;
using System.Text;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class LanguagePreferenceEndpointTests
{
    private const string Path = "/api/v1/account/preferences/language";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidAuthentication_IsDenied(bool patch)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        foreach (var token in new[] { "", "invalid-token", auth.Token("expired") })
        {
            client.DefaultRequestHeaders.Authorization = token == "" ? null : new("Bearer", token);
            var response = patch ? await client.PatchAsJsonAsync(Path, new { language = "ta", mode = "sync" }) : await client.GetAsync(Path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ta")]
    [InlineData("si")]
    public async Task FallbackAndSyncRespectAccountAuthority(string language)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var fallback = await client.GetAsync(Path);
        Assert.Equal(new("en", false), await fallback.Content.ReadFromJsonAsync<LanguagePreferenceDto>());
        Assert.True(fallback.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Preferences);
        var sync = await client.PatchAsJsonAsync(Path, new { language, mode = "sync" });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(new(language, true), await sync.Content.ReadFromJsonAsync<LanguagePreferenceDto>());
        Assert.True(sync.Headers.CacheControl?.NoStore);
        var staleSync = await client.PatchAsJsonAsync(Path, new { language = "en", mode = "sync" });
        Assert.Equal(new(language, true), await staleSync.Content.ReadFromJsonAsync<LanguagePreferenceDto>());
        await client.PatchAsJsonAsync(Path, new { language = "si", mode = "settings" });
        var secondClientSync = await client.PatchAsJsonAsync(Path, new { language = "ta", mode = "sync" });
        Assert.Equal(new("si", true), await secondClientSync.Content.ReadFromJsonAsync<LanguagePreferenceDto>());
        Assert.Single(store.Preferences);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"language\":null,\"mode\":\"sync\"}")]
    [InlineData("{\"language\":\"\",\"mode\":\"sync\"}")]
    [InlineData("{\"language\":\"fr\",\"mode\":\"settings\"}")]
    [InlineData("{\"language\":\"EN\",\"mode\":\"sync\"}")]
    [InlineData("{\"language\":\"ta\"}")]
    [InlineData("{\"language\":\"ta\",\"mode\":\"device\"}")]
    [InlineData("{\"language\":\"ta\",\"mode\":\"sync\",\"accountId\":\"another-user\"}")]
    [InlineData("{")]
    public async Task InvalidRequests_ReturnSafe400WithoutWrites(string json)
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        var response = await client.PatchAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(store.Preferences);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QueryAccountId_DoesNotReadOrChangeAnotherAccount()
    {
        var store = new Store();
        var otherId = Guid.NewGuid();
        store.Preferences[otherId] = AccountLanguagePreference.Create(otherId, "si", DateTimeOffset.UtcNow);
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(new("en", false), await client.GetFromJsonAsync<LanguagePreferenceDto>(Path + "?accountId=" + otherId));
        await client.PatchAsJsonAsync(Path + "?accountId=" + otherId, new { language = "ta", mode = "settings" });
        Assert.Equal("si", store.Preferences[otherId].Language);
        Assert.Equal("ta", store.Preferences[store.Account.Id].Language);
    }

    [Fact]
    public async Task UninitializedAccount_Returns404WithoutCreatingAnything()
    {
        var store = new Store { AccountExists = false };
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync(Path, new { language = "ta", mode = "sync" })).StatusCode);
        Assert.Empty(store.Preferences);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignAccountOrPreferenceResultReturnsSafeUnavailable(bool wrongAccount, bool patch)
    {
        var store = new Store { WrongAccount = wrongAccount, ForeignPreference = !wrongAccount };
        using var auth = new AuthenticationFactory();
        using var factory = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Register(s, store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        using var response = patch ? await client.PatchAsJsonAsync(Path, new { language = "ta", mode = "settings" }) : await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(store.ForeignAccount.Id.ToString(), body);
        Assert.DoesNotContain(store.ForeignAccount.SupabaseUserId.ToString(), body);
        Assert.DoesNotContain("Exception", body);
        if (wrongAccount) Assert.Equal(0, store.PreferenceCalls);
    }

    private static void Register(IServiceCollection services, Store store)
    {
        services.Replace(ServiceDescriptor.Singleton<IAccountRepository>(store));
        services.Replace(ServiceDescriptor.Singleton<ILanguagePreferenceRepository>(store));
    }

    private sealed class Store : IAccountRepository, ILanguagePreferenceRepository
    {
        internal readonly Account Account = Account.Create(AuthenticationFactory.Subject, DateTimeOffset.UtcNow);
        internal readonly Dictionary<Guid, AccountLanguagePreference> Preferences = new();
        internal bool AccountExists = true;
        internal bool WrongAccount, ForeignPreference;
        internal int PreferenceCalls;
        internal readonly Account ForeignAccount = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        internal int Calls;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Account?>(WrongAccount ? ForeignAccount : AccountExists && subject == AuthenticationFactory.Subject ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected account initialization.");
        public Task<AccountLanguagePreference?> FindAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            PreferenceCalls++;
            return Task.FromResult(ForeignPreference ? AccountLanguagePreference.Create(ForeignAccount.Id, "si", DateTimeOffset.UtcNow) : Preferences.GetValueOrDefault(accountId));
        }
        public Task<AccountLanguagePreference> WriteAsync(Guid accountId, string language, bool initializeOnly,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            PreferenceCalls++;
            if (ForeignPreference) return Task.FromResult(AccountLanguagePreference.Create(ForeignAccount.Id, "si", now));
            if (!Preferences.TryGetValue(accountId, out var existing) || (!initializeOnly && existing.Language != language))
                Preferences[accountId] = AccountLanguagePreference.Create(accountId, language, now);
            return Task.FromResult(Preferences[accountId]);
        }
    }
}
