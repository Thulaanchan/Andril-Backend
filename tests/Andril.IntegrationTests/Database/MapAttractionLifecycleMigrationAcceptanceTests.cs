using System.Data;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class MapAttractionLifecycleMigrationAcceptanceTests
{
    private const string InitialMap = "20261121032052_InitialHobbiesMap";
    private const string UniqueSession = "20261121185145_AddMapSubmissionSessionUniqueness";
    private const string Lifecycle = "20261125022647_AddMapAttractionLifecycle";

    [Fact]
    public void Migration_IsMapOnlyForwardOnlyAndMatchesLifecycleModelOffline()
    {
        using var map = new HobbiesMapDbContextFactory().CreateDbContext([]);
        var migrations = map.Database.GetMigrations().ToArray();
        Assert.Equal(new[] { InitialMap, UniqueSession, Lifecycle }, migrations);
        Assert.False(map.Database.HasPendingModelChanges());

        var migrator = map.GetService<IMigrator>();
        var script = migrator.GenerateScript(UniqueSession, Lifecycle);
        Assert.Contains("CREATE TABLE andril_hobbies.map_attraction_lifecycle", script);
        Assert.Contains("CREATE TABLE andril_hobbies.map_attraction_lifecycle_receipts", script);
        Assert.Contains("CREATE TABLE andril_hobbies.map_attraction_lifecycle_audit", script);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", script);
        Assert.Contains("map_lifecycle_check_update", script);
        Assert.Contains("map_lifecycle_check_applied", script);
        Assert.Contains("map_lifecycle_immutable", script);
        Assert.Contains("map_lifecycle_receipt_immutable", script);
        Assert.Contains("map_lifecycle_audit_immutable", script);
        Assert.Contains("__EFMigrationsHistory_Map", script);
        Assert.DoesNotContain("INSERT INTO andril_hobbies.map_attraction_lifecycle", script);
        Assert.DoesNotContain("__EFMigrationsHistory_Gallery", script);
        Assert.DoesNotContain("andril_identity", script);
        Assert.Throws<NotSupportedException>(() => migrator.GenerateScript(Lifecycle, UniqueSession));
        Assert.Equal(ConnectionState.Closed, map.Database.GetDbConnection().State);

        using var gallery = new HobbiesGalleryDbContextFactory().CreateDbContext([]);
        Assert.DoesNotContain(Lifecycle, gallery.Database.GetMigrations());
        using var accounts = new AccountsDbContext(
            new DbContextOptionsBuilder<AccountsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=andril_model_only;Username=unused").Options);
        Assert.DoesNotContain(Lifecycle, accounts.Database.GetMigrations());
    }

    [MapCapturePostgresFact]
    public async Task MigratedSchema_PreservesAttractionsAndRequiresExplicitStateWithIntegrity()
    {
        await WithDisposableDatabaseAsync(async connectionString =>
        {
            await using var map = CreateContext(connectionString);
            await map.Database.MigrateAsync();
            Assert.Equal(new[] { InitialMap, UniqueSession, Lifecycle },
                await map.Database.GetAppliedMigrationsAsync());
            await AssertCatalogIntegrityAsync(map);
            Assert.Equal(0L, await ScalarAsync(map,
                "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle"));

            await using var gallery = new HobbiesGalleryDbContext(
                new DbContextOptionsBuilder<HobbiesGalleryDbContext>()
                    .UseNpgsql(connectionString, HobbiesDatabaseOptions.ConfigureGalleryHistory).Options);
            var galleryMigration = Assert.Single(gallery.Database.GetMigrations());
            await gallery.Database.MigrateAsync();
            Assert.Equal(galleryMigration, Assert.Single(await gallery.Database.GetAppliedMigrationsAsync()));
            Assert.Equal(new[] { InitialMap, UniqueSession, Lifecycle },
                await map.Database.GetAppliedMigrationsAsync());
        });

        await WithDisposableDatabaseAsync(async connectionString =>
        {
            var activeAttraction = Guid.NewGuid();
            var inactiveAttraction = Guid.NewGuid();
            var missingAttraction = Guid.NewGuid();

            await using (var baseline = CreateContext(connectionString))
            {
                await baseline.Database.MigrateAsync(UniqueSession);
                baseline.MapAttractions.AddRange(
                    MapAttraction.Create(activeAttraction, "Lifecycle active test", 1, 1),
                    MapAttraction.Create(inactiveAttraction, "Lifecycle inactive test", 2, 2),
                    MapAttraction.Create(missingAttraction, "Lifecycle missing test", 3, 3));
                await baseline.SaveChangesAsync();
                Assert.Equal(3, await baseline.MapAttractions.CountAsync());
                await baseline.Database.MigrateAsync();

                Assert.Equal(new[] { InitialMap, UniqueSession, Lifecycle },
                    await baseline.Database.GetAppliedMigrationsAsync());
                Assert.Equal(3, await baseline.MapAttractions.CountAsync());
                Assert.Equal(0L, await ScalarAsync(baseline,
                    "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle"));

                await baseline.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_hobbies.map_attraction_lifecycle
                        (attraction_id,is_active,version,operation_sequence,last_receipt_id)
                    VALUES ({activeAttraction},true,0,0,NULL),({inactiveAttraction},false,0,0,NULL)
                    """);

                var initialStates = await ReadStatesAsync(baseline);
                Assert.Equal((true, 0L, 0L, (Guid?)null), initialStates[activeAttraction]);
                Assert.Equal((false, 0L, 0L, (Guid?)null), initialStates[inactiveAttraction]);
                Assert.DoesNotContain(missingAttraction, initialStates.Keys);

                await AssertCatalogIntegrityAsync(baseline);

                var subject = new TestUser(Guid.NewGuid());
                var writer = new MapAttractionLifecycleWriter(
                    new ContextFactory(connectionString), new TestAuthorization(subject), subject);
                var missing = await writer.ChangeAsync(
                    new MapAttractionLifecycleChange(Guid.NewGuid(), missingAttraction, false, 0));
                Assert.Equal(MapAttractionLifecycleOutcome.Denied, missing.Outcome);

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.ChangeAsync(
                    new MapAttractionLifecycleChange(Guid.NewGuid(), inactiveAttraction, true, 0),
                    cancellation.Token));

                var first = new MapAttractionLifecycleChange(Guid.NewGuid(), activeAttraction, false, 0);
                var second = new MapAttractionLifecycleChange(Guid.NewGuid(), activeAttraction, false, 0);
                var results = await Task.WhenAll(writer.ChangeAsync(first), writer.ChangeAsync(second));
                Assert.Single(results, result => result.Outcome == MapAttractionLifecycleOutcome.Changed);
                Assert.Single(results, result => result.Outcome == MapAttractionLifecycleOutcome.Conflict);
                var winner = results[0].Outcome == MapAttractionLifecycleOutcome.Changed ? first : second;
                var retry = await writer.ChangeAsync(winner);
                Assert.Equal(MapAttractionLifecycleOutcome.ExistingAuthorized, retry.Outcome);

                var finalStates = await ReadStatesAsync(baseline);
                Assert.Equal((false, 1L, 1L, true),
                    (finalStates[activeAttraction].Active, finalStates[activeAttraction].Version,
                        finalStates[activeAttraction].Sequence, finalStates[activeAttraction].Receipt.HasValue));
                Assert.Equal(1L, await ScalarAsync(baseline,
                    "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts"));
                Assert.Equal(1L, await ScalarAsync(baseline,
                    "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_audit"));

                var updateError = await Assert.ThrowsAsync<PostgresException>(() =>
                    baseline.Database.ExecuteSqlRawAsync("""
                        UPDATE andril_hobbies.map_attraction_lifecycle_receipts
                        SET result_version=result_version
                        """));
                Assert.Equal("ck_map_lifecycle_evidence_immutable", updateError.ConstraintName);
                var deleteError = await Assert.ThrowsAsync<PostgresException>(() =>
                    baseline.Database.ExecuteSqlRawAsync("""
                        DELETE FROM andril_hobbies.map_attraction_lifecycle_audit
                        """));
                Assert.Equal("ck_map_lifecycle_evidence_immutable", deleteError.ConstraintName);

                await AssertDeferredReceiptFailureRollsBackAsync(connectionString, activeAttraction, subject.UserId!.Value);
                Assert.Equal(1L, await ScalarAsync(baseline,
                    "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts"));
                Assert.Equal(1L, await ScalarAsync(baseline,
                    "SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_audit"));
            }
        });
    }

    private static HobbiesMapDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql(connectionString, HobbiesDatabaseOptions.ConfigureMapHistory).Options);

    private static async Task<Dictionary<Guid, (bool Active, long Version, long Sequence, Guid? Receipt)>> ReadStatesAsync(
        HobbiesMapDbContext context)
    {
        var states = new Dictionary<Guid, (bool, long, long, Guid?)>();
        await using var command = new NpgsqlCommand("""
            SELECT attraction_id,is_active,version,operation_sequence,last_receipt_id
            FROM andril_hobbies.map_attraction_lifecycle
            """, (NpgsqlConnection)context.Database.GetDbConnection());
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            states.Add(reader.GetGuid(0), (reader.GetBoolean(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        await command.Connection.CloseAsync();
        return states;
    }

    private static async Task<long> ScalarAsync(HobbiesMapDbContext context, string sql)
    {
        await using var command = new NpgsqlCommand(sql,
            (NpgsqlConnection)context.Database.GetDbConnection());
        var connection = command.Connection!;
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();
        try
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task AssertCatalogIntegrityAsync(HobbiesMapDbContext context)
    {
        Assert.Equal(3L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_constraint
            WHERE connamespace='andril_hobbies'::regnamespace
              AND conname IN ('fk_map_lifecycle_current_receipt','fk_map_lifecycle_receipt_audit','fk_map_lifecycle_audit_receipt')
              AND convalidated AND condeferrable AND condeferred AND confdeltype='a'
            """));
        Assert.Equal(1L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_constraint
            WHERE connamespace='andril_hobbies'::regnamespace
              AND conname='fk_map_lifecycle_receipt_audit'
              AND array_length(conkey,1)=14 AND array_length(confkey,1)=14
            """));
        Assert.Equal(1L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_trigger t
            JOIN pg_class r ON r.oid=t.tgrelid
            JOIN pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='andril_hobbies' AND t.tgname='map_lifecycle_receipt_applied'
              AND t.tgconstraint<>0 AND t.tgdeferrable AND t.tginitdeferred
            """));
        Assert.Equal(4L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_trigger t
            JOIN pg_class r ON r.oid=t.tgrelid
            JOIN pg_namespace n ON n.oid=r.relnamespace
            WHERE n.nspname='andril_hobbies'
              AND t.tgname IN ('map_lifecycle_update_link','map_lifecycle_receipt_applied',
                               'map_lifecycle_receipt_immutable','map_lifecycle_audit_immutable')
            """));
        Assert.Equal(3L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='andril_hobbies'
              AND p.proname IN ('map_lifecycle_check_update','map_lifecycle_check_applied','map_lifecycle_immutable')
            """));
        Assert.Equal(0L, await ScalarAsync(context, """
            SELECT count(*) FROM pg_index i
            JOIN pg_class t ON t.oid=i.indrelid
            JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname='andril_hobbies'
              AND t.relname IN ('map_attraction_lifecycle','map_attraction_lifecycle_receipts','map_attraction_lifecycle_audit')
              AND NOT i.indisunique
            """));
    }

    private static async Task AssertDeferredReceiptFailureRollsBackAsync(
        string connectionString, Guid attractionId, Guid subjectId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var receiptId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO andril_hobbies.map_attraction_lifecycle_receipts
                (receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,
                 previous_is_active,previous_version,result_is_active,result_version,
                 previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
            VALUES ($1,$2,$3,$4,true,1,false,1,true,2,1,2,1,clock_timestamp())
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(receiptId);
            insert.Parameters.AddWithValue(subjectId);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(attractionId);
            await insert.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Contains(error.ConstraintName,
            new[] { "fk_map_lifecycle_receipt_audit", "ck_map_lifecycle_receipt_applied" });
    }

    private static async Task WithDisposableDatabaseAsync(Func<string, Task> test)
    {
        var configured = Environment.GetEnvironmentVariable("ANDRIL_M3_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Disposable local PostgreSQL is not configured.");
        var settings = new NpgsqlConnectionStringBuilder(configured);
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Only the configured local postgres maintenance database is allowed.");
        if (settings.Host == "localhost")
            settings.Host = "127.0.0.1";
        settings.Pooling = false;
        settings.Enlist = false;
        settings.IncludeErrorDetail = false;
        settings.Timeout = 10;
        settings.CommandTimeout = 15;

        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand(
            "SELECT inet_server_addr() IN ('127.0.0.1'::inet, '::1'::inet)", admin))
        {
            if (await verify.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("PostgreSQL must report a loopback address.");
        }

        var databaseName = "andril_m3_map37b_test_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\" TEMPLATE template0", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            settings.Database = databaseName;
            await test(settings.ConnectionString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestUser(Guid subject) : ICurrentUser
    {
        public Guid? UserId { get; } = subject;
        public bool IsAuthenticated { get; } = true;
    }

    private sealed class TestAuthorization(TestUser user) : IMapAttractionManagementAuthorization
    {
        public Task<MapAttractionManagementAuthorizationResult> AuthorizeAsync(
            System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction,
            Guid attractionId,
            bool requestedIsActive,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MapAttractionManagementAuthorizationResult(
                MapAttractionManagementAuthorizationOutcome.Satisfied,
                new MapAttractionManagementGrant(user.UserId!.Value, attractionId, requestedIsActive)));
        }
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<HobbiesMapDbContext>
    {
        public HobbiesMapDbContext CreateDbContext() => CreateContext(connectionString);
    }
}
