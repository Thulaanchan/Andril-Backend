using System.Net;
using System.Net.Http.Json;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class SocialMatchingHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task PersonalityConfirmationRequiresCurrentCompletedQuestionnaireAndKeepsChoicesPrivate()
    {
        await using var database = await SocialLifecycleDatabase.CreateAsync();
        await SocialMatchingRuntimeCompositionTests.GrantMatchingAsync(database);
        await using var db = database.Database.Open();
        using var names = new Npgsql.NpgsqlCommandBuilder();
        var role = names.QuoteIdentifier(database.Role);
        var privileges = $"""
            GRANT EXECUTE ON FUNCTION andril_social.read_social_matching_personality(uuid,uuid,integer),
                andril_social.set_social_archetype_confirmation(uuid,uuid,integer,integer,integer),
                andril_social.set_social_personality_preferences(uuid,text,text,text) TO {role}
            """;
        await db.Database.ExecuteSqlRawAsync(privileges);
        var owner = await SeedAsync(database);
        var other = await SeedAsync(database);
        using var host = new Host(database, questionnaire: true, personality: true);
        using var first = Client(host, owner);
        using var second = Client(host, other);
        const string personality = "/api/v1/social/matching/personality";
        const string questionnaire = "/api/v1/social/questionnaires/responses";
        await SetPreferencesAsync(first);
        await Expect(await first.PutAsJsonAsync(personality + "/preferences", new { social = 2, communication = 2, planning = 2 }), HttpStatusCode.NoContent);
        await SetPreferencesAsync(first);
        Assert.Equal(2, (await Json(await first.GetAsync(personality + "/me"))).GetProperty("partnerPreferences").GetProperty("social").GetInt32());

        var submission = new
        {
            version = QuestionnaireV1Definition.Version, skip = false,
            answers = Enumerable.Range(1, 12).Select(index => new { questionId = $"q{index:00}", optionId = "3" }).ToArray()
        };
        await Expect(await first.PostAsJsonAsync(questionnaire, submission), HttpStatusCode.OK);
        var before = await Json(await first.GetAsync(personality + "/me"));
        Assert.Equal(3, before.GetProperty("unresolvedAxes").GetArrayLength());
        Assert.False(before.GetProperty("hasConfirmation").GetBoolean());
        var responseId = before.GetProperty("questionnaireResponseId").GetGuid();
        var confirmation = new { questionnaireResponseId = responseId, social = 1, communication = 1, planning = 1 };
        await Expect(await second.PutAsJsonAsync(personality + "/confirmation", confirmation), HttpStatusCode.Conflict);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SELECT andril_social.social_safety_lock()");
            var paused = first.PutAsJsonAsync(personality + "/confirmation", confirmation, deadline.Token);
            await using var observer = database.Database.Open();
            while (!await observer.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                if (paused.IsCompleted) throw new InvalidOperationException("Confirmation did not wait for the evidence fence.");
                await Task.Delay(20, deadline.Token);
            }
            deadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paused);
            await transaction.RollbackAsync();
            Assert.Equal(0, await observer.Database.SqlQueryRaw<int>("""
                SELECT count(*)::integer AS "Value" FROM andril_social.social_archetype_confirmations
                """).SingleAsync());
        }
        await Expect(await first.PutAsJsonAsync(personality + "/confirmation", confirmation), HttpStatusCode.NoContent);
        var after = await Json(await first.GetAsync(personality + "/me"));
        Assert.Empty(after.GetProperty("unresolvedAxes").EnumerateArray());
        Assert.True(after.GetProperty("hasConfirmation").GetBoolean());
        Assert.DoesNotContain("optionId", after.GetRawText());

        await Expect(await second.PostAsJsonAsync(questionnaire, new { version = QuestionnaireV1Definition.Version, skip = true, answers = Array.Empty<object>() }),
            HttpStatusCode.OK);
        var skipped = await Json(await second.GetAsync(personality + "/me"));
        Assert.False(skipped.GetProperty("hasConfirmation").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, skipped.GetProperty("questionnaireResponseId").ValueKind);
        var rankingActor = await SeedAsync(database);
        var alternative = await SeedAsync(database);
        using var ranking = Client(host, rankingActor);
        using var inferred = Client(host, alternative);
        foreach (var client in new[] { first, ranking, inferred })
        {
            await SetPreferencesAsync(client);
            await SetLocationAsync(client, 6.92m, 79.86m);
        }
        await Expect(await ranking.PutAsJsonAsync(personality + "/preferences",
            new { social = 2, communication = 2, planning = 2 }), HttpStatusCode.NoContent);
        await Expect(await ranking.PostAsJsonAsync(questionnaire, submission), HttpStatusCode.OK);
        await Expect(await inferred.PostAsJsonAsync(questionnaire, submission with
        {
            answers = Enumerable.Range(1, 12).Select(index => new { questionId = $"q{index:00}", optionId = "1" }).ToArray()
        }), HttpStatusCode.OK);
        Assert.False((await Json(await inferred.GetAsync(personality + "/me"))).GetProperty("hasConfirmation").GetBoolean());
        var cards = await Json(await ranking.GetAsync(Explore + "?limit=2"));
        Assert.Equal(2, cards.GetArrayLength());
        Assert.Equal(owner.Profile, cards[0].GetProperty("candidateProfileId").GetGuid());
        Assert.DoesNotContain("hasConfirmation", cards.GetRawText());

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_social.social_questionnaire_current_responses WHERE profile_id = {owner.Profile}");
        Assert.False((await Json(await first.GetAsync(personality + "/me"))).GetProperty("hasConfirmation").GetBoolean());
        await Expect(await first.PutAsJsonAsync(personality + "/confirmation", confirmation), HttpStatusCode.Conflict);
    }
}
