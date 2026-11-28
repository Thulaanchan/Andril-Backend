using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class SocialMatchingHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task SharedRuntimeJoinsAZoneThenAppliesSocialRevocationWithoutGrantingCommunication()
    {
        await using var database = await OpenAsync();
        await using var groups = await SharedGroupsDatabase.CreateAsync(sharedDatabase: database.Database);
        var owner = await SeedAsync(database);
        var applicant = await SeedAsync(database);
        var learner = await SeedAsync(database, module: "learning");
        var interest = Guid.NewGuid();
        await using var db = database.Database.Open();
        await GrantCombinedMatchingAsync(database);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT andril_social.provision_social_interest({interest}, 'Isolated runtime catalogue entry', true);
            INSERT INTO andril_social.social_moderation_grants(account_id) VALUES ({owner.Account});
            """);
        using var host = new Host(database, questionnaire: true, interestSelections: true, personality: true, groupRole: groups.Role);
        using var first = Client(host, owner);
        using var second = Client(host, applicant);
        using var otherModule = Client(host, learner);
        await SetPreferencesAsync(first);
        await SetPreferencesAsync(second);

        const string questionnaire = Social + "/questionnaires/responses";
        await Expect(await first.PostAsJsonAsync(questionnaire, new
        {
            version = QuestionnaireV1Definition.Version,
            skip = false,
            answers = Enumerable.Range(1, 12).Select(i => new { questionId = $"q{i:00}", optionId = "3" })
        }), HttpStatusCode.OK);
        await Json(await first.GetAsync(questionnaire + "/me"));
        await Expect(await otherModule.GetAsync(questionnaire + "/me"), HttpStatusCode.Forbidden);
        const string personality = Social + "/matching/personality";
        await Expect(await first.PutAsJsonAsync(personality + "/preferences", new { social = 2, communication = 2, planning = 2 }), HttpStatusCode.NoContent);
        var archetype = await Json(await first.GetAsync(personality + "/me"));
        await Expect(await first.PutAsJsonAsync(personality + "/confirmation", new
        {
            questionnaireResponseId = archetype.GetProperty("questionnaireResponseId").GetGuid(), social = 1, communication = 1, planning = 1
        }), HttpStatusCode.NoContent);
        Assert.True((await Json(await first.GetAsync(personality + "/me"))).GetProperty("hasConfirmation").GetBoolean());

        const string interests = Social + "/matching/interests";
        Assert.Single((await Json(await first.GetAsync(interests + "/catalogue"))).EnumerateArray());
        foreach (var client in new[] { first, second })
        {
            await Expect(await client.PutAsJsonAsync(interests + "/me", new { interestIds = new[] { interest } }), HttpStatusCode.NoContent);
            await SetLocationAsync(client, 6.92m, 79.86m);
        }
        var card = Assert.Single((await Json(await first.GetAsync(Explore))).EnumerateArray());
        Assert.Equal(applicant.Profile, card.GetProperty("candidateProfileId").GetGuid());
        Assert.DoesNotContain(interest.ToString(), card.GetRawText());
        var responses = await Task.WhenAll(Like(first, applicant), Like(second, owner));
        var match = Assert.Single(await Task.WhenAll(responses.Select(Json)), state => state.GetProperty("matchCreated").GetBoolean());
        var matchId = match.GetProperty("matchId").GetGuid();
        Assert.Equal((2L, 1L), await RelationshipCounts(database));

        const string root = "/api/v1/groups";
        const string scope = "?module=social&groupKind=3";
        using var creation = await first.PostAsJsonAsync(root, new { module = "social", groupKind = 3, name = "Shared Zone", isDiscoverable = true, visibility = "public" });
        await Expect(creation, HttpStatusCode.Created);
        var groupId = (await creation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var group = $"{root}/{groupId}";
        Assert.Single((await Json(await second.GetAsync(root + "/discover" + scope))).EnumerateArray());
        await Expect(await otherModule.GetAsync(group + scope), HttpStatusCode.Forbidden);
        await Expect(await second.PostAsJsonAsync(group + "/join-requests", new { module = "social", groupKind = 3 }), HttpStatusCode.OK);
        await Expect(await second.GetAsync(group + "/admissions" + scope), HttpStatusCode.Forbidden);
        var pending = Assert.Single((await Json(await first.GetAsync(group + "/admissions" + scope))).EnumerateArray());
        var approved = await Json(await first.PostAsJsonAsync(group + "/admissions", new
        {
            module = "social", groupKind = 3, approve = true, requestId = pending.GetProperty("requestId").GetGuid()
        }));
        Assert.True(approved.GetProperty("membershipCreated").GetBoolean());
        using var transport = await second.PostAsync($"{Social}/matches/{matchId}/communication-evidence", null);
        using var generation = await second.PostAsJsonAsync(Social + "/ai/recommendations", new { });
        Assert.Equal(HttpStatusCode.NotFound, transport.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, generation.StatusCode);

        var block = $"{Social}/safety/blocks/{applicant.Profile}";
        await Expect(await first.PutAsync(block, null), HttpStatusCode.NoContent);
        await Expect(await second.GetAsync(Status(owner)), HttpStatusCode.Forbidden);
        Assert.Empty((await Json(await second.GetAsync(root + "/discover" + scope))).EnumerateArray());
        Assert.Equal((2L, 0L), await RelationshipCounts(database));
        Assert.Equal(2L, await groups.CountAsync("memberships"));
        await Expect(await first.DeleteAsync(block), HttpStatusCode.NoContent);
        Assert.False((await Json(await second.GetAsync(Status(owner)))).GetProperty("hasActiveMatch").GetBoolean());
        Assert.Equal(1, (await Json(await second.GetAsync(group + "/participation/me" + scope))).GetProperty("state").GetInt32());
        await Expect(await first.PutAsJsonAsync($"{Social}/safety/restrictions/{applicant.Profile}", new { restricted = true }), HttpStatusCode.NoContent);
        await Expect(await Like(first, applicant), HttpStatusCode.Forbidden);
        Assert.Empty((await Json(await first.GetAsync(Explore))).EnumerateArray());
    }

    private static async Task GrantCombinedMatchingAsync(SocialLifecycleDatabase database)
    {
        await using var db = database.Database.Open();
        using var identifiers = new NpgsqlCommandBuilder();
        var role = identifiers.QuoteIdentifier(database.Role);
        var grants = $"""
            GRANT EXECUTE ON FUNCTION andril_social.read_social_interest_catalogue(uuid), andril_social.read_own_social_interests(uuid),
                andril_social.replace_own_social_interests(uuid,uuid[]), andril_social.read_social_matching_interests(uuid,uuid,integer),
                andril_social.read_social_matching_personality(uuid,uuid,integer),
                andril_social.set_social_archetype_confirmation(uuid,uuid,integer,integer,integer),
                andril_social.set_social_personality_preferences(uuid,text,text,text) TO {role};
            """;
        await db.Database.ExecuteSqlRawAsync(grants);
    }
}
