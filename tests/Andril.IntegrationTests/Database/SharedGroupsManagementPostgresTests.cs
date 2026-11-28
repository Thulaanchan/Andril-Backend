using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Groups;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SharedGroupsManagementPostgresTests
{
    [LocalPostgresFact]
    public async Task CreatorMembershipCapabilityAndConfigurationCommitWithTheGroup()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var creator = await fixture.SeedAsync("learning");
        var outsider = await fixture.SeedAsync("learning");
        var classroom = new GroupModuleScope("learning", GroupKind.Classroom);
        var settings = new GroupSettings("Classroom", null, false);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.CreateGroupAsync(creator, classroom, settings));
        await fixture.GrantEducatorAsync(creator);
        var created = await fixture.CreateGroupAsync(creator, classroom, settings);
        Assert.Equal("private", created.Visibility);
        Assert.False(created.IsDiscoverable);
        var updated = await fixture.WithEntriesAsync(creator,
            entries => entries.UpdateAsync(created.Id, classroom, settings with { Name = "Updated", IsDiscoverable = true }, created.Revision));
        Assert.Equal(2, updated?.Revision);
        Assert.Null(await fixture.WithEntriesAsync(outsider, entries => entries.GetGroupAsync(created.Id, classroom)));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.WithEntriesAsync(outsider,
            entries => entries.UpdateAsync(created.Id, classroom, settings, 2)));
        Assert.Equal("Updated", (await fixture.WithEntriesAsync(creator, entries => entries.GetGroupAsync(created.Id, classroom)))?.Name);
        Assert.Equal(1L, await fixture.CountAsync("memberships"));
        Assert.Equal(2L, await fixture.CountAsync("capabilities"));
        Assert.Equal(2L, await fixture.CountAsync("audit"));
    }

    [LocalPostgresFact]
    public async Task CanonicalEntitlementsFenceTheConcurrentFreeQuotaAndPremiumExpiry()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync();
        var actor = await fixture.SeedAsync("hobbies");
        var club = new GroupModuleScope("hobbies", GroupKind.Club);
        var settings = new GroupSettings("Club", null, true, "public");
        for (var index = 0; index < 4; index++) await fixture.CreateGroupAsync(actor, club, settings);
        async Task<bool> TryCreate()
        {
            try { await fixture.CreateGroupAsync(actor, club, settings); return true; }
            catch (GroupQuotaReachedException) { return false; }
        }
        Assert.Single(await Task.WhenAll(TryCreate(), TryCreate()), result => result);
        Assert.Equal(5L, await fixture.CountAsync("groups"));
        await fixture.SetPremiumAsync(actor, DateTimeOffset.UtcNow.AddDays(1));
        await fixture.CreateGroupAsync(actor, club, settings);
        await fixture.SetPremiumAsync(actor, DateTimeOffset.UtcNow.AddDays(-1));
        await Assert.ThrowsAsync<GroupQuotaReachedException>(() => fixture.CreateGroupAsync(actor, club, settings));
        Assert.Equal(6L, await fixture.CountAsync("groups"));
        Assert.Equal(6L, await fixture.CountAsync("memberships"));
    }

    [LocalPostgresFact]
    public async Task MissingEntitlementAuthorityDeniesClubsWhileTheZoneOwnerCanCreatePrivately()
    {
        await using var fixture = await SharedGroupsDatabase.CreateAsync(withEntitlementSource: false);
        var hobbies = await fixture.SeedAsync("hobbies");
        var social = await fixture.SeedAsync("social");
        var settings = new GroupSettings("Private group", null, false);
        await Assert.ThrowsAsync<GroupAuthorityUnavailableException>(() => fixture.CreateGroupAsync(hobbies,
            new GroupModuleScope("hobbies", GroupKind.Club), settings));
        var zone = await fixture.CreateGroupAsync(social, new GroupModuleScope("social", GroupKind.Zone), settings);
        Assert.Equal("private", zone.Visibility);
        Assert.Equal(1L, await fixture.CountAsync("groups"));
    }
}

internal sealed class SharedGroupsDatabase(LocalSocialQuestionnaireDatabase database, string role) : IAsyncDisposable
{
    internal LocalSocialQuestionnaireDatabase Database { get; } = database;
    internal string Role { get; } = role;
    internal sealed record Actor(Guid Account, Guid Subject, Guid Profile) : ICurrentUser, IOnboardingIdentity
    {
        public bool IsAuthenticated => true;
        public Guid? UserId => Subject;
        public bool IsConfirmedFor(Guid subject) => subject == Subject;
    }

