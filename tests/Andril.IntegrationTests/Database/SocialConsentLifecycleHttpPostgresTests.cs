using System.Net;
using System.Net.Http.Json;
using Andril.IntegrationTests.Lifecycle;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

[Collection("ControlledLifecyclePostgreSQL")]
public sealed class SocialConsentLifecycleHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task ControlledActivationDeactivationAndReactivationFenceThePreviousConsentEpoch()
    {
        await using var fixture = await ControlledLifecycleDatabase.CreateAsync();
        fixture.Authority.SocialOptIn = true;
        var options = new DbContextOptionsBuilder<SocialDbContext>().UseNpgsql(fixture.MigrationConnection,
            provider => provider.MigrationsHistoryTable(SocialDbContextFactory.MigrationsHistoryTableName, SocialDbContext.Schema)).Options;
        await using var db = new SocialDbContext(options);
        await db.Database.MigrateAsync();
        var profile = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_starter_memberships (account_id, module, activated_at_utc, social_opt_in_at_utc)
            VALUES ({fixture.AccountId}, 'social', statement_timestamp(), statement_timestamp());
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
            VALUES ({profile}, {fixture.AccountId}, 'social', statement_timestamp());
            """);

        await using var host = await ControlledLifecycleHost.StartAsync(fixture);
        var activation = Guid.NewGuid();
        await Transition("activation", activation, new { socialOptIn = true }, HttpStatusCode.Unauthorized);
        host.Authenticate(fixture.Subject);
        await Transition("activation", activation, new { socialOptIn = true });
        var original = await EpochAsync();
        Assert.True(await IsCurrentAsync(original));
        await Transition("activation", activation, new { socialOptIn = true });
        Assert.Equal(original, await EpochAsync());

        var operation = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social_ai.consents(operation_id, account_id, profile_id, conversation_id, session_epoch, module_epoch,
                purpose_id, purpose_version, scope_id, notice_revision, state, revision, offered_at_utc)
            VALUES ({operation}, {fixture.AccountId}, {profile}, gen_random_uuid(), gen_random_uuid(), {original},
                'fixture', 'v1', 'self', 'fixture', 1, gen_random_uuid(), statement_timestamp());
            """);
        var deactivation = Guid.NewGuid();
        await Transition("deactivation", deactivation, new { expectedRevision = 1 });
        Assert.False(await IsCurrentAsync(original));
        Assert.False(await IsCurrentAsync(await EpochAsync()));
        Assert.Equal(6, await db.Database.SqlQuery<int>($"""
            SELECT state AS "Value" FROM andril_social_ai.consents WHERE operation_id = {operation}
            """).SingleAsync());
        await Transition("reactivation", Guid.NewGuid(), new { socialOptIn = true, expectedRevision = 2 });
        var replacement = await EpochAsync();
        Assert.NotEqual(original, replacement);
        Assert.False(await IsCurrentAsync(original));
        Assert.True(await IsCurrentAsync(replacement));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value" FROM andril_social_ai.consent_invalidations WHERE operation_id = {operation}
            """).SingleAsync());

        Task<Guid> EpochAsync() => db.Database.SqlQuery<Guid>($"""
            SELECT epoch AS "Value" FROM andril_social_ai.module_epochs WHERE account_id = {fixture.AccountId}
            """).SingleAsync();
        Task<bool> IsCurrentAsync(Guid epoch) => db.Database.SqlQuery<bool>($"""
            SELECT andril_social_ai.is_current_module_epoch({fixture.AccountId}, {profile}, {epoch}) AS "Value"
            """).SingleAsync();

        async Task Transition(string action, Guid key, object body, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/account/modules/social/{action}") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key.ToString("D"));
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }
}
