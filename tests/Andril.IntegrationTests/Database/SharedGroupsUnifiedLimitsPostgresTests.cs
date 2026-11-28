using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsUnifiedLimitsPostgresTests
{
    [LocalPostgresFact]
    public Task ClassroomsEnforceTheUnifiedLimitsOnPostgreSql() =>
        VerifyModuleAsync(new("learning", GroupKind.Classroom));

    [LocalPostgresFact]
    public Task ZonesRespectTheUnifiedLimitsOnPostgreSql() =>
        VerifyModuleAsync(new("social", GroupKind.Zone));

    [LocalPostgresFact]
    public async Task AuthenticatedClassroomAndZoneConflictsUseTheUnifiedContract()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        using var app = new SharedGroupsHttpHost(fixture);
        foreach (var scope in new GroupModuleScope[] { new("learning", GroupKind.Classroom), new("social", GroupKind.Zone) })
        {
            var host = await fixture.SeedAsync(scope.Module);
            var applicant = await fixture.SeedAsync(scope.Module);
            if (scope.GroupKind == GroupKind.Classroom) await fixture.GrantEducatorAsync(host);
            using var owner = Client(host);
            using var member = Client(applicant);
            const string groups = "/api/v1/groups";
            var payload = new { module = scope.Module, groupKind = (int)scope.GroupKind, name = "Group", isDiscoverable = true, visibility = "public" };
            Guid group = Guid.Empty;
            for (var index = 0; index < 5; index++)
            {
                using var created = await owner.PostAsJsonAsync(groups, payload);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                group = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            }
            await AssertConflict(await owner.PostAsJsonAsync(groups, payload), "creation_quota");

            await AddExistingMembersAsync(fixture, group, scope.Module, 49);
            var path = $"{groups}/{group}";
            using var request = await member.PostAsJsonAsync(path + "/join-requests", new { module = scope.Module, groupKind = (int)scope.GroupKind });
            Assert.Equal(HttpStatusCode.OK, request.StatusCode);
            var pending = Assert.Single((await owner.GetFromJsonAsync<JsonElement>(path + $"/admissions?module={scope.Module}&groupKind={(int)scope.GroupKind}")).EnumerateArray());
            await AssertConflict(await owner.PostAsJsonAsync(path + "/admissions", new
            {
                module = scope.Module, groupKind = (int)scope.GroupKind, approve = true, requestId = pending.GetProperty("requestId").GetGuid()
            }), "group_capacity");
        }

        HttpClient Client(SharedGroupsDatabase.Actor actor)
        {
            var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "confirmed-" + actor.Subject);
            return client;
        }

        static async Task AssertConflict(HttpResponseMessage response, string code)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
        }
    }

    [LocalPostgresFact]
    public async Task TheSameAccountsOtherModuleGroupsDoNotConsumeTheCurrentModuleQuota()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var actor = await fixture.SeedAsync("hobbies");
        var club = new GroupModuleScope("hobbies", GroupKind.Club);
        for (var index = 0; index < 5; index++)
            await fixture.CreateGroupAsync(actor, club, new("Club", null, false));
        var socialProfile = Guid.NewGuid();
        await using var db = fixture.Open();
        // Change the canonical starter selection inside the disposable fixture only.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({socialProfile}, {actor.Account}, 'social', clock_timestamp());
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({actor.Account}, 'social', 'active', 1);
            UPDATE andril_identity.account_starter_memberships SET module = 'social', social_opt_in_at_utc = activated_at_utc
                WHERE account_id = {actor.Account};
            """);
        var current = actor with { Profile = socialProfile };
        var zone = new GroupModuleScope("social", GroupKind.Zone);
        for (var index = 0; index < 5; index++)
            await fixture.CreateGroupAsync(current, zone, new("Zone", null, false));
        await Assert.ThrowsAsync<GroupQuotaReachedException>(() => fixture.CreateGroupAsync(current, zone, new("Sixth", null, false)));
        Assert.Equal(10L, await fixture.CountAsync("groups"));
    }

    [LocalPostgresFact]
    public async Task UnifiedLimitsMigrationRoundTripsWithoutReplacingExistingGroupRows()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var actor = await fixture.SeedAsync("social");
        var zone = new GroupModuleScope("social", GroupKind.Zone);
        var created = await fixture.CreateGroupAsync(actor, zone, new("Preserved", null, false));
        await using var db = fixture.Open();
        var migrations = db.Database.GetMigrations();
        var previous = migrations.Single(migration => migration.EndsWith("_CanonicalGroupEntitlement", StringComparison.Ordinal));
        var current = migrations.Single(migration => migration.EndsWith("_UnifiedGroupLimits", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        Assert.Equal("Preserved", (await fixture.WithEntriesAsync(actor, entry => entry.GetGroupAsync(created.Id, zone)))?.Name);
        await migrator.MigrateAsync(current);
        Assert.Equal("Preserved", (await fixture.WithEntriesAsync(actor, entry => entry.GetGroupAsync(created.Id, zone)))?.Name);
        Assert.Equal(1L, await fixture.CountAsync("groups"));
        Assert.Equal(1L, await fixture.CountAsync("memberships"));
    }

    private static async Task VerifyModuleAsync(GroupModuleScope scope)
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var creator = await fixture.SeedAsync(scope.Module);
        var first = await fixture.SeedAsync(scope.Module);
        var second = await fixture.SeedAsync(scope.Module);
        if (scope.GroupKind == GroupKind.Classroom) await fixture.GrantEducatorAsync(creator);
        var group = await Create();
        for (var index = 0; index < 3; index++) await Create();
        var attempts = await Task.WhenAll(CanCreate(), CanCreate());
        Assert.Single(attempts, success => success);
        Assert.Equal(5L, await fixture.CountAsync("groups"));
        Assert.False(await CanCreate());

        await AddExistingMembersAsync(fixture, group.Id, scope.Module, 48);
        await Submit(first);
        await Submit(second);
        var results = await Task.WhenAll(Approve(first), Approve(second));
        Assert.Single(results, result => result.MembershipCreated);
        Assert.Single(results, result => result.Status == GroupMembershipTransactionStatus.CapacityExceeded);

        var waiting = results[0].MembershipCreated ? second : first;
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(await CanCreate());
        await AddExistingMembersAsync(fixture, group.Id, scope.Module, 49);
        Assert.True((await Approve(waiting)).MembershipCreated);
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(await CanCreate());
        var afterExpiry = await fixture.SeedAsync(scope.Module);
        await Submit(afterExpiry);
        Assert.Equal(GroupMembershipTransactionStatus.CapacityExceeded, (await Approve(afterExpiry)).Status);
        await fixture.SetPremiumAsync(creator, DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(await CanCreate());
        Assert.Equal(GroupMembershipTransactionStatus.CapacityExceeded, (await Approve(afterExpiry)).Status);
        Assert.Equal(7L, await fixture.CountAsync("groups"));

        Task<StoredGroup> Create() => fixture.CreateGroupAsync(creator, scope, new("Group", null, true, "public"));
        async Task<bool> CanCreate()
        {
            try { await Create(); return true; }
            catch (GroupQuotaReachedException) { return false; }
        }
        Task<GroupJoinRequestSubmissionTransactionResult> Submit(SharedGroupsDatabase.Actor actor) =>
            fixture.WithScopeAsync(actor, execution => new PostgresGroupMembershipTransactionRepository(execution)
                .SubmitJoinRequestAtomicAsync(group.Id, scope, actor.Profile, DateTime.UtcNow));
        Task<GroupAdmissionTransactionResult> Approve(SharedGroupsDatabase.Actor applicant) =>
            fixture.WithScopeAsync(creator, execution => new PostgresGroupMembershipTransactionRepository(execution)
                .ProcessAdmissionAtomicAsync(group.Id, scope, applicant.Profile, true, creator.Profile, DateTime.UtcNow));
    }

    private static async Task AddExistingMembersAsync(SharedGroupsDatabase fixture, Guid group, string module, int count)
    {
        await using var db = fixture.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH accounts AS (
                INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                SELECT gen_random_uuid(), gen_random_uuid(), now(), now() FROM generate_series(1, {count}) RETURNING id
            ), profiles AS (
                INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                SELECT gen_random_uuid(), id, {module}, now() FROM accounts RETURNING id
            )
            INSERT INTO andril_groups.memberships(group_id, module, profile_id, joined_at_utc)
            SELECT {group}, {module}, id, now() FROM profiles
            """);
    }
}
