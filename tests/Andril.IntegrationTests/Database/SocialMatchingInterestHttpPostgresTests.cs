using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class SocialMatchingHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task CatalogueSelectionsArePrivateValidatedAndContributeToLiveMatching()
    {
        await using var database = await SocialLifecycleDatabase.CreateAsync();
        await SocialMatchingRuntimeCompositionTests.GrantMatchingAsync(database);
        await using var db = database.Database.Open();
        using var quoted = new Npgsql.NpgsqlCommandBuilder();
        var role = quoted.QuoteIdentifier(database.Role);
        var privileges = $"""
            GRANT EXECUTE ON FUNCTION andril_social.read_social_interest_catalogue(uuid), andril_social.read_own_social_interests(uuid),
                andril_social.replace_own_social_interests(uuid,uuid[]), andril_social.read_social_matching_interests(uuid,uuid,integer) TO {role}
            """;
        await db.Database.ExecuteSqlRawAsync(privileges);
        var shared = Guid.NewGuid();
        var different = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT andril_social.provision_social_interest({shared}, 'Fixture shared interest', true);
            SELECT andril_social.provision_social_interest({different}, 'Fixture different interest', true)
            """);
        var caller = await SeedAsync(database);
        var match = await SeedAsync(database);
        var other = await SeedAsync(database);
        using var host = new Host(database, interestSelections: true);
        using var first = Client(host, caller);
        using var second = Client(host, match);
        using var third = Client(host, other);
        using var unconfirmed = Client(host, caller, confirmed: false);
        const string catalogue = "/api/v1/social/matching/interests/catalogue";
        const string selection = "/api/v1/social/matching/interests/me";
        Assert.Equal(2, (await Json(await first.GetAsync(catalogue))).GetArrayLength());
        await Expect(await unconfirmed.GetAsync(selection), HttpStatusCode.Forbidden);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await Json(await first.GetAsync(selection))).GetProperty("interestIds").ValueKind);
        foreach (var client in new[] { first, second, third })
        {
            await SetPreferencesAsync(client);
            await SetLocationAsync(client, 6.92m, 79.86m);
        }
        await Expect(await first.PutAsJsonAsync(selection, new { interestIds = new[] { shared, shared } }), HttpStatusCode.BadRequest);
        await Expect(await first.PutAsJsonAsync(selection, new { interestIds = new[] { Guid.NewGuid() } }), HttpStatusCode.BadRequest);
        await Expect(await first.PutAsJsonAsync(selection, new { interestIds = new[] { shared } }), HttpStatusCode.NoContent);
        await Expect(await second.PutAsJsonAsync(selection, new { interestIds = new[] { shared } }), HttpStatusCode.NoContent);
        await Expect(await third.PutAsJsonAsync(selection, new { interestIds = new[] { different } }), HttpStatusCode.NoContent);
        var selected = await Json(await first.GetAsync(selection));
        Assert.Equal(shared, Assert.Single(selected.GetProperty("interestIds").EnumerateArray()).GetGuid());
        await Expect(await first.PutAsJsonAsync(selection, new { interestIds = new[] { shared } }), HttpStatusCode.NoContent);
        Assert.Equal(selected.GetProperty("revision").GetInt64(), (await Json(await first.GetAsync(selection))).GetProperty("revision").GetInt64());

        var cards = await Json(await first.GetAsync(Explore + "?limit=2"));
        Assert.Equal(2, cards.GetArrayLength());
        Assert.Equal(match.Profile, cards[0].GetProperty("candidateProfileId").GetGuid());
        Assert.DoesNotContain(shared.ToString(), cards.GetRawText());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT andril_social.provision_social_interest({shared}, 'Fixture shared interest', false)");
        var retired = await Json(await first.GetAsync(selection));
        Assert.Empty(retired.GetProperty("interestIds").EnumerateArray());
        Assert.True(retired.GetProperty("revision").GetInt64() > selected.GetProperty("revision").GetInt64());
        await Expect(await first.PutAsJsonAsync(selection, new { interestIds = new[] { shared } }), HttpStatusCode.BadRequest);
    }
}
