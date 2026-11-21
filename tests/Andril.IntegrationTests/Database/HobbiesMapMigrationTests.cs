using System;
using System.Data;
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
        Assert.Equal(creates.Length + indexes.Length, migration.UpOperations.Count);
        Assert.All(creates, table => Assert.Equal("uuid",
            Assert.Single(table.Columns, column => column.Name == Assert.Single(table.PrimaryKey!.Columns)).ColumnType));
        Assert.Equal(new[] { 9, 4, 4, 8 }, creates.Select(table => table.CheckConstraints.Count));

        var ledger = creates[0];
        var reversal = Assert.Single(ledger.ForeignKeys);
        Assert.Equal(ledger.Name, reversal.PrincipalTable);
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
        var migrator = context.GetService<IMigrator>();
        var up = migrator.GenerateScript(Migration.InitialDatabase, mapping.Key);
        var down = migrator.GenerateScript(mapping.Key, Migration.InitialDatabase);
        var modelStatements = context.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(6, modelStatements.Length);
        Assert.All(modelStatements, statement => Assert.Contains(statement + ";", up));
        Assert.DoesNotContain("DROP ", up, StringComparison.OrdinalIgnoreCase);
        Assert.All(Tables, table => Assert.Contains($"DROP TABLE {table};", down));
        Assert.Contains($"WHERE \"MigrationId\" = '{mapping.Key}';", down);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public async Task Migration_AppliesAndRollsBackWithoutTouchingUnrelatedData()
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
            await using var context = new HobbiesMapDbContext(
                new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(settings.ConnectionString).Options);
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE migration_isolation_probe (id integer PRIMARY KEY)");
            await context.Database.ExecuteSqlRawAsync("INSERT INTO migration_isolation_probe VALUES (1)");
            var migration = Assert.Single(context.Database.GetMigrations());

            await context.Database.MigrateAsync(migration);
            Assert.Equal(migration, Assert.Single(await context.Database.GetAppliedMigrationsAsync()));
            Assert.Equal(0, await context.MapAttractions.CountAsync());
            Assert.Equal(0, await context.CaptureSessions.CountAsync());
            Assert.Equal(0, await context.MapContributions.CountAsync());
            Assert.Equal(0, await context.FlightPointsLedgerEntries.CountAsync());

            await context.Database.MigrateAsync(Migration.InitialDatabase);
            Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
            await using var probe = new NpgsqlConnection(settings.ConnectionString);
            await probe.OpenAsync();
            await using var check = new NpgsqlCommand("""
                SELECT to_regclass('map_attractions') IS NULL
                   AND to_regclass('map_contribution_capture_sessions') IS NULL
                   AND to_regclass('map_contributions') IS NULL
                   AND to_regclass('flight_point_ledger') IS NULL
                   AND EXISTS (SELECT 1 FROM migration_isolation_probe WHERE id = 1)
                """, probe);
            Assert.Equal(true, await check.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
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
