using System.Net;
using System.Net.Http.Json;
using System.Text;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class PrivateProfileEndpointTests
{
    private const string Route = "/api/v1/account/profiles/learning";

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task InvalidAuthenticationCannotCreateProfiles(string fault)
    {
        using var factory = new AuthenticationFactory();
        using var client = factory.CreateClient();
        if (fault != "missing") client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(fault));
        using var response = await client.PostAsJsonAsync(Route, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("{\"accountId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"isComplete\":true}")]
    [InlineData("{\"displayName\":\"not-approved\"}")]
    [InlineData("{\"photoUrl\":\"https://example.test/public\"}")]
    [InlineData("{\"module\":\"learning\",\"module\":\"social\"}")]
    [InlineData("[]")]
    public async Task UnknownFieldsAndCompletionClaimsAreRejected(string body)
    {
        var store = new Store();
        using var factory = new AuthenticationFactory();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => Configure(s, store)));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await client.PostAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(store.Profile);
    }

    [Fact]
    public async Task OwnerOnlyCreationRetriesReadsAndModuleIsolation()
    {
        var store = new Store();
        using var factory = new AuthenticationFactory(identityHandler: new ConfirmEachSyntheticSubject());
        using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => Configure(s, store)));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route)).StatusCode);
        var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync(Route, new { })));
        var ids = new List<Guid>();
        foreach (var reply in replies)
        {
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.True(reply.Headers.CacheControl?.NoStore);
            var json = await reply.Content.ReadAsStringAsync();
            Assert.DoesNotContain(store.Account.Id.ToString(), json);
            Assert.DoesNotContain(store.Account.SupabaseUserId.ToString(), json);
            var profile = await reply.Content.ReadFromJsonAsync<PrivateProfileDto>();
            ids.Add(profile!.ProfileId);
            Assert.False(profile.IsComplete);
            reply.Dispose();
        }
        Assert.Single(ids.Distinct());
        var edited = await client.PutAsJsonAsync(Route, new { displayName = "  ALIce  ", bio = "Private draft", visibility = "private" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.True(edited.Headers.CacheControl?.NoStore);
        var draft = await edited.Content.ReadFromJsonAsync<PrivateProfileDto>();
        Assert.Equal("ALIce", draft!.DisplayName);
        Assert.False(draft.IsComplete);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Route,
            new { displayName = "Alice", bio = "x", visibility = "public" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Route,
            new { displayName = "Alice", bio = "x", visibility = "private", isComplete = true })).StatusCode);
        var photo = new ByteArrayContent([1, 2, 3]);
        photo.Headers.ContentType = new("application/octet-stream");
        var unavailable = await client.PostAsync(Route + "/photos/" + Guid.NewGuid(), photo);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.True(unavailable.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/account/profiles/hobbies")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/account/profiles/dating")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PatchAsJsonAsync(Route, new { isComplete = true })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(subject: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route + "?accountId=" + store.Account.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Route,
            new { displayName = "Attacker", bio = "x", visibility = "private" })).StatusCode);
    }

    [Fact]
    public async Task DraftEditsEnforceAuthorizationValidationAndSafeConflicts()
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var app = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Configure(s, store)));
        using var client = app.CreateClient();
        var valid = new { displayName = "Alex_2000", bio = "original", visibility = "private" };
        using var unauthorized = await client.PutAsJsonAsync(Route, valid);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.True(unauthorized.Headers.CacheControl?.NoStore);

        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        using var created = await client.PostAsJsonAsync(Route, new { });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var saved = await client.PutAsJsonAsync(Route, valid);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var forbidden = await client.PutAsJsonAsync("/api/v1/account/profiles/hobbies", valid);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        foreach (var json in new[]
        {
            """{"displayName":"Andril-Support","bio":"new","visibility":"private"}""",
            """{"displayName":"Valid Name","bio":"bad\u0000bio","visibility":"private"}""",
            """{"displayName":"Name\uD800","bio":"new","visibility":"private"}""",
            """{"displayName":"Valid Name","bio":"bad\uDC00","visibility":"private"}""",
            """{"displayName":"Valid Name","bio":null,"visibility":"private","\uD800":"bad"}""",
            """{"displayName":"Valid Name","bio":null,"visibility":"private","accountId":"forged"}"""
        })
        {
            using var rejected = await client.PutAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.True(rejected.Headers.CacheControl?.NoStore);
        }

        // The repository integration tests exercise the actual database uniqueness constraints.
        store.UnavailableCanonicalName = "taken name";
        using var conflict = await client.PutAsJsonAsync(Route,
            new { displayName = "  TAKEN NAME ", bio = "new", visibility = "private" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.True(conflict.Headers.CacheControl?.NoStore);
        var body = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("Display name unavailable", body);
        Assert.DoesNotContain(store.Account.Id.ToString(), body);
        Assert.DoesNotContain("TAKEN NAME", body);
        var draft = await client.GetFromJsonAsync<PrivateProfileDto>(Route);
        Assert.Equal("Alex_2000", draft!.DisplayName);
        Assert.Equal("original", draft.Bio);
        Assert.False(draft.IsComplete);
    }

    [Fact]
    public async Task KestrelRejectsOversizedDraftBeforeWriting()
    {
        var store = new Store();
        using var auth = new AuthenticationFactory();
        using var app = auth.WithWebHostBuilder(b => b.ConfigureTestServices(s => Configure(s, store)));
        app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app.StartServer();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(Assert.Single(addresses.Addresses))
        };
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token());
        using var created = await client.PostAsJsonAsync(Route, new { });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var rejected = await client.PutAsJsonAsync(Route,
            new { displayName = "Alex_2000", bio = new string('a', 17_000), visibility = "private" });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
        Assert.Null(store.Profile!.DisplayName);
    }

    private static void Configure(IServiceCollection services, Store store)
    {
        services.AddSingleton<IAccountRepository>(store);
        services.AddSingleton<IPrivateProfileRepository>(store);
        services.AddSingleton<IPolicyCatalogProvider>(store);
    }

    private sealed class ConfirmEachSyntheticSubject : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Test-only identity server: permit two separately verified synthetic subjects.
            var subject = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(request.Headers.Authorization!.Parameter).Subject;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                id = subject, aud = "authenticated", role = "authenticated", is_anonymous = false,
                email = "synthetic@example.test", email_confirmed_at = "2020-01-01T00:00:00Z"
            }) });
        }
    }

    private sealed class Store : IAccountRepository, IPrivateProfileRepository, IPolicyCatalogProvider
    {
        internal readonly Account Account = Account.Create(AuthenticationFactory.Subject, DateTimeOffset.UtcNow);
        internal PrivateModuleProfile? Profile;
        internal string? UnavailableCanonicalName;
        private readonly object gate = new();
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(Account.SupabaseUserId == subject ? Account : null);
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public PolicyCatalog GetCurrent() => new(new("v1", "https://example.test/terms", "en"),
            new("v1", "https://example.test/privacy", "en"), new("v1", "https://example.test/community", "en"));
        private void Validate(Guid accountId, Action<StarterModuleState> validate)
        {
            Assert.Equal(Account.Id, accountId);
            var now = DateTimeOffset.UtcNow;
            validate(new(AccountDemographics.Create(Account.Id, new(2000, 1, 1), "other", now),
                [AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "v1", now),
                 AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "v1", now)],
                 StarterMembership.Create(Account.Id, "learning", false, now)));
        }
        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            Validate(accountId, validate);
            return Task.FromResult(Profile);
        }
        public Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default)
        {
            Validate(accountId, validate);
            lock (gate) return Task.FromResult(Profile ??= PrivateModuleProfile.Create(accountId, module, clock.GetUtcNow()));
        }
        public Task<PrivateModuleProfile> UpdateAsync(Guid accountId, string module, ProfileEdit edit, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            Validate(accountId, validate);
            if (Profile is null) throw new ProfileMissingException();
            if (DisplayNames.Canonical(edit.DisplayName) == UnavailableCanonicalName)
                throw new ProfileNameConflictException();
            Profile.SetDraftFields(edit);
            return Task.FromResult(Profile);
        }
    }
}
