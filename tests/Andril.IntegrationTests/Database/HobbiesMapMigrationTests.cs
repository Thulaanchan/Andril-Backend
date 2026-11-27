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
    private const string InitialMap = "20261121032052_InitialHobbiesMap";
    private const string UniqueSessionMigration = "20261121185145_AddMapSubmissionSessionUniqueness";
    private const string LifecycleMigration = "20261125022647_AddMapAttractionLifecycle";
    private const string UniqueSession = "ux_map_contributions_capture_session_id";
    private const string ScopedSession = "fk_map_contributions_capture_session_scope";

    [Fact]
    public void Migration_IsDiscoveredOnlyForMapAndMatchesTheSnapshotOffline()
    {
        using var map = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var ids = map.Database.GetMigrations().ToArray();
        Assert.Equal(5, ids.Length);
        Assert.Equal(InitialMap, ids[0]);
        Assert.Equal(UniqueSessionMigration, ids[1]);
        Assert.Equal(LifecycleMigration, ids[2]);
        Assert.Equal("20261126092843_AddMapPrivateMedia", ids[3]);
        Assert.Equal("20261127014224_AddMapModeration", ids[4]);
        Assert.NotNull(map.GetService<IMigrationsAssembly>().ModelSnapshot);
        Assert.False(map.Database.HasPendingModelChanges());

        using var gallery = new HobbiesGalleryDbContextFactory().CreateDbContext([]);
        var galleryId = Assert.Single(gallery.Database.GetMigrations());
        Assert.EndsWith("_InitialHobbiesGallery", galleryId);
        using var accounts = new AccountsDbContext(
            new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(OfflineConnection).Options);
        Assert.NotEmpty(accounts.Database.GetMigrations());
        Assert.DoesNotContain(galleryId, map.Database.GetMigrations());
        using var notifications = new NotificationsDbContext(
            new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(OfflineConnection).Options);
        Assert.All(ids, id =>
        {
            Assert.DoesNotContain(id, accounts.Database.GetMigrations());
            Assert.DoesNotContain(id, gallery.Database.GetMigrations());
            Assert.DoesNotContain(id, notifications.Database.GetMigrations());
        });
    }

    [Fact]
    public void Migration_ScriptsPreserveTheExistingMapSchemaAndRollbackScopeOffline()
    {
        using var context = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var assembly = context.GetService<IMigrationsAssembly>();
        var mapping = assembly.Migrations.First();
        Assert.Equal(InitialMap, mapping.Key);
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
        Assert.False(Assert.Single(creates[3].Columns, column => column.Name == "capture_session_id").IsNullable);
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

    [Fact]
    public void SubmissionMigration_ScriptsContainOnlyMapConstraintChanges()
    {
        using var map = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var target = UniqueSessionMigration;
        var migrator = map.GetService<IMigrator>();
        var up = migrator.GenerateScript(InitialMap, target);
        var down = migrator.GenerateScript(target, InitialMap);
        Assert.Contains($"CREATE UNIQUE INDEX {UniqueSession} ON andril_hobbies.map_contributions (capture_session_id)", up);
        Assert.Contains("ADD CONSTRAINT ak_map_capture_session_scope UNIQUE (session_id, owner_hobbies_module_profile_id, attraction_id)", up);
        Assert.Contains($"ADD CONSTRAINT {ScopedSession} FOREIGN KEY (capture_session_id, owner_hobbies_module_profile_id, attraction_id) " +
            "REFERENCES andril_hobbies.map_contribution_capture_sessions (session_id, owner_hobbies_module_profile_id, attraction_id)", up);
        Assert.DoesNotContain("ON DELETE CASCADE", up);
        Assert.Contains("INSERT INTO andril_hobbies.\"__EFMigrationsHistory_Map\"", up);
        Assert.Contains("DELETE FROM andril_hobbies.\"__EFMigrationsHistory_Map\"", down);
        Assert.DoesNotContain("DROP TABLE", down);
        Assert.DoesNotContain("andril_identity", up + down);
        Assert.DoesNotContain("__EFMigrationsHistory_Gallery", up + down);
        Assert.Equal(ConnectionState.Closed, map.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task SubmissionMigration_ValidatesLegacyRowsAndEnforcesScope() =>
        WithDatabaseAsync(async (connection, probe) =>
        {
            await using var map = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options);
            await map.Database.MigrateAsync(InitialMap);
            var session = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var attraction = Guid.NewGuid();

            Task<int> ExecuteAsync(FormattableString sql) => map.Database.ExecuteSqlInterpolatedAsync(sql);
            Task<int> InsertAsync(Guid? captureSession, Guid contributionOwner, Guid contributionAttraction) =>
                ExecuteAsync($"""
                    INSERT INTO andril_hobbies.map_contributions
                        (contribution_id, capture_session_id, owner_hobbies_module_profile_id, attraction_id,
                         private_photo_storage_object_key, description, location_evidence, submitted_at_utc, moderation_status)
                    VALUES ({Guid.NewGuid()}, CAST({captureSession} AS uuid), {contributionOwner}, {contributionAttraction},
                            'private/photo', 'description', jsonb_build_object(), now(), 1)
                    """);

            await ExecuteAsync($"""
                INSERT INTO andril_hobbies.map_contribution_capture_sessions
                    (session_id, owner_hobbies_module_profile_id, attraction_id, started_at_utc)
                VALUES ({session}, {owner}, {attraction}, now())
                """);
            var nullError = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(null, owner, attraction));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, nullError.SqlState);
            Assert.Equal("capture_session_id", nullError.ColumnName);

            foreach (var (badSession, badOwner, badAttraction, state, constraint) in new[]
            {
                (session, owner, attraction, PostgresErrorCodes.UniqueViolation, UniqueSession),
                (Guid.NewGuid(), owner, attraction, PostgresErrorCodes.ForeignKeyViolation, ScopedSession),
                (session, Guid.NewGuid(), attraction, PostgresErrorCodes.ForeignKeyViolation, ScopedSession),
                (session, owner, Guid.NewGuid(), PostgresErrorCodes.ForeignKeyViolation, ScopedSession)
            })
            {
                await InsertAsync(badSession, badOwner, badAttraction);
                var duplicate = constraint == UniqueSession;
                if (duplicate)
                    await InsertAsync(session, owner, attraction);
                var error = await Assert.ThrowsAsync<PostgresException>(() => map.Database.MigrateAsync(UniqueSessionMigration));
                Assert.Equal(state, error.SqlState);
                Assert.Equal(constraint, error.ConstraintName);
                Assert.Equal(new[] { InitialMap }, await map.Database.GetAppliedMigrationsAsync());
                Assert.Equal(duplicate ? 2 : 1, await map.MapContributions.CountAsync());
                await ExecuteAsync($"DELETE FROM andril_hobbies.map_contributions");
            }

            await InsertAsync(session, owner, attraction);
            await map.Database.MigrateAsync(UniqueSessionMigration);
            Assert.Equal(1, await map.MapContributions.CountAsync());
            var requiredError = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(null, owner, attraction));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, requiredError.SqlState);
            Assert.Equal("capture_session_id", requiredError.ColumnName);
            var duplicateError = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(session, owner, attraction));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateError.SqlState);
            Assert.Equal(UniqueSession, duplicateError.ConstraintName);

            foreach (var (badSession, badOwner, badAttraction) in new[]
            {
                (Guid.NewGuid(), owner, attraction),
                (session, Guid.NewGuid(), attraction),
                (session, owner, Guid.NewGuid())
            })
            {
                await ExecuteAsync($"DELETE FROM andril_hobbies.map_contributions");
                var error = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(badSession, badOwner, badAttraction));
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
                Assert.Equal(ScopedSession, error.ConstraintName);
            }

            await InsertAsync(session, owner, attraction);
            var deleteError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
                DELETE FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id = {session}
                """));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deleteError.SqlState);
            Assert.Equal(ScopedSession, deleteError.ConstraintName);
            await using var deleteAction = new NpgsqlCommand(
                "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'fk_map_contributions_capture_session_scope'", probe);
            Assert.Equal("a", await deleteAction.ExecuteScalarAsync());

            await map.Database.MigrateAsync(InitialMap);
            Assert.Equal(new[] { InitialMap }, await map.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1, await map.MapContributions.CountAsync());
            await InsertAsync(session, owner, attraction);
            await InsertAsync(Guid.NewGuid(), owner, attraction);
            Assert.Equal(3, await map.MapContributions.CountAsync());
            Assert.Equal(1, await map.CaptureSessions.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Migration_GalleryThenMap_RollsBackIndependently() =>
        WithDatabaseAsync((connection, probe) => VerifyIndependentMigrationsAsync(connection, probe, galleryFirst: true));

    [MapCapturePostgresFact]
    public Task Migration_MapThenGallery_RollsBackIndependently() =>
        WithDatabaseAsync((connection, probe) => VerifyIndependentMigrationsAsync(connection, probe, galleryFirst: false));

    private static async Task WithDatabaseAsync(Func<string, NpgsqlConnection, Task> test)
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
            await using var probe = new NpgsqlConnection(settings.ConnectionString);
            await probe.OpenAsync();
            await test(settings.ConnectionString, probe);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyIndependentMigrationsAsync(string connection, NpgsqlConnection probe, bool galleryFirst)
    {
        await using var map = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options);
        await using var gallery = new HobbiesGalleryDbContext(new DbContextOptionsBuilder<HobbiesGalleryDbContext>()
            .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureGalleryHistory).Options);
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
        // The independent rollback test covers the reversible portion of each chain.
        // Lifecycle forward-only behavior is covered by its own acceptance test.
        var firstIds = GetReversibleMigrationIds(first);
        var secondIds = GetReversibleMigrationIds(second);
        await first.Database.MigrateAsync(firstIds[^1]);
        await second.Database.MigrateAsync(secondIds[^1]);
        Assert.Equal(firstIds, await first.Database.GetAppliedMigrationsAsync());
        Assert.Equal(secondIds, await second.Database.GetAppliedMigrationsAsync());

        await first.Database.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await first.Database.GetAppliedMigrationsAsync());
        Assert.Equal(secondIds, await second.Database.GetAppliedMigrationsAsync());
        await AssertTableInventoryAsync(probe, galleryPresent: !galleryFirst, mapPresent: galleryFirst);

        await first.Database.MigrateAsync(firstIds[^1]);
        Assert.Equal(firstIds, await first.Database.GetAppliedMigrationsAsync());
        await second.Database.MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await second.Database.GetAppliedMigrationsAsync());
        Assert.Equal(firstIds, await first.Database.GetAppliedMigrationsAsync());
        await AssertTableInventoryAsync(probe, galleryPresent: galleryFirst, mapPresent: !galleryFirst);

        await second.Database.MigrateAsync(secondIds[^1]);
        Assert.Equal(secondIds, await second.Database.GetAppliedMigrationsAsync());
        await AssertTableInventoryAsync(probe, galleryPresent: true, mapPresent: true);
    }

    private static string[] GetReversibleMigrationIds(DbContext context) =>
        context.Database.GetMigrations().TakeWhile(id => id != LifecycleMigration).ToArray();

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
