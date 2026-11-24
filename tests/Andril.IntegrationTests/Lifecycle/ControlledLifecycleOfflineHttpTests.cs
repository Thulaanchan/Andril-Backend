using System.Net;
using System.Text;
using System.Text.Json;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Policies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

public sealed class ControlledLifecycleOfflineHttpTests
{
    private const string Root = "/api/v1/account/modules";
    private static readonly Guid Subject = new("71b6b0b3-eeb0-4b8d-984d-009bb40fd6e8");
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("activation", "{\"socialOptIn\":false,\"socialOptIn\":true}")]
    [InlineData("activation", "{\"SocialOptIn\":false}")]
    [InlineData("activation", "{\"expectedRevision\":null}")]
    [InlineData("activation", "{\"accountId\":\"foreign\"}")]
    [InlineData("activation", "{")]
    [InlineData("activation", "[]")]
    [InlineData("reactivation", "{\"expectedRevision\":2,\"expectedRevision\":3}")]
    [InlineData("reactivation", "{\"expectedRevision\":9223372036854775807}")]
    [InlineData("reactivation", "{\"expectedRevision\":\"2\"}")]
    public async Task ShapeFailuresRunBeforeCurrentIdentityOrStoreAccess(string operation, string body)
    {
        var authorityReads = 0;
        await using var host = await ControlledLifecycleHost.StartAsync(
            () => throw new InvalidOperationException("Malformed request cannot access the store."),
            () => { authorityReads++; return false; });
        host.Authenticate(Subject);

        using var response = await Post(host, operation, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, authorityReads);
    }

    [Fact]
    public async Task AuthenticationAndThrottlePrecedeRequestShapeValidation()
    {
        var authorityReads = 0;
        await using var host = await ControlledLifecycleHost.StartAsync(
            () => throw new InvalidOperationException("Invalid request cannot access the store."),
            () => { authorityReads++; return false; }, limit: 1);

        using var anonymous = await Post(host, "activation", "{");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.CacheControl?.NoStore);

        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.Token(Subject, expired: true));
        using var expired = await Post(host, "activation", "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        host.Authenticate(Subject);
        using var badRequest = await Post(host, "activation", "{");
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        using var throttled = await Post(host, "activation", "{}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.CacheControl?.NoStore);
        Assert.Equal(0, authorityReads);
    }

    [Fact]
    public async Task OversizedBodiesAreRejectedWithAndWithoutContentLength()
    {
        var authorityReads = 0;
        await using var host = await ControlledLifecycleHost.StartAsync(
            () => throw new InvalidOperationException("Oversized request cannot access the store."),
            () => { authorityReads++; return true; });
        host.Authenticate(Subject);
        var body = new string(' ', 4096) + "{}";

        using var declared = await Post(host, "activation", body);
        Assert.Equal(HttpStatusCode.BadRequest, declared.StatusCode);
        using var content = new LengthlessJsonContent(body);
        Assert.Null(content.Headers.ContentLength);
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "/learning/activation") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var streamed = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, streamed.StatusCode);
        Assert.True(streamed.Headers.CacheControl?.NoStore);
        Assert.Equal(0, authorityReads);
    }

    [Fact]
    public async Task VerificationIsRepeatedForReplayAndReactivationRemainsScoped()
    {
        var store = new OfflineStore();
        var confirmed = true;
        var authorityReads = 0;
        await using var host = await ControlledLifecycleHost.StartAsync(() => store, () => { authorityReads++; return confirmed; });
        host.Authenticate(Subject);
        var key = Guid.NewGuid().ToString("D");
        using var first = await Post(host, "activation", "{}", key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = await Post(host, "activation", "{}", key);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        confirmed = false;
        using var revoked = await Post(host, "activation", "{}", key);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain("revision", await revoked.Content.ReadAsStringAsync());
        Assert.Equal(3, authorityReads);
        Assert.Equal(1, store.EffectiveWrites);

        confirmed = true;
        store.SeedInactive();
        using var stale = await Post(host, "reactivation", """{"expectedRevision":1}""");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var result = await Post(host, "reactivation", """{"expectedRevision":2}""");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(new[] { "module", "revision", "state", "stateChangedAtUtc" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using var home = await host.Client.GetAsync("/api/v1/modules/learning/home");
        Assert.Equal(HttpStatusCode.Forbidden, home.StatusCode);
        Assert.Equal(2, store.EffectiveWrites);
    }

    [Fact]
    public async Task ControlledOpenApiAndProductionStartupRemainIsolated()
    {
        await using var host = await ControlledLifecycleHost.StartAsync(
            () => throw new InvalidOperationException("OpenAPI cannot access the store."), () => false);
        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith(Root, StringComparison.Ordinal));
        Assert.Equal(new[] { Root + "/{module}/activation", Root + "/{module}/reactivation" },
            paths.Select(path => path.Name).Order(StringComparer.Ordinal));
        foreach (var path in paths)
        {
            var post = Assert.Single(path.Value.EnumerateObject());
            Assert.Equal("post", post.Name);
            var requirement = post.Value.GetProperty("parameters").EnumerateArray();
            Assert.Contains(requirement, parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("required").GetBoolean());
        }

        using var production = new WebApplicationFactory<Program>();
        var services = production.Services;
        var routes = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(routes, endpoint =>
            endpoint.RoutePattern.RawText?.TrimStart('/').StartsWith(Root.TrimStart('/'), StringComparison.OrdinalIgnoreCase) == true);
        var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.DoesNotContain(schemes, scheme => scheme.Name.Contains("test", StringComparison.OrdinalIgnoreCase));
        var bearer = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotEqual("andril-lifecycle-test", bearer.TokenValidationParameters.ValidIssuer);
        Assert.Null(services.GetService<IModuleLifecycleStore>());
    }

    private static async Task<HttpResponseMessage> Post(ControlledLifecycleHost host, string operation, string body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root + "/learning/" + operation)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));
        return await host.Client.SendAsync(request);
    }

    private sealed class LengthlessJsonContent : HttpContent
    {
        private readonly byte[] bytes;

        internal LengthlessJsonContent(string body)
        {
            bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class OfflineStore : IModuleLifecycleStore
    {
        private readonly Account account = Account.Create(Subject, Now);
        private StoredModuleMembership? current;
        private readonly Dictionary<Guid, ModuleLifecycleReceipt> receipts = [];
        private ModuleMembershipScope Scope => new(account.Id, "learning");
        internal int EffectiveWrites { get; private set; }

        internal void SeedInactive() => current = new(new InactiveModuleMembership(Scope, 2), Now, Now);

        public Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid requestKey,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Subject, subject);
            Assert.Equal("learning", module);
            var version = Guid.NewGuid();
            var policy = new PolicyDocument("current-test-version", "https://example.invalid", "en");
            var evidence = new ModuleActivationEvidence(Scope, ModuleAuthorizationOutcome.Satisfied, account, subject,
                AccountDemographics.Create(account.Id, new DateOnly(2000, 1, 1), "prefer_not_to_say", Now),
                new(policy, policy, policy),
                [
                    AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, policy.Version, Now),
                    AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, policy.Version, Now)
                ], true, false, version, version, Now);
            var decision = decide(new(evidence, current, receipts.GetValueOrDefault(requestKey)));
            if (decision.Write is { } write)
            {
                receipts.Add(requestKey, write.Receipt);
                if (write.ChangedMembership is { } changed)
                {
                    current = changed;
                    EffectiveWrites++;
                }
            }
            return Task.FromResult(decision.Result);
        }

        public Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid requestKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The offline fixture does not simulate database commit ambiguity.");
    }
}
