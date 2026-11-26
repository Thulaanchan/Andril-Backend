using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapAttractionPersistenceTests
{
    private const string ModelOnlyConnection =
        "Host=127.0.0.1;Database=andril_model_only;Username=unused";
    private const string Name = "  Synthetic attraction  ";
    private static readonly Guid AttractionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid MissingId = new("44444444-4444-4444-8444-444444444444");

    [Fact]
    public async Task Discover_RejectsNullAndPreCancellationBeforeUsingDatabase()
    {
        var probe = new OfflineDiscoveryProbe();
        await using var context = CreateContext(ModelOnlyConnection, probe);
        var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);

        await Assert.ThrowsAsync<ArgumentNullException>("query", () => repository.DiscoverAsync(null!));
        var token = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.DiscoverAsync(new MapAttractionDiscoveryQuery(0, 1), token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Null(probe.CommandText);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Discover_CompilesOnlyCanonicalColumnsAndPaginatesOnServer()
    {
        var probe = new OfflineDiscoveryProbe();
        await using var context = CreateContext(ModelOnlyConnection, probe);
        var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.DiscoverAsync(new MapAttractionDiscoveryQuery(17, 23)));

        Assert.Same(probe.Stop, error);
        Assert.NotNull(probe.CommandText);
        Assert.Matches(
            @"^SELECT m\.attraction_id, m\.name, m\.latitude, m\.longitude\s+FROM andril_hobbies\.map_attractions AS m\s+ORDER BY m\.attraction_id\s+LIMIT @\w+ OFFSET @\w+\s*$",
            probe.CommandText);
        Assert.Equal(new[] { 17, 23 }, probe.ParameterValues!.Order());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [MapCapturePostgresFact]
    public Task Discover_PagesStableCanonicalRecordsAndLeavesTrackedStateUntouched() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);
            Assert.Empty(await repository.DiscoverAsync(new MapAttractionDiscoveryQuery(0, 1)));

            var count = MapAttractionDiscoveryQuery.MaximumLimit + 1;
            var ids = Enumerable.Range(1, count)
                .Select(index => new Guid($"00000000-0000-4000-8000-{index:D12}"))
                .ToArray();
            context.MapAttractions.AddRange(ids.Reverse().Select(id => MapAttraction.Create(id, Name, 7.5, -80.5)));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var pending = MapAttraction.Create(MissingId, "Pending attraction", 0, 0);
            context.MapAttractions.Add(pending);
            var first = await repository.DiscoverAsync(new MapAttractionDiscoveryQuery(0, count - 1));
            var second = await repository.DiscoverAsync(new MapAttractionDiscoveryQuery(count - 1, count - 1));
            Assert.Equal(count - 1, first.Count);
            Assert.Single(second);
            Assert.Equal(ids, first.Concat(second).Select(item => item.AttractionId));
            Assert.All(first.Concat(second), item =>
            {
                Assert.Equal(Name, item.Name);
                Assert.Equal(7.5, item.Latitude);
                Assert.Equal(-80.5, item.Longitude);
            });
            Assert.Empty(await repository.DiscoverAsync(new MapAttractionDiscoveryQuery(count + 1, 1)));
            var tracked = Assert.Single(context.ChangeTracker.Entries());
            Assert.Same(pending, tracked.Entity);
            Assert.Equal(EntityState.Added, tracked.State);
        });

    [Fact]
    public void Model_RegistersAttractionWithRequiredPostgresMappings()
    {
        using var context = CreateContext(ModelOnlyConnection);
        var entity = context.MapAttractions.EntityType;
        Assert.Same(entity, context.Model.FindEntityType(typeof(MapAttraction)));
        Assert.Equal("map_attractions", entity.GetTableName());

        var key = entity.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Equal("pk_map_attractions", key.GetName());
        var id = Assert.Single(key.Properties);
        Assert.Equal(nameof(MapAttraction.AttractionId), id.Name);
        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);

        var table = StoreObjectIdentifier.Table("map_attractions", entity.GetSchema());
        foreach (var (propertyName, column, type) in new[]
        {
            (nameof(MapAttraction.AttractionId), "attraction_id", "uuid"),
            (nameof(MapAttraction.Name), "name", "text"),
            (nameof(MapAttraction.Latitude), "latitude", "double precision"),
            (nameof(MapAttraction.Longitude), "longitude", "double precision")
        })
        {
            var property = entity.FindProperty(propertyName);
            Assert.NotNull(property);
            Assert.False(property.IsNullable);
            Assert.Equal(column, property.GetColumnName(table));
            Assert.Equal(type, property.GetColumnType());
        }

        // Check constraints are exposed by the design-time model.
        var designModel = context.GetService<IDesignTimeModel>().Model;
        Assert.Equal(
            new[]
            {
                "ck_map_attraction_id", "ck_map_attraction_latitude",
                "ck_map_attraction_longitude", "ck_map_attraction_name"
            },
            designModel.FindEntityType(typeof(MapAttraction))!.GetCheckConstraints()
                .Select(constraint => constraint.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Model_PreservesCaptureSessionMapping()
    {
        using var context = CreateContext(ModelOnlyConnection);
        var entity = context.CaptureSessions.EntityType;
        Assert.Equal("map_contribution_capture_sessions", entity.GetTableName());
        var key = Assert.Single(entity.FindPrimaryKey()!.Properties);
        Assert.Equal(nameof(MapContributionCaptureSession.SessionId), key.Name);
        Assert.Equal(ValueGenerated.Never, key.ValueGenerated);
        Assert.False(entity.FindProperty(nameof(MapContributionCaptureSession.OwnerHobbiesModuleProfileId))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(MapContributionCaptureSession.AttractionId))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(MapContributionCaptureSession.StartedAtUtc))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(MapContributionCaptureSession.CompletedAtUtc))!.IsNullable);
        Assert.Equal("ix_map_capture_owner_attraction", Assert.Single(entity.GetIndexes()).GetDatabaseName());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task AddAndFind_RoundTripsImmutableValuesWithoutTracking() =>
        WithDatabaseAsync(async connection =>
        {
            var attraction = CreateAttraction();
            await using (var writer = CreateContext(connection))
            {
                await new MapAttractionRepository<HobbiesMapDbContext>(writer).AddAsync(attraction);
                Assert.Equal(EntityState.Detached, writer.Entry(attraction).State);
            }

            await using var reader = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(reader);
            var saved = await repository.FindByIdAsync(AttractionId);
            Assert.NotSame(attraction, saved);
            AssertValues(attraction, saved);
            Assert.Null(await repository.FindByIdAsync(MissingId));
            Assert.Null(await repository.FindByIdAsync(Guid.Empty));
            Assert.Empty(reader.ChangeTracker.Entries());
        });

    [MapCapturePostgresFact]
    public Task Add_DuplicateKeyPropagatesFailureAndLeavesOriginalUnchanged() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);
            var original = CreateAttraction();
            await repository.AddAsync(original);

            var duplicate = MapAttraction.Create(AttractionId, "Replacement", 0, 0);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(duplicate));
            AssertDuplicateKey(error);
            Assert.Equal(EntityState.Detached, context.Entry(duplicate).State);
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(1, await context.MapAttractions.CountAsync());
            AssertValues(original, await repository.FindByIdAsync(AttractionId));
        });

    [MapCapturePostgresFact]
    public Task Add_ConcurrentDuplicateKeyAllowsExactlyOneInsert() =>
        WithDatabaseAsync(async connection =>
        {
            await using var firstContext = CreateContext(connection);
            await using var secondContext = CreateContext(connection);
            await firstContext.Database.OpenConnectionAsync();
            await secondContext.Database.OpenConnectionAsync();
            var first = CreateAttraction();
            var second = MapAttraction.Create(AttractionId, "Second attraction", -10, 20);

            var errors = await Task.WhenAll(
                Record.ExceptionAsync(() => new MapAttractionRepository<HobbiesMapDbContext>(firstContext).AddAsync(first)),
                Record.ExceptionAsync(() => new MapAttractionRepository<HobbiesMapDbContext>(secondContext).AddAsync(second)));

            Assert.Contains(errors, error => error is null);
            var failure = Assert.Single(errors, error => error is not null);
            AssertDuplicateKey(Assert.IsType<DbUpdateException>(failure));
            Assert.Empty(firstContext.ChangeTracker.Entries());
            Assert.Empty(secondContext.ChangeTracker.Entries());

            await using var reader = CreateContext(connection);
            var saved = await reader.MapAttractions.AsNoTracking().SingleAsync();
            AssertValues(errors[0] is null ? first : second, saved);
        });

    [MapCapturePostgresFact]
    public Task Database_RejectsInvalidValuesWithoutDomainValidation() =>
        WithDatabaseAsync(async connection =>
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();

            foreach (var (id, name, latitude, longitude, constraint) in new
                (Guid Id, string Name, double Latitude, double Longitude, string Constraint)[]
            {
                (Guid.Empty, Name, 0, 0, "ck_map_attraction_id"),
                (AttractionId, "", 0, 0, "ck_map_attraction_name"),
                (AttractionId, " \t\r\n", 0, 0, "ck_map_attraction_name"),
                (AttractionId, Name, -90.000001, 0, "ck_map_attraction_latitude"),
                (AttractionId, Name, 90.000001, 0, "ck_map_attraction_latitude"),
                (AttractionId, Name, double.NaN, 0, "ck_map_attraction_latitude"),
                (AttractionId, Name, double.NegativeInfinity, 0, "ck_map_attraction_latitude"),
                (AttractionId, Name, double.PositiveInfinity, 0, "ck_map_attraction_latitude"),
                (AttractionId, Name, 0, -180.000001, "ck_map_attraction_longitude"),
                (AttractionId, Name, 0, 180.000001, "ck_map_attraction_longitude"),
                (AttractionId, Name, 0, double.NaN, "ck_map_attraction_longitude"),
                (AttractionId, Name, 0, double.NegativeInfinity, "ck_map_attraction_longitude"),
                (AttractionId, Name, 0, double.PositiveInfinity, "ck_map_attraction_longitude")
            })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => InsertAsync(database, id, name, latitude, longitude));
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal(constraint, error.ConstraintName);
            }

            foreach (var (id, name, latitude, longitude, column) in new
                (Guid? Id, string? Name, double? Latitude, double? Longitude, string Column)[]
            {
                (null, Name, 0, 0, "attraction_id"),
                (AttractionId, null, 0, 0, "name"),
                (AttractionId, Name, null, 0, "latitude"),
                (AttractionId, Name, 0, null, "longitude")
            })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(
                    () => InsertAsync(database, id, name, latitude, longitude));
                Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
                Assert.Equal(column, error.ColumnName);
            }

            await using var context = CreateContext(connection);
            Assert.Equal(0, await context.MapAttractions.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Database_AcceptsZeroAndInclusiveCoordinateBoundaries() =>
        WithDatabaseAsync(async connection =>
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var context = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);

            foreach (var (id, latitude, longitude) in new[]
            {
                (AttractionId, 0d, 0d),
                (Guid.Parse("55555555-5555-4555-8555-555555555555"), -90d, -180d),
                (Guid.Parse("66666666-6666-4666-8666-666666666666"), 90d, 180d)
            })
            {
                Assert.Equal(1, await InsertAsync(database, id, Name, latitude, longitude));
                AssertValues(MapAttraction.Create(id, Name, latitude, longitude), await repository.FindByIdAsync(id));
            }
        });

    [MapCapturePostgresFact]
    public Task Add_PreservesUnrelatedPendingChangesAndRejectsTrackedInput() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);
            var attraction = CreateAttraction();
            var session = MapContributionCaptureSession.Create(
                new Guid("11111111-1111-4111-8111-111111111111"),
                new Guid("22222222-2222-4222-8222-222222222222"),
                AttractionId, new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
            var sessionEntry = context.CaptureSessions.Add(session);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(attraction));
            Assert.Equal(EntityState.Added, sessionEntry.State);
            Assert.Equal(EntityState.Detached, context.Entry(attraction).State);
            Assert.Same(session, Assert.Single(context.ChangeTracker.Entries()).Entity);
            Assert.Equal(0, await context.CaptureSessions.CountAsync());
            Assert.Equal(0, await context.MapAttractions.CountAsync());

            await context.SaveChangesAsync();
            var attractionEntry = context.Attach(attraction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(attraction));
            Assert.Equal(EntityState.Unchanged, attractionEntry.State);
            Assert.Equal(EntityState.Unchanged, sessionEntry.State);
            Assert.Equal(0, await context.MapAttractions.CountAsync());

            attractionEntry.State = EntityState.Detached;
            await repository.AddAsync(attraction);
            Assert.Equal(EntityState.Detached, context.Entry(attraction).State);
            Assert.Same(session, Assert.Single(context.ChangeTracker.Entries()).Entity);
        });

    [MapCapturePostgresFact]
    public Task Repository_PreCancelledOperationsThrowAndDoNotWrite() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);
            await repository.AddAsync(CreateAttraction());
            var token = new CancellationToken(canceled: true);
            var pending = MapAttraction.Create(MissingId, "Cancelled attraction", 0, 0);

            var addError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.AddAsync(pending, token));
            Assert.Equal(token, addError.CancellationToken);
            foreach (var id in new[] { AttractionId, MissingId, Guid.Empty })
            {
                var findError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => repository.FindByIdAsync(id, token));
                Assert.Equal(token, findError.CancellationToken);
            }
            var discoveryError = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.DiscoverAsync(new MapAttractionDiscoveryQuery(0, 1), token));
            Assert.Equal(token, discoveryError.CancellationToken);

            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(1, await context.MapAttractions.CountAsync());
            Assert.Null(await repository.FindByIdAsync(MissingId));
        });

    [MapCapturePostgresFact]
    public Task Repository_PropagatesDatabaseFailuresForWritesAndReads() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE andril_hobbies.map_attractions RENAME TO temporarily_unavailable_attractions");
            var repository = new MapAttractionRepository<HobbiesMapDbContext>(context);
            var attraction = CreateAttraction();

            var writeError = await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(attraction));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, Assert.IsType<PostgresException>(writeError.InnerException).SqlState);
            Assert.Equal(EntityState.Detached, context.Entry(attraction).State);
            Assert.Empty(context.ChangeTracker.Entries());

            var readError = await Assert.ThrowsAsync<PostgresException>(() => repository.FindByIdAsync(AttractionId));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, readError.SqlState);

            var discoveryError = await Assert.ThrowsAsync<PostgresException>(
                () => repository.DiscoverAsync(new MapAttractionDiscoveryQuery(0, 1)));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, discoveryError.SqlState);
        });

    private static MapAttraction CreateAttraction() =>
        MapAttraction.Create(AttractionId, Name, 7.123456789, -80.987654321);

    private static void AssertValues(MapAttraction expected, MapAttraction? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.AttractionId, actual.AttractionId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Latitude, actual.Latitude);
        Assert.Equal(expected.Longitude, actual.Longitude);
    }

    private static void AssertDuplicateKey(DbUpdateException error)
    {
        var postgresError = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        Assert.Equal("pk_map_attractions", postgresError.ConstraintName);
    }

    private static async Task<int> InsertAsync(
        NpgsqlConnection connection, Guid? id, string? name, double? latitude, double? longitude)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO andril_hobbies.map_attractions (attraction_id, name, latitude, longitude) VALUES ($1, $2, $3, $4)",
            connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)id ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)name ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)latitude ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Double, Value = (object?)longitude ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync();
    }

    private static HobbiesMapDbContext CreateContext(string connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql(connection).AddInterceptors(interceptors).Options);

    private sealed class OfflineDiscoveryProbe : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public InvalidOperationException Stop { get; } = new("Offline discovery SQL captured.");
        public string? CommandText { get; private set; }
        public int[]? ParameterValues { get; private set; }

        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            CommandText = result.CommandText;
            ParameterValues = result.Parameters.Cast<DbParameter>().Select(parameter => (int)parameter.Value!).ToArray();
            result.Dispose();
            throw Stop;
        }

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            // Let EF create the command without opening an actual PostgreSQL connection.
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private static async Task WithDatabaseAsync(Func<string, Task> test)
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

        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1")
            || settings.Database != "postgres")
        {
            throw new InvalidOperationException("Use a local PostgreSQL server and its postgres maintenance database.");
        }

        // Use a literal loopback address and verify the server endpoint before creating anything.
        if (settings.Host == "localhost")
            settings.Host = "127.0.0.1";

        settings.Pooling = false;
        settings.Enlist = false;
        settings.IncludeErrorDetail = false;
        settings.SearchPath = "public";

        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand(
            "SELECT inet_server_addr() IN ('127.0.0.1'::inet, '::1'::inet)", admin))
        {
            if (await verify.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("The PostgreSQL server must report a loopback address.");
        }

        var databaseName = $"andril_m3_attraction_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand(
            $"CREATE DATABASE \"{databaseName}\" TEMPLATE template0", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            settings.Database = databaseName;
            var connection = settings.ConnectionString;
            await using (var context = CreateContext(connection))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await test(connection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
