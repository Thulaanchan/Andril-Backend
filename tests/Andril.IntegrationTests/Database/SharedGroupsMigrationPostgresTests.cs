using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsMigrationPostgresTests
{
    [Fact]
    public void GroupsModelMatchesItsDedicatedMigrationSnapshot()
    {
        using var groups = new GroupsDbContextFactory().CreateDbContext([]);
        Assert.False(groups.Database.HasPendingModelChanges());
        Assert.EndsWith("_InitialSharedGroups", Assert.Single(groups.Database.GetMigrations()));
        Assert.All(groups.Model.GetEntityTypes(), row => Assert.Equal(GroupsDbContext.Schema, row.GetSchema()));
    }

    [LocalPostgresFact]
    public async Task MigrationAndRollbackPreserveExistingProfilesAndEnforceExactModuleScope()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        await fixture.ApplyAccountsMigrationsAsync();
        await using var groups = Open(fixture);
        var classroomOwner = await SeedAsync(groups, "learning");
        var clubOwner = await SeedAsync(groups, "hobbies");
        var zoneOwner = await SeedAsync(groups, "social");
        await groups.Database.MigrateAsync();
        var classroom = await InsertGroupAsync(groups, classroomOwner.Profile, "learning", 1);
        var club = await InsertGroupAsync(groups, clubOwner.Profile, "hobbies", 2);
        var zone = await InsertGroupAsync(groups, zoneOwner.Profile, "social", 3);
        Assert.NotEqual(classroom, club);
        Assert.NotEqual(club, zone);

        var kind = await Assert.ThrowsAsync<PostgresException>(() => InsertGroupAsync(groups, clubOwner.Profile, "hobbies", 1));
        Assert.Equal("ck_groups_module_kind", kind.ConstraintName);
        var ownership = await Assert.ThrowsAsync<PostgresException>(() => InsertGroupAsync(groups, classroomOwner.Profile, "hobbies", 2));
        Assert.Equal("fk_groups_creator_profile", ownership.ConstraintName);
        var crossScope = await Assert.ThrowsAsync<PostgresException>(() => InsertMemberAsync(groups, club, "hobbies", zoneOwner.Profile));
        Assert.Equal("fk_groups_member_profile", crossScope.ConstraintName);
        await InsertMemberAsync(groups, club, "hobbies", clubOwner.Profile);
        var duplicateMember = await Assert.ThrowsAsync<PostgresException>(() => InsertMemberAsync(groups, club, "hobbies", clubOwner.Profile));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateMember.SqlState);
        await InsertRequestAsync(groups, club, "hobbies", clubOwner.Profile);
        var duplicateRequest = await Assert.ThrowsAsync<PostgresException>(() => InsertRequestAsync(groups, club, "hobbies", clubOwner.Profile));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateRequest.SqlState);

        await groups.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        Assert.False(await groups.Database.SqlQueryRaw<bool>("SELECT to_regclass('andril_groups.groups') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(3L, await groups.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_identity.private_module_profiles").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task RestrictedRoleSeesItsOwnRowsOnlyAndCannotForgeWritesOrReadAudit()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        await fixture.ApplyAccountsMigrationsAsync();
        await using var groups = Open(fixture);
        await groups.Database.MigrateAsync();
        var owner = await SeedAsync(groups, "social");
        var other = await SeedAsync(groups, "social");
        var first = await InsertGroupAsync(groups, owner.Profile, "social", 3);
        var second = await InsertGroupAsync(groups, other.Profile, "social", 3);
        await InsertMemberAsync(groups, first, "social", owner.Profile);
        await InsertMemberAsync(groups, second, "social", other.Profile);
        await InsertRequestAsync(groups, second, "social", other.Profile);

        await using var transaction = await groups.Database.BeginTransactionAsync();
        var role = "groups_schema_" + Guid.NewGuid().ToString("N");
        var grants = $"""
            CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
            GRANT {role} TO CURRENT_USER;
            GRANT USAGE ON SCHEMA andril_groups TO {role};
            GRANT SELECT ON andril_groups.groups, andril_groups.memberships, andril_groups.join_requests, andril_groups.audit TO {role};
            GRANT EXECUTE ON FUNCTION andril_groups.owns_current_profile(uuid, text) TO {role};
            SET LOCAL ROLE {role};
            """;
        await groups.Database.ExecuteSqlRawAsync(grants);
        Assert.Equal(0L, await CountGroupsAsync(groups));
        await groups.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('andril.subject', {owner.Subject.ToString()}, true), set_config('andril.account', {owner.Account.ToString()}, true),
                set_config('andril.module', 'social', true)
            """);
        Assert.Equal(1L, await CountGroupsAsync(groups));
        Assert.Equal(1L, await groups.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.memberships").SingleAsync());
        Assert.Equal(0L, await groups.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.join_requests").SingleAsync());
        Assert.Equal(0L, await groups.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.audit").SingleAsync());
        Assert.False(await groups.Database.SqlQueryRaw<bool>("SELECT has_table_privilege('andril_groups.groups','INSERT') AS \"Value\"").SingleAsync());
        await groups.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account', {other.Account.ToString()}, true)");
        Assert.Equal(0L, await CountGroupsAsync(groups));
        await transaction.RollbackAsync();
    }

    private static GroupsDbContext Open(LocalSocialQuestionnaireDatabase fixture) => new(new DbContextOptionsBuilder<GroupsDbContext>()
        .UseNpgsql(fixture.ConnectionString, options => options.MigrationsHistoryTable(GroupsDbContextFactory.MigrationsHistoryTableName, GroupsDbContext.Schema))
        .Options);

    private static Task<long> CountGroupsAsync(GroupsDbContext groups) =>
        groups.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.groups").SingleAsync();

    private static async Task<Guid> InsertGroupAsync(GroupsDbContext groups, Guid creator, string module, int kind)
    {
        var id = Guid.NewGuid();
        await groups.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_groups.groups(id, module, kind, creator_profile_id, name, visibility, is_discoverable, is_active, is_open, revision)
            VALUES ({id}, {module}, {kind}, {creator}, 'Test group', 'private', false, true, true, 1)
            """);
        return id;
    }

    private static Task<int> InsertMemberAsync(GroupsDbContext groups, Guid id, string module, Guid profile) =>
        groups.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_groups.memberships(group_id, module, profile_id, joined_at_utc) VALUES ({id}, {module}, {profile}, CURRENT_TIMESTAMP)
            """);

    private static Task<int> InsertRequestAsync(GroupsDbContext groups, Guid id, string module, Guid profile) =>
        groups.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_groups.join_requests(id, group_id, module, profile_id, state, requested_at_utc)
            VALUES ({Guid.NewGuid()}, {id}, {module}, {profile}, 'pending', CURRENT_TIMESTAMP)
            """);

    private static async Task<(Guid Account, Guid Subject, Guid Profile)> SeedAsync(GroupsDbContext groups, string module)
    {
        var account = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var profile = Guid.NewGuid();
        await groups.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({account}, {subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_demographics(account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                VALUES ({account}, DATE '1990-01-01', 'female', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({profile}, {account}, {module}, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({account}, {module}, CURRENT_TIMESTAMP, CASE WHEN {module} = 'social' THEN CURRENT_TIMESTAMP END);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({account}, {module}, 'active', 1);
            """);
        return (account, subject, profile);
    }
}
