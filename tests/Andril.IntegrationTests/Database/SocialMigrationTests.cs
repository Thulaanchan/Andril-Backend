using System;
using System.Linq;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class SocialMigrationTests
{
    private const string InitialMigrationName = "20261124000100_InitialSocialQuestionnaire";
    private static readonly string[] ExpectedTables = ["social_questionnaire_answers", "social_questionnaire_responses"];

    [Fact]
    public void SocialMigration_IsDiscoveredOnlyForSocial_AndMatchesSnapshotOffline()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var migrations = context.Database.GetMigrations().ToArray();

        var migrationId = Assert.Single(migrations);
        Assert.Equal(InitialMigrationName, migrationId);
        Assert.NotNull(context.GetService<IMigrationsAssembly>().ModelSnapshot);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void SocialMigration_UpAndDownOperations_AreStrictlyScopedToSocialSchema()
    {
        using var context = new SocialDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var mapping = Assert.Single(assembly.Migrations);
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
        await using var context = fixture.Open();

        // 1. Apply Social migration to isolated disposable database
        await context.Database.MigrateAsync();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains(InitialMigrationName, applied);

        // 2. Verify expected schema objects exist in andril_social
        await using (var conn = context.Database.GetDbConnection())
        {
            await conn.OpenAsync();
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
        }

        // 3. Rollback migration to InitialDatabase ("0")
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration.InitialDatabase);

        var afterRollback = await context.Database.GetAppliedMigrationsAsync();
        Assert.DoesNotContain(InitialMigrationName, afterRollback);
    }
}
