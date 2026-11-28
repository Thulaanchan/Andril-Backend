using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static Andril.IntegrationTests.Database.CanonicalEntitlementDeploymentPostgresTests;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsCanonicalRuntimePostgresTests
{
    [LocalPostgresFact]
    public async Task AllGroupModulesKeepExistingGroupsAcrossCanonicalGrantRenewalExpiryAndRevocation()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        using var host = new SharedGroupsHttpHost(fixture, hobbies: true);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await using var db = fixture.Open();
        const string path = "/api/v1/groups";

        foreach (var scope in new GroupModuleScope[] { new("learning", GroupKind.Classroom),
            new("hobbies", GroupKind.Club), new("social", GroupKind.Zone) })
        {
            var actor = await fixture.SeedAsync(scope.Module);
            if (scope.GroupKind == GroupKind.Classroom) await fixture.GrantEducatorAsync(actor);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "confirmed-" + actor.Subject);
            var payload = new { module = scope.Module, groupKind = (int)scope.GroupKind, name = "Canonical Group", isDiscoverable = false };
            Guid original = Guid.Empty;
            for (var index = 0; index < 5; index++)
            {
                using var created = await client.PostAsJsonAsync(path, payload);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                original = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            }
            await AssertCreationAsync(HttpStatusCode.Conflict);
            Assert.Equal(50, await CapacityAsync());

            var revision = await ProvisionAsync(null, 1, DateTimeOffset.UtcNow.AddHours(1));
            Assert.Equal(100, await CapacityAsync());
            await AssertCreationAsync(HttpStatusCode.Created);
            var expiry = DateTimeOffset.UtcNow.AddSeconds(3);
            revision = await ProvisionAsync(revision, 2, expiry);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_sleep(greatest(0, extract(epoch FROM ({expiry} - clock_timestamp()))))");
            Assert.Equal(50, await CapacityAsync());
            await AssertCreationAsync(HttpStatusCode.Conflict);
            Assert.Equal(revision.UtcDateTime, await db.Database.SqlQuery<DateTime>($"""
                SELECT updated_at_utc AS "Value" FROM andril_economy.premium_entitlements WHERE account_id = {actor.Account}
                """).SingleAsync());
            revision = await ProvisionAsync(revision, 1, DateTimeOffset.UtcNow.AddHours(2));
            Assert.Equal(100, await CapacityAsync());
            await AssertCreationAsync(HttpStatusCode.Created);
            await ProvisionAsync(revision, 0, null);
            Assert.Equal(50, await CapacityAsync());
            await AssertCreationAsync(HttpStatusCode.Conflict);
            Assert.NotNull(await fixture.WithEntriesAsync(actor, entry => entry.GetGroupAsync(original, scope)));

            Task<int?> CapacityAsync() => fixture.WithScopeAsync(actor, execution =>
                new PostgresGroupEvidenceProvider(execution).GetGroupCapacityAsync(original, scope));

            async Task<DateTimeOffset> ProvisionAsync(DateTimeOffset? previous, int tier, DateTimeOffset? end)
            {
                await using var provisioner = new NpgsqlConnection(fixture.ProvisioningConnection);
                return await ApplyAsync(fixture, actor.Subject, Guid.NewGuid(), previous, tier, end, transactionConnection: provisioner);
            }

            async Task AssertCreationAsync(HttpStatusCode expected)
            {
                using var result = await client.PostAsJsonAsync(path, payload);
                Assert.Equal(expected, result.StatusCode);
            }
        }
        Assert.Equal(21L, await fixture.CountAsync("groups"));
        Assert.Equal(21L, await fixture.CountAsync("memberships"));
        Assert.Equal(12L, await db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM andril_economy.premium_entitlement_transitions WHERE invoked_by::text = {fixture.ProvisionerRole}
            """).SingleAsync());
    }

    [LocalPostgresFact]
    public async Task StartupDeniesAGroupRoleThatCanTransitionSubscriptions()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        await using var authority = fixture.Open();
        var revoke = $"""
            REVOKE EXECUTE ON FUNCTION andril_economy.apply_subscription_transition(uuid, uuid, timestamptz, integer, timestamptz, text, text)
                FROM {fixture.ProvisionerRole};
            """;
        await authority.Database.ExecuteSqlRawAsync(revoke);
        using var missingProvisioner = new SharedGroupsHttpHost(fixture, hobbies: true);
        Assert.Contains("restricted canonical entitlement installation", Assert.Throws<InvalidOperationException>(() => missingProvisioner.CreateClient()).Message);
        var grants = $"""
            GRANT USAGE ON SCHEMA andril_economy TO {fixture.Role};
            GRANT EXECUTE ON FUNCTION andril_economy.apply_subscription_transition(uuid, uuid, timestamptz, integer, timestamptz, text, text)
                TO {fixture.Role}, {fixture.ProvisionerRole};
            """;
        await authority.Database.ExecuteSqlRawAsync(grants);
        using var host = new SharedGroupsHttpHost(fixture, hobbies: true);
        Assert.Contains("privileges are invalid", Assert.Throws<InvalidOperationException>(() => host.CreateClient()).Message);
    }
}