    internal static async Task<SharedGroupsDatabase> CreateAsync(bool withEntitlementSource = true)
    {
        var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync();
        var store = new SharedGroupsDatabase(fixture, "groups_runtime_" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var groups = store.Open();
            await groups.Database.MigrateAsync();
            await using var tx = await groups.Database.BeginTransactionAsync();
            if (withEntitlementSource)
            {
                // Canonical table from M2's 20261126160000_InitialEconomy migration at d51ba59.
                await groups.Database.ExecuteSqlRawAsync("""
                    CREATE SCHEMA andril_economy;
                    CREATE TABLE andril_economy.premium_entitlements (
                        account_id uuid CONSTRAINT pk_premium_entitlements PRIMARY KEY,
                        tier integer NOT NULL, is_active boolean NOT NULL, expires_at_utc timestamptz NULL,
                        provider character varying(32) NOT NULL, external_subscription_id character varying(128) NULL,
                        created_at_utc timestamptz NOT NULL, updated_at_utc timestamptz NOT NULL,
                        CONSTRAINT ck_premium_entitlements_account_id_nonempty CHECK (account_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                        CONSTRAINT ck_premium_entitlements_timestamps CHECK (updated_at_utc >= created_at_utc),
                        CONSTRAINT fk_premium_entitlements_accounts FOREIGN KEY (account_id) REFERENCES andril_identity.accounts(id) ON DELETE CASCADE);
                    ALTER TABLE andril_economy.premium_entitlements ENABLE ROW LEVEL SECURITY;
                    """);
            }
            var grants = $"""
                CREATE ROLE {store.Role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS;
                GRANT {store.Role} TO CURRENT_USER;
                GRANT USAGE ON SCHEMA andril_groups TO {store.Role};
                GRANT EXECUTE ON FUNCTION andril_groups.owns_current_profile(uuid, text), andril_groups.read_group_details(uuid, text, uuid),
                    andril_groups.create_group(uuid, text, integer, uuid, text, text, text, boolean),
                    andril_groups.update_group(uuid, text, uuid, text, text, text, boolean, bigint),
                    andril_groups.mutate_membership(uuid, text, uuid, uuid, text, bigint),
                    andril_groups.write_authority(text, uuid, text, boolean), andril_groups.read_profile_evidence(uuid, text, uuid),
                    andril_groups.read_participation_evidence(uuid, text, uuid), andril_groups.read_club_capacity(uuid, uuid) TO {store.Role};
                """;
            await groups.Database.ExecuteSqlRawAsync(grants);
            await tx.CommitAsync();
            return store;
        }
        catch { await store.DisposeAsync(); throw; }
    }

    internal GroupsDbContext Open() => new(Options());

    internal DbContextOptions<GroupsDbContext> Options() => new DbContextOptionsBuilder<GroupsDbContext>()
        .UseNpgsql(Database.ConnectionString, options => options.MigrationsHistoryTable(GroupsDbContextFactory.MigrationsHistoryTableName, GroupsDbContext.Schema))
        .Options;

    internal async Task<Actor> SeedAsync(string module, bool active = true, bool minor = false)
    {
        var actor = new Actor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var birth = minor ? new DateOnly(2015, 1, 1) : new DateOnly(1990, 1, 1);
        await using var db = Database.OpenAccounts();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({actor.Account}, {actor.Subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_demographics(account_id, date_of_birth, gender, created_at_utc, updated_at_utc)
                VALUES ({actor.Account}, {birth}, 'female', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({actor.Profile}, {actor.Account}, {module}, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({actor.Account}, {module}, CURRENT_TIMESTAMP, CASE WHEN {module} = 'social' THEN CURRENT_TIMESTAMP END);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({actor.Account}, {module}, {(active ? "active" : "inactive")}, 1);
            INSERT INTO andril_identity.account_policy_records(account_id, action, version, recorded_at_utc)
                VALUES ({actor.Account}, 'terms_agreed', 'fixture-terms-v1', CURRENT_TIMESTAMP),
                       ({actor.Account}, 'privacy_acknowledged', 'fixture-privacy-v1', CURRENT_TIMESTAMP);
            """);
        return actor;
    }

    internal async Task GrantEducatorAsync(Actor actor)
    {
        await using var db = Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_groups.classroom_host_grants(profile_id, module) VALUES ({actor.Profile}, 'learning')");
    }

    internal async Task SetPremiumAsync(Actor actor, DateTimeOffset expiry)
    {
        await using var db = Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_economy.premium_entitlements(account_id, tier, is_active, expires_at_utc, provider, created_at_utc, updated_at_utc)
            VALUES ({actor.Account}, 1, true, {expiry}, 'isolated-fixture', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT (account_id) DO UPDATE SET expires_at_utc = EXCLUDED.expires_at_utc, updated_at_utc = EXCLUDED.updated_at_utc
            """);
    }

    internal Task<StoredGroup> CreateGroupAsync(Actor actor, GroupModuleScope scope, GroupSettings settings) =>
        WithEntriesAsync(actor, repository => repository.CreateAsync(scope, settings));

    internal Task<T> WithEntriesAsync<T>(Actor actor, Func<PostgresGroupEntryRepository, Task<T>> action) =>
        WithScopeAsync(actor, scope => action(new PostgresGroupEntryRepository(scope)));

    internal async Task<T> WithScopeAsync<T>(Actor actor, Func<GroupsPostgresScope, Task<T>> action)
    {
        await using var accounts = Database.OpenAccounts();
        var profiles = new FixtureProfileService(new PrivateProfileRepository(accounts), actor.Account);
        return await action(new GroupsPostgresScope(accounts, Options(), profiles, actor, Role));
    }

    internal async Task<long> CountAsync(string table)
    {
        if (table is not ("groups" or "memberships" or "capabilities" or "audit" or "join_requests")) throw new ArgumentException("Unknown fixture table.");
        await using var db = Open();
        var sql = $"SELECT count(*) AS \"Value\" FROM andril_groups.{table}";
        return await db.Database.SqlQueryRaw<long>(sql).SingleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        await using var maintenance = await SocialLifecycleDatabase.OpenMaintenanceAsync();
        await using var command = new NpgsqlCommand($"DROP ROLE IF EXISTS {Role}", maintenance);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixtureProfileService(PrivateProfileRepository repository, Guid account) : IPrivateProfileService
    {
        public Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken token) =>
            repository.WithAuthorizedProfileAsync(account, module, _ => { },
                profile => action(new(profile.Id, profile.Module, profile.CreatedAtUtc)), token);
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
