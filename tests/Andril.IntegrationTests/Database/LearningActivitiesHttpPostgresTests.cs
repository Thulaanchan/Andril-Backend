using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Extensions;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningActivitiesHttpPostgresTests
{
    private sealed record Envelope(string Outcome, LearningActivityView? Activity);
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.Authorization.ToString() != "Synthetic"
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "synthetic-fixture-owner")], Scheme.Name)), Scheme.Name)));
    }
    private sealed class Capture : ILoggerProvider
    {
        internal ConcurrentQueue<(string Category, string Text)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(string category, ConcurrentQueue<(string Category, string Text)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, formatter(state, exception) + (exception?.ToString() ?? "")));
        }
    }
    private static async Task<WebApplication> Start(LearningActivitiesPostgresFixture fixture, Capture? capture = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("Synthetic", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ILearningActivitiesRepository>(fixture.Repository());
        if (capture is not null) builder.Logging.AddProvider(capture).AddFilter<Capture>("Microsoft.AspNetCore", LogLevel.Information);
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControlledLearningActivities();
        await app.StartAsync(); return app;
    }
    private static HttpClient Client(WebApplication app)
    {
        var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic"); return client;
    }

    [LocalPostgresFact]
    public async Task AuthenticatedControlledHttpCreatesAndReopensEverySupportedKindWithoutIdentityDisclosure()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); await using var app = await Start(f); using var client = Client(app);
        foreach (var kind in Enum.GetValues<LearningActivityKind>())
        {
            var response = await client.PostAsJsonAsync($"/api/learning/activities/{kind}", f.Request(kind));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); var item = await response.Content.ReadFromJsonAsync<Envelope>();
            Assert.Equal("Saved", item!.Outcome); Assert.NotNull(item.Activity);
            var read = await client.GetAsync($"/api/learning/activities/{kind}/{item.Activity.Id}"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var text = await read.Content.ReadAsStringAsync(); Assert.DoesNotContain(f.Shared.Actor().UserId.ToString(), text);
            Assert.DoesNotContain(f.Peer.AccountId.ToString(), text); Assert.DoesNotContain(f.Peer.ProfileId.ToString(), text);
            var reopened = await read.Content.ReadFromJsonAsync<Envelope>(); Assert.Equal(item.Activity.Revision, reopened!.Activity!.Revision);
            var list = await client.GetAsync($"/api/learning/activities/{kind}?limit=1"); Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(item.Activity.Id, Assert.Single(page.RootElement.GetProperty("activities").EnumerateArray()).GetProperty("id").GetGuid());
        }
    }

    [LocalPostgresFact]
    public async Task AuthenticationAndClientIdentityRejectionHappenBeforeRepositoryMutation()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); await using var app = await Start(f);
        using (var anonymous = app.GetTestClient())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/learning/activities/GrowthSelfReport", new CreateLearningActivityRequest())).StatusCode);
        Assert.Equal(0, f.Authority.Calls);
        using var client = Client(app);
        var payload = new StringContent("{\"accountId\":\"" + Guid.NewGuid() + "\",\"skillIds\":[]}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/learning/activities/GrowthSelfReport", payload)).StatusCode);
        var module = new StringContent("{\"module\":\"dating\",\"skillIds\":[]}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/learning/activities/GrowthSelfReport", module)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/learning/activities/GrowthSelfReport",
            new CreateLearningActivityRequest(Commitments: ["an unapproved evidence body must not be discarded or persisted"]))).StatusCode);
        await using var owner = await f.BoundOwner(); Assert.Empty(await owner.Set<LearningActivityRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task StaleRevisionsDuplicatesAndRevokedAuthorityUseSafeDistinctHttpOutcomes()
    {
        await using var f = await LearningActivitiesPostgresFixture.Create(); await using var app = await Start(f); using var client = Client(app);
        var request = f.Request(LearningActivityKind.SkillExchange);
        var response = await client.PostAsJsonAsync("/api/learning/activities/SkillExchange", request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<Envelope>())!.Activity!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/learning/activities/SkillExchange", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/learning/activities/SkillExchange/{item.Id}/commands",
            new ChangeLearningActivityRequest(LearningActivityAction.Cancel, item.Revision with { Version = 99 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/learning/activities/SkillExchange/{item.Id}/commands",
            new ChangeLearningActivityRequest(LearningActivityAction.Accept, item.Revision))).StatusCode);
        f.Authority.Denied = true;
        var denied = await client.GetAsync($"/api/learning/activities/SkillExchange/{item.Id}"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain(item.OfferedSkillId!.Value.ToString(), await denied.Content.ReadAsStringAsync());
        f.Authority.Denied = false; f.Authority.Unavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/learning/activities/SkillExchange/{item.Id}")).StatusCode);
    }

    [LocalPostgresFact]
    public async Task InfrastructureExceptionsStayConfidentialWhileFrameworkLoggingRemainsEffective()
    {
        const string secret = "PRIVATE_ACTIVITY_AUTHORITY_FAILURE";
        await using var f = await LearningActivitiesPostgresFixture.Create(); using var capture = new Capture();
        f.Authority.BeforeAuthorize = _ => throw new InvalidOperationException(secret);
        await using var app = await Start(f, capture); using var client = Client(app);
        var response = await client.PostAsJsonAsync("/api/learning/activities/GrowthSelfReport", f.Request(LearningActivityKind.GrowthSelfReport));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync());
        Assert.Contains(capture.Entries, x => x.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Entries, x => x.Text.Contains(secret, StringComparison.Ordinal));
    }
}
