using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsHttpPostgresTests
{
    private const string Groups = "/api/v1/groups";
    private const string ClubScope = "?module=hobbies&groupKind=2";
    private static readonly object Club = new { module = "hobbies", groupKind = 2 };

    [LocalPostgresFact]
    public async Task AuthenticatedClubJourneyPersistsSettingsAdmissionAndOwnerOnlyParticipation()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        var applicant = await fixture.SeedAsync("hobbies");
        var classroomActor = await fixture.SeedAsync("learning");
        using var server = new SharedGroupsHttpHost(fixture, hobbies: true);
        using var owner = Client(server, host);
        using var member = Client(server, applicant);
        using var learner = Client(server, classroomActor);

        var response = await owner.PostAsJsonAsync(Groups, new
            { module = "hobbies", groupKind = 2, name = "Club", isDiscoverable = true, visibility = "public" });
        Assert.NotNull(response.Headers.Location);
        var created = await Json(response, HttpStatusCode.Created);
        var group = $"{Groups}/{created.GetProperty("id").GetGuid()}";
        var details = await Json(await member.GetAsync(group + ClubScope), HttpStatusCode.OK);
        Assert.Equal("Club", details.GetProperty("name").GetString());
        var settings = new { module = "hobbies", groupKind = 2, name = "Updated Club", isDiscoverable = true,
            visibility = "public", expectedRevision = 1 };
        await Status(await member.PutAsJsonAsync(group + "/configuration", settings), HttpStatusCode.Forbidden);
        await Status(await owner.PutAsJsonAsync(group + "/configuration", settings), HttpStatusCode.OK);
        var conflict = await Json(await owner.PutAsJsonAsync(group + "/configuration", settings), HttpStatusCode.Conflict);
        Assert.Equal("revision_conflict", conflict.GetProperty("code").GetString());
        var listed = await Json(await member.GetAsync(Groups + "/discover" + ClubScope + "&search=updated&maximum=1"), HttpStatusCode.OK);
        Assert.Equal("Updated Club", Assert.Single(listed.EnumerateArray()).GetProperty("name").GetString());
        await Status(await learner.GetAsync(group + "?module=learning&groupKind=1"), HttpStatusCode.Forbidden);
        Assert.Equal(0, (await Json(await member.GetAsync(group + "/participation/me" + ClubScope), HttpStatusCode.OK)).GetProperty("state").GetInt32());

        var attempts = await Task.WhenAll(member.PostAsJsonAsync(group + "/join-requests", Club),
            member.PostAsJsonAsync(group + "/join-requests", Club));
        using var accepted = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        using var duplicate = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.True(accepted.Headers.CacheControl?.NoStore);
        Assert.True(duplicate.Headers.CacheControl?.NoStore);
        Assert.Equal(1L, await fixture.CountAsync("join_requests"));
        Assert.Equal(2, (await Json(await member.GetAsync(group + "/participation/me" + ClubScope), HttpStatusCode.OK)).GetProperty("state").GetInt32());
        await Status(await member.GetAsync(group + "/admissions" + ClubScope), HttpStatusCode.Forbidden);
        await Status(await Delete(member, group + "/join-requests"), HttpStatusCode.OK);
        await Status(await member.PostAsJsonAsync(group + "/join-requests", Club), HttpStatusCode.OK);
        await Status(await member.PostAsJsonAsync(group + "/join-requests/cancel", Club), HttpStatusCode.OK);

        await Status(await member.PostAsJsonAsync(group + "/join-requests", Club), HttpStatusCode.OK);
        var first = await Pending();
        await Status(await owner.PostAsJsonAsync(group + "/admissions", new
            { module = "hobbies", groupKind = 2, requestId = first, approve = false }), HttpStatusCode.OK);
        await Status(await member.PostAsJsonAsync(group + "/join-requests", Club), HttpStatusCode.OK);
        var current = await Pending();
        Assert.NotEqual(first, current);
        var admission = await Json(await owner.PostAsJsonAsync(group + "/admissions", new
            { module = "hobbies", groupKind = 2, requestId = current, approve = true }), HttpStatusCode.OK);
        Assert.True(admission.GetProperty("membershipCreated").GetBoolean());
        Assert.Equal(1, (await Json(await member.GetAsync(group + "/participation/me" + ClubScope), HttpStatusCode.OK)).GetProperty("state").GetInt32());
        await Status(await Delete(member, group + "/members/me"), HttpStatusCode.OK);
        await Status(await member.PostAsJsonAsync(group + "/leave", Club), HttpStatusCode.Conflict);
        await using var persisted = fixture.Open();
        Assert.Equal(1L, await persisted.Database.SqlQueryRaw<long>("""
            SELECT count(*) AS "Value" FROM andril_groups.memberships WHERE departed_at_utc IS NULL
            """).SingleAsync());

        async Task<Guid> Pending()
        {
            var pending = Assert.Single((await Json(await owner.GetAsync(group + "/admissions" + ClubScope), HttpStatusCode.OK)).EnumerateArray());
            Assert.Equal(2, pending.EnumerateObject().Count());
            Assert.DoesNotContain(applicant.Profile.ToString(), pending.GetRawText());
            return pending.GetProperty("requestId").GetGuid();
        }
    }

    [LocalPostgresFact]
    public async Task ConfirmedAuthorityStrictPayloadsAndScopedBlocksControlClassroomsAndZones()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var administrator = await fixture.SeedAsync("learning");
        var faculty = await fixture.SeedAsync("learning");
        var learner = await fixture.SeedAsync("learning");
        var adult = await fixture.SeedAsync("social");
        var minor = await fixture.SeedAsync("social", minor: true);
        await using var db = fixture.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_groups.moderator_grants(account_id,module) VALUES ({administrator.Account}, 'learning')
            """);
        using var server = new SharedGroupsHttpHost(fixture);
        using var adminClient = Client(server, administrator);
        using var host = Client(server, faculty);
        using var member = Client(server, learner);
        using var social = Client(server, adult);
        using var underage = Client(server, minor);
        using var unconfirmed = Client(server, faculty, confirmed: false);
        using var anonymous = Client(server);
        var classroom = new { module = "learning", groupKind = 1, name = "Classroom", isDiscoverable = true, visibility = "public" };
        await Status(await anonymous.PostAsJsonAsync(Groups, classroom), HttpStatusCode.Unauthorized);
        await Status(await unconfirmed.PostAsJsonAsync(Groups, classroom), HttpStatusCode.Forbidden);
        await Status(await host.PostAsJsonAsync(Groups, classroom), HttpStatusCode.Forbidden);
        var educator = $"{Groups}/learning/educators/{faculty.Profile}";
        await Status(await host.PutAsJsonAsync(educator, new { enabled = true }), HttpStatusCode.Forbidden);
        await Status(await adminClient.PutAsJsonAsync(educator, new { enabled = true }), HttpStatusCode.NoContent);

        await Status(await host.PostAsJsonAsync(Groups, new { module = "learning", groupKind = 1, name = "Missing discoverability" }), HttpStatusCode.BadRequest);
        await Status(await host.PostAsJsonAsync(Groups, new { module = "learning", groupKind = 1, name = "Extra ownership", isDiscoverable = true,
            ownerProfileId = faculty.Profile }), HttpStatusCode.BadRequest);
        using var duplicate = new StringContent("""{"module":"learning","Module":"hobbies","groupKind":1,"name":"duplicate","isDiscoverable":true}""",
            Encoding.UTF8, "application/json");
        await Status(await host.PostAsync(Groups, duplicate), HttpStatusCode.BadRequest);
        using var nonJson = new StringContent("classroom", Encoding.UTF8, "text/plain");
        await Status(await host.PostAsync(Groups, nonJson), HttpStatusCode.UnsupportedMediaType);
        var result = await Json(await host.PostAsJsonAsync(Groups, classroom), HttpStatusCode.Created);
        var group = $"{Groups}/{result.GetProperty("id").GetGuid()}";
        const string scope = "?module=learning&groupKind=1";
        await Status(await member.GetAsync(group + scope), HttpStatusCode.OK);
        var path = $"{Groups}/safety/learning/blocks/{faculty.Profile}";
        await Status(await member.PutAsJsonAsync(path, new { enabled = true }), HttpStatusCode.NoContent);
        Assert.Empty((await Json(await member.GetAsync(Groups + "/discover" + scope), HttpStatusCode.OK)).EnumerateArray());
        await Status(await member.GetAsync(group + "/participation/me" + scope), HttpStatusCode.Forbidden);
        await Status(await member.PutAsJsonAsync(path, new { enabled = false }), HttpStatusCode.NoContent);
        var restriction = $"{Groups}/safety/learning/restrictions/{learner.Profile}";
        await Status(await host.PutAsJsonAsync(restriction, new { enabled = true }), HttpStatusCode.Forbidden);
        await Status(await adminClient.PutAsJsonAsync(restriction, new { enabled = true }), HttpStatusCode.NoContent);
        await Status(await member.GetAsync(group + scope), HttpStatusCode.Forbidden);
        await Status(await adminClient.PutAsJsonAsync(educator, new { enabled = false }), HttpStatusCode.NoContent);
        await Status(await host.PostAsJsonAsync(Groups, classroom), HttpStatusCode.Forbidden);

        var zone = new { module = "social", groupKind = 3, name = "Zone", isDiscoverable = false };
        await Status(await underage.PostAsJsonAsync(Groups, zone), HttpStatusCode.Forbidden);
        var zoneResult = await Json(await social.PostAsJsonAsync(Groups, zone), HttpStatusCode.Created);
        Assert.Equal("private", zoneResult.GetProperty("visibility").GetString());
        await Status(await social.GetAsync(Groups + "/discover" + ClubScope), HttpStatusCode.Forbidden);
    }

    [LocalPostgresFact]
    public async Task DisabledRoutesAndActualDatabaseOutageReturnNonDisclosingResponses()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var host = await fixture.SeedAsync("hobbies");
        using var disabled = new SharedGroupsHttpHost(fixture, enabled: false);
        using var inactive = Client(disabled, host);
        await Status(await inactive.GetAsync(Groups + "/discover" + ClubScope), HttpStatusCode.NotFound);
        using var enabled = new SharedGroupsHttpHost(fixture, hobbies: true);
        using var client = Client(enabled, host);
        Assert.Empty((await Json(await client.GetAsync(Groups + "/discover" + ClubScope), HttpStatusCode.OK)).EnumerateArray());
        var name = new NpgsqlConnectionStringBuilder(fixture.Database.ConnectionString).Database;
        Assert.Matches("^andril_social_test_[a-f0-9]{32}$", name!);
        await using var maintenance = await SocialLifecycleDatabase.OpenMaintenanceAsync();
        await using var change = new NpgsqlCommand($"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS false", maintenance);
        await change.ExecuteNonQueryAsync();
        try
        {
            var error = await Json(await client.GetAsync(Groups + "/discover" + ClubScope), HttpStatusCode.ServiceUnavailable);
            Assert.Equal("Group authority is unavailable.", error.GetProperty("title").GetString());
            Assert.DoesNotContain(name!, error.GetRawText());
            await Status(await client.PostAsJsonAsync(Groups, new { module = "hobbies", groupKind = 2, name = "Outage", isDiscoverable = false }),
                HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            change.CommandText = $"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS true";
            await change.ExecuteNonQueryAsync();
        }
        Assert.Equal(0L, await fixture.CountAsync("groups"));
    }

    [LocalPostgresFact]
    public async Task DevelopmentOpenApiExposesOnlyTheActivatedGroupOperations()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        using var host = new SharedGroupsHttpHost(fixture, hobbies: true);
        using var client = Client(host);
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var operations = document.GetProperty("paths").EnumerateObject()
            .Where(route => route.Name.StartsWith(Groups, StringComparison.Ordinal))
            .SelectMany(route => route.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "post" or "put" or "delete")
                .Select(operation => operation.Name + " " + route.Name[Groups.Length..]))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "delete /{groupId}/join-requests", "delete /{groupId}/members/me", "get /discover", "get /{groupId}",
            "get /{groupId}/admissions", "get /{groupId}/participation/me", "post ", "post /{groupId}/admissions",
            "post /{groupId}/join-requests", "post /{groupId}/join-requests/cancel", "post /{groupId}/leave",
            "put /learning/educators/{profileId}", "put /safety/{module}/blocks/{profileId}",
            "put /safety/{module}/restrictions/{profileId}", "put /{groupId}/configuration"
        }.Order(StringComparer.Ordinal), operations);
    }

    private static HttpClient Client(SharedGroupsHttpHost server, SharedGroupsDatabase.Actor? actor = null, bool confirmed = true)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (actor is not null) client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (confirmed ? "confirmed-" : "unconfirmed-") + actor.Subject);
        return client;
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path) { Content = JsonContent.Create(Club) };
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static Task Status(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        return Task.CompletedTask;
    }
}
