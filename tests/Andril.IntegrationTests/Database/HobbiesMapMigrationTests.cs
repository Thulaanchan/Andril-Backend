using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class HobbiesMapMigrationTests
{
    private static readonly string[] Tables =
        ["flight_point_ledger", "map_attractions", "map_contribution_capture_sessions", "map_contributions"];
    private const string OfflineConnection = "Host=127.0.0.1;Database=andril_model_only;Username=unused";

    [Fact]
    public void Migration_IsDiscoveredOnlyForMapAndMatchesTheSnapshotOffline()
    {
        using var map = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var id = Assert.Single(map.Database.GetMigrations());
        Assert.EndsWith("_InitialHobbiesMap", id);
        Assert.NotNull(map.GetService<IMigrationsAssembly>().ModelSnapshot);
        Assert.False(map.Database.HasPendingModelChanges());

        using var gallery = new HobbiesGalleryDbContextFactory().CreateDbContext([]);
        var galleryId = Assert.Single(gallery.Database.GetMigrations());
        Assert.EndsWith("_InitialHobbiesGallery", galleryId);
        using var accounts = new AccountsDbContext(
            new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(OfflineConnection).Options);
        Assert.NotEmpty(accounts.Database.GetMigrations());
        Assert.DoesNotContain(id, accounts.Database.GetMigrations());
        Assert.DoesNotContain(galleryId, map.Database.GetMigrations());
        using var notifications = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(OfflineConnection).Options);
        Assert.DoesNotContain(id, notifications.Database.GetMigrations());
    }

    [Fact]
    public void Migration_ScriptsPreserveTheExistingMapSchemaAndRollbackScopeOffline()
    {
        using var context = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var mapping = Assert.Single(assembly.Migrations);
        var migration = assembly.CreateMigration(mapping.Value, context.Database.ProviderName!);
        var creates = migration.UpOperations.OfType<CreateTableOperation>().OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(Tables, creates.Select(table => table.Name));
        Assert.Equal(2, indexes.Length);
        Assert.Equal("andril_hobbies", Assert.IsType<EnsureSchemaOperation>(migration.UpOperations[0]).Name);
        Assert.Equal(creates.Length + indexes.Length + 1, migration.UpOperations.Count);
        Assert.All(creates, table => Assert.Equal("andril_hobbies", table.Schema));
        Assert.All(indexes, index => Assert.Equal("andril_hobbies", index.Schema));
        Assert.All(creates, table => Assert.Equal("uuid",
            Assert.Single(table.Columns, column => column.Name == Assert.Single(table.PrimaryKey!.Columns)).ColumnType));
        Assert.Equal(new[] { 9, 4, 4, 8 }, creates.Select(table => table.CheckConstraints.Count));

        var ledger = creates[0];
        var reversal = Assert.Single(ledger.ForeignKeys);
        Assert.Equal(ledger.Name, reversal.PrincipalTable);
        Assert.Equal("andril_hobbies", reversal.PrincipalSchema);
        Assert.Equal("reversed_entry_id", Assert.Single(reversal.Columns));
        Assert.Equal(ReferentialAction.Restrict, reversal.OnDelete);
        Assert.All(creates.Skip(1), table => Assert.Empty(table.ForeignKeys));
        var reversalIndex = Assert.Single(indexes, index => index.Table == ledger.Name);
        Assert.True(reversalIndex.IsUnique);
        Assert.NotNull(reversalIndex.Filter);
        Assert.Equal("reversed_entry_id", Assert.Single(reversalIndex.Columns));
        Assert.Single(indexes, index => index.Table == "map_contribution_capture_sessions");
        Assert.Equal("jsonb", Assert.Single(creates[3].Columns, column => column.Name == "location_evidence").ColumnType);

        var drops = migration.DownOperations.Select(Assert.IsType<DropTableOperation>).ToArray();
        Assert.Equal(Tables, drops.Select(drop => drop.Name).Order(StringComparer.Ordinal));
        Assert.All(drops, drop => Assert.Equal("andril_hobbies", drop.Schema));
        var migrator = context.GetService<IMigrator>();
        var up = migrator.GenerateScript(Migration.InitialDatabase, mapping.Key);
        var idempotent = migrator.GenerateScript(Migration.InitialDatabase, mapping.Key, MigrationsSqlGenerationOptions.Idempotent);
        var down = migrator.GenerateScript(mapping.Key, Migration.InitialDatabase);
        var modelSql = context.Database.GenerateCreateScript();
        var modelStatements = modelSql[modelSql.IndexOf("CREATE TABLE", StringComparison.Ordinal)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(6, modelStatements.Length);
        Assert.All(modelStatements, statement => Assert.Contains(statement + ";", up));
        Assert.DoesNotContain("DROP ", up, StringComparison.OrdinalIgnoreCase);
        const string history = "andril_hobbies.\"__EFMigrationsHistory_Map\"";
        Assert.Contains($"INSERT INTO {history}", up);
        Assert.Contains($"FROM {history}", idempotent);
        Assert.Contains($"DELETE FROM {history}", down);
        Assert.DoesNotContain("__EFMigrationsHistory_Gallery", up);
        Assert.DoesNotContain("andril_identity", idempotent);
        Assert.DoesNotContain("DROP SCHEMA", down);
        Assert.All(Tables, table => Assert.Contains($"DROP TABLE andril_hobbies.{table};", down));
        Assert.Contains($"WHERE \"MigrationId\" = '{mapping.Key}';", down);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task Migration_GalleryThenMap_RollsBackIndependently() => VerifyIndependentMigrationsAsync(galleryFirst: true);

    [MapCapturePostgresFact]
    public Task Migration_MapThenGallery_RollsBackIndependently() => VerifyIndependentMigrationsAsync(galleryFirst: false);

    private static async Task VerifyIndependentMigrationsAsync(bool galleryFirst)
    {
        var settings = GetLocalMaintenanceSettings();
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand(
            "SELECT inet_server_addr() IN ('127.0.0.1'::inet, '::1'::inet)", admin))
        {
            if (await verify.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("The PostgreSQL server must report a loopback address.");
        }

        var name = $"andril_m3_map_migration_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", admin))
            await create.ExecuteNonQueryAsync();

        try
        {
            settings.Database = name;
            await using var map = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(settings.ConnectionString, HobbiesDatabaseOptions.ConfigureMapHistory).Options);
            await using var gallery = new HobbiesGalleryDbContext(new DbContextOptionsBuilder<HobbiesGalleryDbContext>()
                .UseNpgsql(settings.ConnectionString, HobbiesDatabaseOptions.ConfigureGalleryHistory).Options);
            await using var probe = new NpgsqlConnection(settings.ConnectionString);
            await probe.OpenAsync();
            await using var setup = new NpgsqlCommand("""
                CREATE SCHEMA andril_identity;
                CREATE TABLE andril_identity."__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY);
                INSERT INTO andril_identity."__EFMigrationsHistory" VALUES ('accounts-sentinel');
                CREATE TABLE public.migration_isolation_probe (id integer PRIMARY KEY);
                INSERT INTO public.migration_isolation_probe VALUES (1);
                """, probe);
            await setup.ExecuteNonQueryAsync();

            DbContext first = galleryFirst ? gallery : map;
            DbContext second = galleryFirst ? map : gallery;
            var firstId = Assert.Single(first.Database.GetMigrations());
            var secondId = Assert.Single(second.Database.GetMigrations());
            await first.Database.MigrateAsync();
            await second.Database.MigrateAsync();
            Assert.Equal(firstId, Assert.Single(await first.Database.GetAppliedMigrationsAsync()));
            Assert.Equal(secondId, Assert.Single(await second.Database.GetAppliedMigrationsAsync()));

            await first.Database.MigrateAsync(Migration.InitialDatabase);
            Assert.Empty(await first.Database.GetAppliedMigrationsAsync());
            Assert.Equal(secondId, Assert.Single(await second.Database.GetAppliedMigrationsAsync()));
            await AssertTableInventoryAsync(probe, galleryPresent: !galleryFirst, mapPresent: galleryFirst);

            await first.Database.MigrateAsync();
            Assert.Equal(firstId, Assert.Single(await first.Database.GetAppliedMigrationsAsync()));
            await second.Database.MigrateAsync(Migration.InitialDatabase);
            Assert.Empty(await second.Database.GetAppliedMigrationsAsync());
            Assert.Equal(firstId, Assert.Single(await first.Database.GetAppliedMigrationsAsync()));
            await AssertTableInventoryAsync(probe, galleryPresent: galleryFirst, mapPresent: !galleryFirst);

            await second.Database.MigrateAsync();
            Assert.Equal(secondId, Assert.Single(await second.Database.GetAppliedMigrationsAsync()));
            await AssertTableInventoryAsync(probe, galleryPresent: true, mapPresent: true);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertTableInventoryAsync(NpgsqlConnection connection, bool galleryPresent, bool mapPresent)
    {
        await using var check = new NpgsqlCommand("""
            SELECT tablename FROM pg_catalog.pg_tables
            WHERE schemaname = 'andril_hobbies';
            SELECT "MigrationId" FROM andril_identity."__EFMigrationsHistory";
            SELECT id FROM public.migration_isolation_probe;
            """, connection);
        await using var rows = await check.ExecuteReaderAsync();
        var expected = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            { "__EFMigrationsHistory_Gallery", "__EFMigrationsHistory_Map" };
        if (galleryPresent)
            expected.UnionWith(["GalleryMedia", "GalleryPreferences"]);
        if (mapPresent)
            expected.UnionWith(Tables);
        while (await rows.ReadAsync())
            Assert.True(expected.Remove(rows.GetString(0)));
        Assert.Empty(expected);
        Assert.True(await rows.NextResultAsync());
        Assert.True(await rows.ReadAsync());
        Assert.Equal("accounts-sentinel", rows.GetString(0));
        Assert.False(await rows.ReadAsync());
        Assert.True(await rows.NextResultAsync());
        Assert.True(await rows.ReadAsync());
        Assert.Equal(1, rows.GetInt32(0));
    }

    private static NpgsqlConnectionStringBuilder GetLocalMaintenanceSettings()
    {
        var configured = Environment.GetEnvironmentVariable("ANDRIL_M3_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Local PostgreSQL test connection is missing.");
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(configured);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Local PostgreSQL test connection is invalid.");
        }

        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Use a local PostgreSQL server and its postgres maintenance database.");
        if (settings.Host == "localhost")
            settings.Host = "127.0.0.1";
        settings.Pooling = false;
        settings.Enlist = false;
        settings.IncludeErrorDetail = false;
        settings.SearchPath = "public";
        settings.Timeout = 10;
        settings.CommandTimeout = 15;
        return settings;
    }
}
