using System;
using System.Linq;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialMigrationTests
{
    private const string InitialMigrationName = "20261124000100_InitialSocialQuestionnaire";
    private const string LifecycleMigrationName = "20261127203424_SocialQuestionnaireLifecycle";
    private const string RelationshipMigrationName = "20261127220222_SocialRelationshipPersistence";
    private const string PreferencesMigrationName = "20261127222933_SocialMatchingPreferences";
    private const string SafetyMigrationName = "20261127223734_SocialSafetyAuthority";
    private const string MatchingMigrationName = "20261127224847_SocialMatchingEvidence";
    private const string SocialInterestCatalogueMigrationName = "20261128021004_SocialInterestCatalogue";
    private const string SocialPersonalityEvidenceMigrationName = "20261128022353_SocialPersonalityEvidence";
    private const string SocialContextualConsentMigrationName = "20261128023415_SocialContextualConsent";
    private static readonly string[] ExpectedTables = ["social_questionnaire_answers", "social_questionnaire_responses"];

    [Fact]
    public void SocialMigration_IsDiscoveredOnlyForSocial_AndMatchesSnapshotOffline()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var migrations = context.Database.GetMigrations().ToArray();

        Assert.Equal(new[] { InitialMigrationName, LifecycleMigrationName, RelationshipMigrationName, PreferencesMigrationName, SafetyMigrationName, MatchingMigrationName, SocialInterestCatalogueMigrationName, SocialPersonalityEvidenceMigrationName, SocialContextualConsentMigrationName }, migrations);
        Assert.NotNull(context.GetService<IMigrationsAssembly>().ModelSnapshot);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void SocialMigration_UpAndDownOperations_AreStrictlyScopedToSocialSchema()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var mapping = Assert.Single(assembly.Migrations, item => item.Key == InitialMigrationName);
        Assert.Equal(InitialMigrationName, mapping.Key);

        var migration = assembly.CreateMigration(mapping.Value, context.Database.ProviderName!);

        // Up Operations
        var schemaOp = Assert.IsType<EnsureSchemaOperation>(migration.UpOperations[0]);
        Assert.Equal(SocialDbContext.Schema, schemaOp.Name);

        var createTables = migration.UpOperations.OfType<CreateTableOperation>()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTables, createTables.Select(t => t.Name));
        Assert.All(createTables, t => Assert.Equal(SocialDbContext.Schema, t.Schema));

        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.All(indexes, i => Assert.Equal(SocialDbContext.Schema, i.Schema));

        // Down Operations
        var dropTables = migration.DownOperations.OfType<DropTableOperation>()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTables, dropTables.Select(d => d.Name));
        Assert.All(dropTables, d => Assert.Equal(SocialDbContext.Schema, d.Schema));
    }

    [Fact]
    public void SocialMigration_GeneratedSqlScripts_TargetIsolatedSchemaAndHistory()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var migrator = context.GetService<IMigrator>();

        var up = migrator.GenerateScript(Migration.InitialDatabase, InitialMigrationName);
        var idempotent = migrator.GenerateScript(Migration.InitialDatabase, InitialMigrationName, MigrationsSqlGenerationOptions.Idempotent);
        var down = migrator.GenerateScript(InitialMigrationName, Migration.InitialDatabase);

        // Up script verification
        Assert.Contains("CREATE SCHEMA andril_social", up);
        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_responses", up);
        Assert.Contains("CREATE TABLE andril_social.social_questionnaire_answers", up);
        Assert.Contains(SocialDbContextFactory.MigrationsHistoryTableName, up);
        Assert.DoesNotContain("DROP TABLE", up);
        Assert.DoesNotContain("andril_identity", up);
        Assert.DoesNotContain("andril_hobbies", up);

        // Idempotent script verification
        Assert.Contains($"FROM andril_social.\"{SocialDbContextFactory.MigrationsHistoryTableName}\"", idempotent);

        // Down script verification
        Assert.Contains("DROP TABLE andril_social.social_questionnaire_answers;", down);
        Assert.Contains("DROP TABLE andril_social.social_questionnaire_responses;", down);
        Assert.Contains($"DELETE FROM andril_social.\"{SocialDbContextFactory.MigrationsHistoryTableName}\"", down);
    }

    [LocalPostgresFact]
    public async Task SocialMigration_LivePostgres_ApplyAndRollback_Succeeds()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        await fixture.ApplyAccountsMigrationsAsync();
        await using var context = fixture.Open();

        // 1. Apply Social migration to isolated disposable database
        await context.Database.MigrateAsync();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(new[] { InitialMigrationName, LifecycleMigrationName, RelationshipMigrationName, PreferencesMigrationName, SafetyMigrationName, MatchingMigrationName, SocialInterestCatalogueMigrationName, SocialPersonalityEvidenceMigrationName, SocialContextualConsentMigrationName }, applied);

        // 2. Verify expected schema objects exist in andril_social without disposing EF Core-owned connection
        var conn = context.Database.GetDbConnection();
        var shouldClose = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            shouldClose = true;
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'andril_social' ORDER BY table_name;";
            var tables = new System.Collections.Generic.List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            Assert.Contains("social_questionnaire_answers", tables);
            Assert.Contains("social_questionnaire_responses", tables);
            Assert.Contains("social_questionnaire_current_responses", tables);
            Assert.Contains("social_directional_interests", tables);
            Assert.Contains("social_matches", tables);
            Assert.Contains("social_matching_preferences", tables);
            Assert.Contains("social_profile_blocks", tables);
            Assert.Contains("social_profile_restrictions", tables);
            Assert.Contains("social_location_evidence", tables);
            Assert.Contains("social_archetype_confirmations", tables);
            Assert.Contains("social_interest_catalogue", tables);
            Assert.Contains("social_interest_selection_state", tables);
            Assert.Contains("social_interest_selections", tables);
        }
        finally
        {
            if (shouldClose && conn.State == System.Data.ConnectionState.Open)
            {
                await conn.CloseAsync();
            }
        }

        // 3. Rollback migration to InitialDatabase ("0")
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration.InitialDatabase);

        var afterRollback = await context.Database.GetAppliedMigrationsAsync();
        Assert.Empty(afterRollback);
    }

    [LocalPostgresFact]
    public async Task Lifecycle_LivePostgres_UpgradePreservesHistoryAndEnforcesOwnershipAndUniqueness()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        await fixture.ApplyAccountsMigrationsAsync();
        await using var context = fixture.Open();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialMigrationName);

        var account = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var social = Guid.NewGuid();
        var hobbies = Guid.NewGuid();
        var first = Guid.NewGuid();
        var later = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({account}, {subject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({social}, {account}, 'social', CURRENT_TIMESTAMP), ({hobbies}, {account}, 'hobbies', CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({account}, 'social', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({account}, 'social', 'active', 1);
            INSERT INTO andril_social.social_questionnaire_responses(id, profile_id, version, status, created_at_utc)
                VALUES ({first}, {social}, 'v1', 'Skipped', CURRENT_TIMESTAMP),
                       ({later}, {social}, 'v2', 'Skipped', CURRENT_TIMESTAMP + interval '1 day'),
                       ({foreign}, {hobbies}, 'v1', 'Skipped', CURRENT_TIMESTAMP);
            """);

        await migrator.MigrateAsync(LifecycleMigrationName);
        Assert.Equal(3, await context.QuestionnaireResponses.CountAsync());
        Assert.Equal(0L, await CurrentCount(context));
        var wrongResponse = await Assert.ThrowsAsync<PostgresException>(() => AddCurrent(context, social, foreign));
        Assert.Equal("fk_social_questionnaire_current_response", wrongResponse.ConstraintName);
        var wrongModule = await Assert.ThrowsAsync<PostgresException>(() => AddCurrent(context, hobbies, foreign));
        Assert.Equal("fk_social_questionnaire_current_social_profile", wrongModule.ConstraintName);
        await AddCurrent(context, social, first);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => AddCurrent(context, social, later));
        Assert.Equal("pk_social_questionnaire_current_responses", duplicate.ConstraintName);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var role = "social_migration_" + Guid.NewGuid().ToString("N");
            using var identifiers = new NpgsqlCommandBuilder();
            var quotedRole = identifiers.QuoteIdentifier(role);
            var grantRole = $"""
                CREATE ROLE {quotedRole} NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
                GRANT {quotedRole} TO CURRENT_USER;
                GRANT USAGE ON SCHEMA andril_social TO {quotedRole};
                GRANT SELECT ON andril_social.social_questionnaire_current_responses TO {quotedRole};
                GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid) TO {quotedRole};
                SET LOCAL ROLE {quotedRole};
                """;
            await context.Database.ExecuteSqlRawAsync(grantRole);
            Assert.Equal(0L, await CurrentCount(context));
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT set_config('andril.subject', {subject.ToString()}, true),
                       set_config('andril.account', {account.ToString()}, true), set_config('andril.module', 'social', true)
                """);
            Assert.Equal(1L, await CurrentCount(context));
            await context.Database.ExecuteSqlRawAsync("SELECT set_config('andril.module', 'hobbies', true)");
            Assert.Equal(0L, await CurrentCount(context));
            await transaction.RollbackAsync();
        }

        await migrator.MigrateAsync(InitialMigrationName);
        Assert.Equal(3, await context.QuestionnaireResponses.CountAsync());
        Assert.Equal(new[] { InitialMigrationName }, await context.Database.GetAppliedMigrationsAsync());
    }

    [LocalPostgresFact]
    public async Task Relationship_LivePostgres_UpgradeEnforcesScopeAndRollsBackIndependently()
    {
        await using var fixture = await LocalSocialQuestionnaireDatabase.CreateAsync(applySocialMigrations: false);
        await fixture.ApplyAccountsMigrationsAsync();
        await using var context = fixture.Open();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(LifecycleMigrationName);

        var first = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("f0000000-0000-0000-0000-000000000002");
        var hobbies = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        var firstSubject = Guid.NewGuid();
        var secondSubject = Guid.NewGuid();
        var response = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts(id, supabase_user_id, created_at_utc, updated_at_utc)
                VALUES ({firstAccount}, {firstSubject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                    ({secondAccount}, {secondSubject}, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.private_module_profiles(id, account_id, module, created_at_utc)
                VALUES ({first}, {firstAccount}, 'social', CURRENT_TIMESTAMP),
                    ({second}, {secondAccount}, 'social', CURRENT_TIMESTAMP),
                    ({hobbies}, {firstAccount}, 'hobbies', CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_starter_memberships(account_id, module, activated_at_utc, social_opt_in_at_utc)
                VALUES ({firstAccount}, 'social', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                    ({secondAccount}, 'social', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            INSERT INTO andril_identity.account_module_lifecycles(account_id, module, state, revision)
                VALUES ({firstAccount}, 'social', 'active', 1), ({secondAccount}, 'social', 'active', 1);
            INSERT INTO andril_social.social_questionnaire_responses(id, profile_id, version, status, created_at_utc)
                VALUES ({response}, {first}, 'v1', 'Skipped', CURRENT_TIMESTAMP);
            """);
        await AddCurrent(context, first, response);
        await migrator.MigrateAsync(RelationshipMigrationName);
        Assert.Equal(1L, await CurrentCount(context));
        Assert.Equal("fk_social_interest_target", (await Assert.ThrowsAsync<PostgresException>(() => AddInterest(context, first, hobbies))).ConstraintName);
        Assert.Equal("ck_social_interest_distinct", (await Assert.ThrowsAsync<PostgresException>(() => AddInterest(context, first, first))).ConstraintName);
        Assert.Equal("ck_social_match_canonical_pair", (await Assert.ThrowsAsync<PostgresException>(() => AddMatch(context, second, first))).ConstraintName);
        await AddInterest(context, first, second);
        Assert.Equal("pk_social_directional_interests", (await Assert.ThrowsAsync<PostgresException>(() => AddInterest(context, first, second))).ConstraintName);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var role = "social_relationship_schema_" + Guid.NewGuid().ToString("N");
            using var identifiers = new NpgsqlCommandBuilder();
            var quotedRole = identifiers.QuoteIdentifier(role);
            var grants = $"""
                CREATE ROLE {quotedRole} NOLOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT;
                GRANT {quotedRole} TO CURRENT_USER;
                GRANT USAGE ON SCHEMA andril_social TO {quotedRole};
                GRANT SELECT ON andril_social.social_directional_interests, andril_social.social_matches TO {quotedRole};
                GRANT EXECUTE ON FUNCTION andril_social.questionnaire_owner_visible(uuid),
                    andril_social.try_form_social_match(uuid, uuid, uuid, timestamp with time zone) TO {quotedRole};
                SET LOCAL ROLE {quotedRole};
                """;
            await context.Database.ExecuteSqlRawAsync(grants);
            Assert.False(await context.Database.SqlQuery<bool>($"""
                SELECT has_table_privilege('andril_social.social_matches', 'INSERT') AS "Value"
                """).SingleAsync());
            Assert.False(await context.Database.SqlQuery<bool>($"""
                SELECT has_function_privilege('andril_social.social_reciprocal_interest(uuid,uuid)', 'EXECUTE') AS "Value"
                """).SingleAsync());
            await BindRelationshipScope(context, secondSubject, secondAccount);
            Assert.Equal(0L, await VisibleRelationshipRows(context));
            Assert.Null(await TryFormMatch(context, second, first, Guid.NewGuid()));
            await BindRelationshipScope(context, firstSubject, firstAccount);
            Assert.Equal(1L, await VisibleRelationshipRows(context));
            Assert.Null(await TryFormMatch(context, first, second, Guid.NewGuid()));
            await transaction.RollbackAsync();
        }

        await AddInterest(context, second, first);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await BindRelationshipScope(context, firstSubject, firstAccount);
            var match = Guid.NewGuid();
            Assert.Equal(match, await TryFormMatch(context, first, second, match));
            Assert.Null(await TryFormMatch(context, first, second, Guid.NewGuid()));
            await transaction.CommitAsync();
        }
        Assert.Equal("ux_social_matches_active_pair", (await Assert.ThrowsAsync<PostgresException>(() => AddMatch(context, first, second))).ConstraintName);

        await migrator.MigrateAsync(LifecycleMigrationName);
        Assert.Equal(1L, await CurrentCount(context));
        Assert.Equal(new[] { InitialMigrationName, LifecycleMigrationName }, await context.Database.GetAppliedMigrationsAsync());
        Assert.True(await context.Database.SqlQuery<bool>($"""
            SELECT (to_regclass('andril_social.social_directional_interests') IS NULL
                AND to_regclass('andril_social.social_matches') IS NULL) AS "Value"
            """).SingleAsync());
    }

    private static Task<int> AddInterest(SocialDbContext context, Guid actor, Guid target) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social.social_directional_interests(actor_profile_id, target_profile_id, module, is_positive, recorded_at_utc)
            VALUES ({actor}, {target}, 'social', true, CURRENT_TIMESTAMP)
            """);

    private static Task<int> AddMatch(SocialDbContext context, Guid first, Guid second) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social.social_matches(id, first_profile_id, second_profile_id, module, state, created_at_utc)
            VALUES ({Guid.NewGuid()}, {first}, {second}, 'social', 'active', CURRENT_TIMESTAMP)
            """);

    private static Task<int> BindRelationshipScope(SocialDbContext context, Guid subject, Guid account) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT set_config('andril.subject', {subject.ToString()}, true),
                set_config('andril.account', {account.ToString()}, true), set_config('andril.module', 'social', true)
            """);

    private static Task<Guid?> TryFormMatch(SocialDbContext context, Guid actor, Guid target, Guid match) =>
        context.Database.SqlQuery<Guid?>($"""
            SELECT andril_social.try_form_social_match({actor}, {target}, {match}, CURRENT_TIMESTAMP) AS "Value"
            """).SingleAsync();

    private static Task<long> VisibleRelationshipRows(SocialDbContext context) => context.Database.SqlQueryRaw<long>(
        "SELECT count(*) AS \"Value\" FROM andril_social.social_directional_interests").SingleAsync();

    private static Task<int> AddCurrent(SocialDbContext context, Guid profile, Guid response) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social.social_questionnaire_current_responses(profile_id, response_id, module)
                VALUES ({profile}, {response}, 'social')
            """);

    private static Task<long> CurrentCount(SocialDbContext context) => context.Database.SqlQueryRaw<long>(
        "SELECT count(*) AS \"Value\" FROM andril_social.social_questionnaire_current_responses").SingleAsync();
}
