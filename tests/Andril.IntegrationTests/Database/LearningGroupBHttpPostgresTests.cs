using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Extensions;
using Andril.API.Integrations.Learning;
using Andril.Learning.Activities;
using Andril.Learning.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningGroupBHttpPostgresTests
{
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.Authorization.ToString() != "Fixture"
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "isolated-group-b-owner")], Scheme.Name)), Scheme.Name)));
    }
    private static async Task<WebApplication> Start(LearningGroupBPostgresFixture f)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" }); b.WebHost.UseTestServer();
        b.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("Fixture", _ => { });
        b.Services.AddAuthorization(); b.Services.AddSingleton(f.Home); b.Services.AddSingleton(f.People()); b.Services.AddSingleton(f.Classrooms());
        b.Services.AddSingleton<ILearningActivitiesRepository>(f.Connections());
        var app = b.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControlledLearningHomeDiscovery(); app.MapControlledLearningActivities();
        await app.StartAsync(); return app;
    }
    private static HttpClient Client(WebApplication app)
    { var c = app.GetTestClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Fixture"); return c; }

    [LocalPostgresFact]
    public async Task HomePeopleAndClassroomsReadThroughActualStorageWithMinimalInertOutput()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); var person = f.Evidence(name: "<script>private ordinary display</script>");
        var classroom = f.Evidence("Classroom", "<script>ordinary classroom</script>");
        await using (var db = await f.BoundOwner()) { db.AddRange(person, classroom); await db.SaveChangesAsync(); }
        await using var app = await Start(f); using var client = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/learning/home")).StatusCode);
        var people = await client.GetAsync("/api/learning/people?query=ordinary"); Assert.Equal(HttpStatusCode.OK, people.StatusCode);
        var text = await people.Content.ReadAsStringAsync(); Assert.DoesNotContain("<script>", text); Assert.Contains("\\u003C", text);
        using (var json = JsonDocument.Parse(text)) Assert.Equal(person.TargetId, Assert.Single(json.RootElement.GetProperty("people").EnumerateArray()).GetProperty("profileId").GetGuid());
        var classes = await client.GetAsync("/api/learning/classrooms?query=ordinary"); Assert.Equal(HttpStatusCode.OK, classes.StatusCode);
        var body = await classes.Content.ReadAsStringAsync(); Assert.DoesNotContain("<script>", body); Assert.DoesNotContain(f.Activities.Shared.Actor().UserId.ToString(), body);
        using var result = JsonDocument.Parse(body); Assert.Equal(classroom.TargetId, Assert.Single(result.RootElement.GetProperty("classrooms").EnumerateArray()).GetProperty("id").GetGuid());
    }
    [LocalPostgresFact]
    public async Task EveryGroupBEndpointRequiresAuthenticationAndUnknownClientIdentityIsRejected()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); await using var app = await Start(f); using var anonymous = app.GetTestClient();
        foreach (var path in new[] { "/api/learning/home", "/api/learning/people?query=test", "/api/learning/classrooms?query=test", "/api/learning/activities/Connection" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var client = Client(app);
        using var request = new StringContent("{\"accountId\":\"" + Guid.NewGuid() + "\",\"module\":\"dating\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/learning/activities/Connection", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/learning/people?query=test&limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/learning/classrooms?query=test&limit=0")).StatusCode);
    }
    [LocalPostgresFact]
    public async Task ConnectionHttpUsesApprovedParticipantRolesAndSafeConflictDenialAndUnavailableOutcomes()
    {
        await using var f = await LearningGroupBPostgresFixture.Create(); await using var app = await Start(f); using var client = Client(app);
        var response = await client.PostAsJsonAsync("/api/learning/activities/Connection", f.ConnectRequest()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var activity = json.RootElement.GetProperty("activity");
        var id = activity.GetProperty("id").GetGuid(); var rev = activity.GetProperty("revision").Deserialize<LearningActivityRevision>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/learning/activities/Connection", f.ConnectRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/learning/activities/Connection/{id}/commands", new ChangeLearningActivityRequest(LearningActivityAction.Accept, rev))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/learning/activities/Connection/{id}/commands", new ChangeLearningActivityRequest(LearningActivityAction.Cancel, rev with { Version = 999 }))).StatusCode);
        f.Pairs.Blocked = true; var denied = await client.GetAsync($"/api/learning/activities/Connection/{id}"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain(id.ToString(), await denied.Content.ReadAsStringAsync()); f.Pairs.Blocked = false; f.Pairs.Unavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/learning/activities/Connection/{id}")).StatusCode);
    }
    [LocalPostgresFact]
    public async Task RevokedHomeAndDiscoveryAuthorityNeverReturnProtectedContentOrPrivateExceptions()
    {
        await using var f = await LearningGroupBPostgresFixture.Create();
        await using (var db = await f.BoundOwner()) { db.AddRange(f.Evidence(), f.Evidence("Classroom")); await db.SaveChangesAsync(); }
        await using var app = await Start(f); using var client = Client(app);
        f.Owner.Complete = false; Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/learning/home")).StatusCode);
        f.Sources.Throw = true;
        foreach (var path in new[] { "/api/learning/people?query=visible", "/api/learning/classrooms?query=visible" })
        {
            var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("PRIVATE_BROWSE_SOURCE_FAILURE", body); Assert.DoesNotContain("visible name", body);
        }
        f.Owner.Revoked = true; Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/learning/people?query=visible")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/learning/classrooms?query=visible")).StatusCode);
    }
}
