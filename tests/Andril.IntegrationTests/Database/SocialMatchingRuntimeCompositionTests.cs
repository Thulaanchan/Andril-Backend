using Andril.API.Extensions;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialMatchingRuntimeCompositionTests
{
    [LocalPostgresFact]
    public async Task StartupRequiresVerifiedGrantsAndKeepsDistanceServicesSeparatelyOptIn()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        var initial = Compose(store, distances: false);
        await using (initial)
        {
            var rejected = Assert.Single(initial.GetServices<IHostedService>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.StartAsync(CancellationToken.None));
        }

        await GrantMatchingAsync(store);
        await using var accepted = Compose(store, distances: false);
        await Assert.Single(accepted.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
        await using var scope = accepted.CreateAsyncScope();
        Assert.Null(scope.ServiceProvider.GetService<Andril.Persistence.Repositories.Social.SocialLocationSettings>());
    }

    [LocalPostgresFact]
    public async Task QuestionnaireStartupAllowsOnlyItsOwnMigrationsWhenMatchingIsDisabled()
    {
        await using var store = await SocialLifecycleDatabase.CreateAsync();
        await using (var database = store.Database.Open())
            await database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync("20261127203424_SocialQuestionnaireLifecycle");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AndrilAccounts"] = store.Database.ConnectionString,
            ["SocialQuestionnaire:RuntimeRole"] = store.Role
        }).Build();
        var services = new ServiceCollection();
        services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql(store.Database.ConnectionString));
        services.AddVerifiedSocialQuestionnairePersistence(config);
        await using var provider = services.BuildServiceProvider();
        await Assert.Single(provider.GetServices<IHostedService>()).StartAsync(CancellationToken.None);
    }

    private static ServiceProvider Compose(SocialLifecycleDatabase store, bool distances)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Settings(store, distances)).Build();
        var services = new ServiceCollection();
        services.AddDbContext<AccountsDbContext>(options => options.UseNpgsql(store.Database.ConnectionString));
        services.AddVerifiedSocialMatchingPersistence(config);
        return services.BuildServiceProvider();
    }

    internal static Dictionary<string, string?> Settings(SocialLifecycleDatabase store, bool distances = true) => new()
    {
        ["ConnectionStrings:AndrilAccounts"] = store.Database.ConnectionString,
        ["SocialMatching:EnablePersistence"] = "true",
        ["SocialMatching:RuntimeRole"] = store.Role,
        ["SocialMatching:EnableDistanceDiscovery"] = distances.ToString(),
        ["SocialMatching:Location:CellSizeDegrees"] = "0.10",
        ["SocialMatching:Location:MaximumAge"] = "00:05:00"
    };

    internal static async Task GrantMatchingAsync(SocialLifecycleDatabase store)
    {
        await using var db = store.Database.Open();
        using var identifiers = new NpgsqlCommandBuilder();
        var role = identifiers.QuoteIdentifier(store.Role);
        var grants = $"""
            GRANT SELECT, INSERT, UPDATE (is_positive, recorded_at_utc) ON andril_social.social_directional_interests TO {role};
            GRANT SELECT, UPDATE (state, revoked_at_utc) ON andril_social.social_matches TO {role};
            GRANT SELECT, INSERT ON andril_social.social_matching_preferences, andril_social.social_location_evidence TO {role};
            GRANT UPDATE (discoverable, show_me, min_age, max_age, min_distance_km, max_distance_km, languages, connection_goals,
                partner_social, partner_communication, partner_planning, revision) ON andril_social.social_matching_preferences TO {role};
            GRANT UPDATE (latitude_cell, longitude_cell, observed_at_utc, revision) ON andril_social.social_location_evidence TO {role};
            GRANT EXECUTE ON FUNCTION andril_social.social_safety_lock(), andril_social.social_safety_decision(uuid, uuid),
                andril_social.set_social_block(uuid, boolean), andril_social.set_social_restriction(uuid, boolean),
                andril_social.social_reciprocal_interest(uuid, uuid), andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone),
                andril_social.read_social_matching_evidence(uuid, uuid, integer) TO {role};
            """;
        await db.Database.ExecuteSqlRawAsync(grants);
    }
}
