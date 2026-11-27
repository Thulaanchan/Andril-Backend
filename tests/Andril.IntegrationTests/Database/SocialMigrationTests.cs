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
    private static readonly string[] ExpectedTables = ["social_questionnaire_answers", "social_questionnaire_responses"];

    [Fact]
    public void SocialMigration_IsDiscoveredOnlyForSocial_AndMatchesSnapshotOffline()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var migrations = context.Database.GetMigrations().ToArray();

        Assert.Equal(new[] { InitialMigrationName, LifecycleMigrationName }, migrations);
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
        Assert.Equal(new[] { InitialMigrationName, LifecycleMigrationName }, applied);

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

    private static Task<int> AddCurrent(SocialDbContext context, Guid profile, Guid response) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_social.social_questionnaire_current_responses(profile_id, response_id, module)
                VALUES ({profile}, {response}, 'social')
            """);

    private static Task<long> CurrentCount(SocialDbContext context) => context.Database.SqlQueryRaw<long>(
        "SELECT count(*) AS \"Value\" FROM andril_social.social_questionnaire_current_responses").SingleAsync();
}
