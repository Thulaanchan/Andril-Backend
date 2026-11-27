using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Andril.API.Authentication;
using Andril.Persistence.DbContexts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using QuestionnaireHarness = Andril.IntegrationTests.Database.SocialQuestionnaireHttpPostgresTests;

namespace Andril.IntegrationTests.Database;

public sealed class SocialMatchingHttpPostgresTests
{
    private const string Social = "/api/v1/social";
    private const string Explore = Social + "/explore";
    private const string Preferences = Social + "/matching/preferences";
    private const string Location = Social + "/location";
    private const string Relationships = Social + "/relationships";
    private const string Interest = Relationships + "/interest";

    [LocalPostgresFact]
    public async Task DiscoveryUsesOnlyConsentedDistanceAndWithdrawalSuppressesTheCard()
    {
        await using var database = await OpenAsync();
        var caller = await SeedAsync(database);
        var candidate = await SeedAsync(database);
        using var host = new Host(database, questionnaire: true);
        using var first = Client(host, caller);
        using var second = Client(host, candidate);
        await SetPreferencesAsync(first);
        await SetPreferencesAsync(second);
        await Expect(await second.PostAsJsonAsync(Social + "/questionnaires/responses", new
        {
            version = Andril.Social.Personality.QuestionnaireV1Definition.Create().Version,
            skip = true,
            answers = Array.Empty<object>()
        }), HttpStatusCode.OK);
        await Expect(await first.PutAsJsonAsync(Location, new { latitude = 6.912345m, longitude = 79.911111m, consent = false }), HttpStatusCode.BadRequest);
        await SetLocationAsync(first, 6.912345m, 79.911111m);
        Assert.Empty((await Json(await first.GetAsync(Explore))).EnumerateArray());
        await SetLocationAsync(second, 7.021234m, 79.923456m);

        var card = Assert.Single((await Json(await first.GetAsync(Explore + "?limit=2&rotationOffset=0"))).EnumerateArray());
        Assert.Equal(candidate.Profile, card.GetProperty("candidateProfileId").GetGuid());
        Assert.Equal(11, card.GetProperty("approximateDistanceKm").GetInt32());
        Assert.Equal(new[] { "age", "approximateDistanceKm", "candidateProfileId", "displayName", "sharedConnectionGoals", "sharedLanguages" },
            card.EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain(candidate.Account.ToString(), card.GetRawText());
        Assert.DoesNotContain("79.923456", card.GetRawText());
        await SetPreferencesAsync(second, maxDistance: 5);
        Assert.Empty((await Json(await first.GetAsync(Explore))).EnumerateArray());
        await SetPreferencesAsync(second);

        await Expect(await second.DeleteAsync(Location), HttpStatusCode.NoContent);
        Assert.Empty((await Json(await first.GetAsync(Explore))).EnumerateArray());
        await using var context = database.Database.Open();
        Assert.Equal(1L, await context.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM andril_social.social_location_evidence WHERE latitude_cell IS NULL AND longitude_cell IS NULL
            """).SingleAsync());
        var interest = await Json(await Like(first, candidate));
        Assert.True(interest.GetProperty("success").GetBoolean());
    }

    [LocalPostgresFact]
    public async Task RelationshipRequestsKeepInitialInterestPrivateAndRevokeAcrossRequestScopes()
    {
        await using var database = await OpenAsync();
        var first = await SeedAsync(database);
        var second = await SeedAsync(database);
        using var host = new Host(database);
        using var a = Client(host, first);
        using var b = Client(host, second);
        await SetPreferencesAsync(a);
        await SetPreferencesAsync(b);
        var firstWrite = await Json(await Like(a, second));
        Assert.Equal("PendingUnilateral", firstWrite.GetProperty("visibleState").GetString());
        Assert.False((await Json(await b.GetAsync(Status(first)))).GetProperty("hasActiveMatch").GetBoolean());
        var match = await Json(await Like(b, first));
        Assert.True(match.GetProperty("matchCreated").GetBoolean());
        var id = match.GetProperty("matchId").GetGuid();
        using var independent = Client(host, first);
        Assert.Equal(id, (await Json(await independent.GetAsync(Status(second)))).GetProperty("matchId").GetGuid());
        var duplicate = await Json(await Like(a, second));
        Assert.False(duplicate.GetProperty("matchCreated").GetBoolean());
        Assert.Equal(id, duplicate.GetProperty("matchId").GetGuid());
        Assert.True((await Json(await a.PostAsync($"{Relationships}/{second.Profile}/revoke", null))).GetProperty("matchRevoked").GetBoolean());
        Assert.False((await Json(await b.DeleteAsync($"{Relationships}/{first.Profile}"))).GetProperty("matchRevoked").GetBoolean());
        Assert.False((await Json(await b.GetAsync(Status(first)))).GetProperty("hasActiveMatch").GetBoolean());
        Assert.Equal((2L, 0L), await RelationshipCounts(database));
    }

    [LocalPostgresFact]
    public async Task ConcurrentHttpInterestsCreateOneMatchAndBlockRevokesIt()
    {
        await using var database = await OpenAsync();
        var first = await SeedAsync(database);
        var second = await SeedAsync(database);
        using var host = new Host(database);
        using var a = Client(host, first);
        using var b = Client(host, second);
        await SetPreferencesAsync(a);
        await SetPreferencesAsync(b);
        var responses = await Task.WhenAll(Like(a, second), Like(b, first));
        var payloads = await Task.WhenAll(responses.Select(Json));
        Assert.Single(payloads, payload => payload.GetProperty("matchCreated").GetBoolean());
        Assert.Equal((2L, 1L), await RelationshipCounts(database));

        await Expect(await a.PutAsync($"{Social}/safety/blocks/{second.Profile}", null), HttpStatusCode.NoContent);
        await Expect(await b.GetAsync(Status(first)), HttpStatusCode.Forbidden);
        await Expect(await b.DeleteAsync($"{Social}/safety/blocks/{first.Profile}"), HttpStatusCode.NoContent);
        await Expect(await Like(b, first), HttpStatusCode.Forbidden);
        await Expect(await a.DeleteAsync($"{Social}/safety/blocks/{second.Profile}"), HttpStatusCode.NoContent);
        Assert.False((await Json(await b.GetAsync(Status(first)))).GetProperty("hasActiveMatch").GetBoolean());
        Assert.Equal((2L, 0L), await RelationshipCounts(database));
    }

    [LocalPostgresFact]
    public async Task IdentityPeerStandingAndModerationGrantsAreRequired()
    {
        await using var database = await OpenAsync();
        var actor = await SeedAsync(database);
        var target = await SeedAsync(database);
        var hobbies = await SeedAsync(database, module: "hobbies");
        var minor = await SeedAsync(database, minor: true);
        var inactive = await SeedAsync(database, active: false);
        using var host = new Host(database);
        using var anonymous = Client(host);
        using var unconfirmed = Client(host, actor, confirmed: false);
        using var authorized = Client(host, actor);
        using var otherModule = Client(host, hobbies);
        await Expect(await anonymous.GetAsync(Explore), HttpStatusCode.Unauthorized);
        await Expect(await Like(unconfirmed, target), HttpStatusCode.Forbidden);
        await Expect(await Like(otherModule, target), HttpStatusCode.Forbidden);
        await Expect(await Like(authorized, minor), HttpStatusCode.Forbidden);
        await Expect(await Like(authorized, inactive), HttpStatusCode.Forbidden);
        await Expect(await Like(authorized, actor), HttpStatusCode.Forbidden);
        await Expect(await Like(authorized, hobbies), HttpStatusCode.Forbidden);
        using var targetClient = Client(host, target);
        await SetPreferencesAsync(targetClient);
        var path = $"{Social}/safety/restrictions/{target.Profile}";
        await Expect(await authorized.PutAsJsonAsync(path, new { restricted = true }), HttpStatusCode.Forbidden);
        await using (var context = database.Database.Open())
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_social.social_moderation_grants(account_id) VALUES ({actor.Account})");
        await Expect(await authorized.PutAsJsonAsync(path, new { restricted = true }), HttpStatusCode.NoContent);
        await Expect(await Like(authorized, target), HttpStatusCode.Forbidden);
        await Expect(await authorized.PutAsJsonAsync(path, new { restricted = false }), HttpStatusCode.NoContent);
        await Expect(await Like(authorized, target), HttpStatusCode.OK);
    }

    [LocalPostgresFact]
    public async Task StrictBodiesAndOptInRoutesMatchDevelopmentOpenApi()
    {
        await using var database = await OpenAsync();
        var actor = await SeedAsync(database);
        using var off = new Host(database, enabled: false);
        using var inactive = Client(off, actor);
        await Expect(await inactive.GetAsync(Explore), HttpStatusCode.NotFound);
        await Expect(await inactive.DeleteAsync(Location), HttpStatusCode.NotFound);
        using var on = new Host(database);
        using var client = Client(on, actor);
        await Expect(await client.PostAsJsonAsync(Interest, new { targetProfileId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        using var duplicate = new StringContent("{\"targetProfileId\":\"" + Guid.NewGuid() + "\",\"isPositive\":true,\"isPositive\":false}", Encoding.UTF8, "application/json");
        await Expect(await client.PostAsync(Interest, duplicate), HttpStatusCode.BadRequest);
        await Expect(await client.PostAsJsonAsync(Interest, new { targetProfileId = Guid.NewGuid(), isPositive = true, actorId = actor.Profile }), HttpStatusCode.BadRequest);
        var openApi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = openApi.GetProperty("paths");
        (string Path, string Method)[] operations =
        [
            (Explore, "get"), (Interest, "post"), (Relationships + "/{targetProfileId}/status", "get"),
            (Relationships + "/{targetProfileId}/revoke", "post"), (Relationships + "/{targetProfileId}", "delete"),
            (Preferences, "put"), (Location, "put"), (Location, "delete"),
            (Social + "/safety/blocks/{targetProfileId}", "put"), (Social + "/safety/blocks/{targetProfileId}", "delete"),
            (Social + "/safety/restrictions/{targetProfileId}", "put")
        ];
        foreach (var (path, method) in operations)
            Assert.True(paths.GetProperty(path).GetProperty(method).GetProperty("responses").TryGetProperty("503", out _));
        var documented = paths.EnumerateObject()
            .Where(path => operations.Any(operation => operation.Path == path.Name))
            .Sum(path => path.Value.EnumerateObject().Count(member => member.Name is "get" or "post" or "put" or "delete"));
        Assert.Equal(operations.Length, documented);
    }

    [LocalPostgresFact]
    public async Task RealDatabaseOutageReturnsSafeServiceUnavailable()
    {
        await using var database = await OpenAsync();
        var actor = await SeedAsync(database);
        using var host = new Host(database);
        using var client = Client(host, actor);
        var name = database.Database.DatabaseName;
        using var quoter = new NpgsqlCommandBuilder();
        var sqlName = quoter.QuoteIdentifier(name);
        await using var maintenance = await SocialLifecycleDatabase.OpenMaintenanceAsync();
        await using var disable = new NpgsqlCommand($"ALTER DATABASE {sqlName} WITH ALLOW_CONNECTIONS false", maintenance);
        await disable.ExecuteNonQueryAsync();
        try
        {
            using var response = await client.GetAsync(Explore);
            await Expect(response, HttpStatusCode.ServiceUnavailable);
            Assert.Equal("Social matching storage is unavailable.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        }
        finally
        {
            await using var enable = new NpgsqlCommand($"ALTER DATABASE {sqlName} WITH ALLOW_CONNECTIONS true", maintenance);
            await enable.ExecuteNonQueryAsync();
        }
    }

    [LocalPostgresFact]
    public async Task CancelledHttpInterestLeavesNoDirectionalOrMatchRows()
    {
        await using var database = await OpenAsync();
        var first = await SeedAsync(database);
        var second = await SeedAsync(database);
        var paused = new PauseInterest();
        using var host = new Host(database, pause: paused);
        using var a = Client(host, first);
        using var b = Client(host, second);
        await SetPreferencesAsync(b);
        using var cancellation = new CancellationTokenSource();
        var request = a.PostAsJsonAsync(Interest, new { targetProfileId = second.Profile, isPositive = true }, cancellation.Token);
        try
        {
            var signaled = await Task.WhenAny(request, paused.Entered.Task).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Same(paused.Entered.Task, signaled);
        }
        finally { await cancellation.CancelAsync(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await paused.Exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((0L, 0L), await RelationshipCounts(database));
    }

    private static async Task<SocialLifecycleDatabase> OpenAsync()
    {
        var database = await SocialLifecycleDatabase.CreateAsync();
        try { await SocialMatchingRuntimeCompositionTests.GrantMatchingAsync(database); return database; }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task<SocialLifecycleDatabase.Actor> SeedAsync(SocialLifecycleDatabase database,
        bool active = true, bool minor = false, string module = "social")
    {
        var actor = await QuestionnaireHarness.SeedAsync(database, active: active, minor: minor, module: module);
        await using var account = database.Database.OpenAccounts();
        await account.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_demographics SET gender = 'female' WHERE account_id = {actor.Account};
            UPDATE andril_identity.private_module_profiles SET display_name = {"Social-" + actor.Profile.ToString("N")[..8]} WHERE id = {actor.Profile};
            """);
        return actor;
    }

    private static Task<HttpResponseMessage> Like(HttpClient client, SocialLifecycleDatabase.Actor target) =>
        client.PostAsJsonAsync(Interest, new { targetProfileId = target.Profile, isPositive = true });

    private static string Status(SocialLifecycleDatabase.Actor target) => $"{Relationships}/{target.Profile}/status";

    private static async Task SetPreferencesAsync(HttpClient client, int maxDistance = 100) => await Expect(
        await client.PutAsJsonAsync(Preferences, new { discoverable = true, showMe = "All", minAge = 18, maxAge = 99,
            minDistanceKm = 0, maxDistanceKm = maxDistance, languages = new[] { "Tamil" }, connectionGoals = new[] { "Friendship" } }),
        HttpStatusCode.NoContent);

    private static async Task SetLocationAsync(HttpClient client, decimal latitude, decimal longitude) => await Expect(
        await client.PutAsJsonAsync(Location, new { latitude, longitude, consent = true }), HttpStatusCode.NoContent);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using (response)
        {
            await Expect(response, HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static Task Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return Task.CompletedTask;
    }

    private static async Task<(long Interests, long ActiveMatches)> RelationshipCounts(SocialLifecycleDatabase database)
    {
        await using var context = database.Database.Open();
        return (await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_directional_interests").SingleAsync(),
            await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_social.social_matches WHERE state = 'active'").SingleAsync());
    }

    private static HttpClient Client(Host host, SocialLifecycleDatabase.Actor? actor = null, bool confirmed = true)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (actor is not null) client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (confirmed ? "confirmed-" : "unconfirmed-") + actor.Subject);
        return client;
    }

    private sealed class Host(SocialLifecycleDatabase database, bool enabled = true, PauseInterest? pause = null, bool questionnaire = false)
        : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var settings = SocialMatchingRuntimeCompositionTests.Settings(database);
            settings["SocialMatching:EnablePersistence"] = enabled.ToString();
            settings["SocialQuestionnaire:EnablePersistence"] = questionnaire.ToString();
            settings["SocialQuestionnaire:RuntimeRole"] = database.Role;
            foreach (var (name, version) in new[] { ("Terms", "fixture-terms-v1"), ("Privacy", "fixture-privacy-v1"), ("CommunityGuidelines", "fixture-community-v1") })
            {
                settings[$"AccountPolicies:{name}:Version"] = version;
                settings[$"AccountPolicies:{name}:Language"] = "en";
                settings[$"AccountPolicies:{name}:Url"] = $"https://example.invalid/{name}";
            }
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                const string scheme = "MatchingFixture";
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = scheme;
                    options.DefaultChallengeScheme = scheme;
                    options.DefaultForbidScheme = scheme;
                }).AddScheme<AuthenticationSchemeOptions, QuestionnaireHarness.FixtureAuthentication>(scheme, _ => { });
                services.RemoveAll<ISupabaseUserVerifier>();
                services.AddSingleton<ISupabaseUserVerifier, QuestionnaireHarness.FixtureConfirmation>();
                services.AddSingleton<IStartupFilter, QuestionnaireHarness.Loopback>();
                if (pause is not null) services.ConfigureDbContext<SocialDbContext>(options => options.AddInterceptors(pause));
            });
        }
    }

    private sealed class PauseInterest : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO andril_social.social_directional_interests", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Exited.TrySetResult(); }
            }
            return result;
        }
    }
}
