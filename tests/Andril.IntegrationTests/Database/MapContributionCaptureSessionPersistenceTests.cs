
using System;
using System.Linq;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>
/// Enables PostgreSQL tests only when a dedicated local
/// M3 test connection is explicitly configured.
/// </summary>
public sealed class MapCapturePostgresFactAttribute
    : FactAttribute
{
    public MapCapturePostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                "ANDRIL_M3_TEST_POSTGRES")))
        {
            Skip = "Disposable local PostgreSQL is not configured.";
        }
    }
}

public sealed class MapContributionCaptureSessionPersistenceTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ProfileId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid AttractionId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset StartUtc =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private static MapContributionCaptureSession CreateSession()
    {
        return MapContributionCaptureSession.Create(
            SessionId,
            ProfileId,
            AttractionId,
            StartUtc);
    }

    private static HobbiesMapDbContext CreateContext(
        string connectionString)
    {
        var options =
            new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        return new HobbiesMapDbContext(options);
    }

    private static async Task WithTestDatabaseAsync(
        Func<string, Task> test)
    {
        var configuredConnection =
            Environment.GetEnvironmentVariable(
                "ANDRIL_M3_TEST_POSTGRES");

        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            throw new InvalidOperationException(
                "Local PostgreSQL test connection is missing.");
        }

        var settings =
            new NpgsqlConnectionStringBuilder(
                configuredConnection);

        // Fail closed for external or production hosts.
        if (settings.Host != "localhost"
            && settings.Host != "127.0.0.1"
            && settings.Host != "::1")
        {
            throw new InvalidOperationException(
                "Only local PostgreSQL hosts are allowed.");
        }

        if (settings.Database != "postgres")
        {
            throw new InvalidOperationException(
                "Use the local postgres maintenance database.");
        }

        settings.Pooling = false;
        settings.Enlist = false;

        // Every test owns a newly generated database.
        var databaseName =
            $"andril_m3_map_test_{Guid.NewGuid():N}";

        await using var admin =
            new NpgsqlConnection(settings.ConnectionString);

        await admin.OpenAsync();

        var created = false;

        try
        {
            // The database identifier is generated internally.
            await using (var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\" " +
                "TEMPLATE template0",
                admin))
            {
                await create.ExecuteNonQueryAsync();
            }

            created = true;

            var testSettings =
                new NpgsqlConnectionStringBuilder(
                    settings.ConnectionString)
                {
                    Database = databaseName
                };

            var testConnection =
                testSettings.ConnectionString;

            await using (var context =
                CreateContext(testConnection))
            {
                await context.Database.EnsureCreatedAsync();
            }

            await test(testConnection);
        }
        finally
        {
            // Drop only the database created by this test.
            if (created)
            {
                await using var drop = new NpgsqlCommand(
                    $"DROP DATABASE \"{databaseName}\"",
                    admin);

                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    // TEST 01
    // Runs without opening a database connection.
    [Fact]
    public void Model_ContainsCaptureSessionMapping()
    {
        const string unusedConnection =
            "Host=127.0.0.1;" +
            "Database=andril_model_only;" +
            "Username=unused";

        using var context =
            CreateContext(unusedConnection);

        var entity = context.Model.FindEntityType(
            typeof(MapContributionCaptureSession))
            ?? throw new InvalidOperationException(
                "Capture session mapping is missing.");

        Assert.Equal(
            "map_contribution_capture_sessions",
            entity.GetTableName());

        var primaryKey = entity.FindPrimaryKey()
            ?? throw new InvalidOperationException(
                "Capture session primary key is missing.");

        Assert.Equal(
            "SessionId",
            Assert.Single(primaryKey.Properties).Name);

        Assert.NotNull(
            entity.FindProperty(
                "OwnerHobbiesModuleProfileId"));

        Assert.NotNull(
            entity.FindProperty("AttractionId"));

        Assert.NotNull(
            entity.FindProperty("StartedAtUtc"));

        Assert.True(
            entity.FindProperty("CompletedAtUtc")!
                .IsNullable);
    }

    // TEST 02
    // Insert, retrieve and ownership isolation.
    [MapCapturePostgresFact]
    public async Task AddAndFind_EnforcesSessionScope()
    {
        await WithTestDatabaseAsync(async connection =>
        {
            await using (var writeContext =
                CreateContext(connection))
            {
                var writer =
                    new MapContributionCaptureSessionRepository
                        <HobbiesMapDbContext>(writeContext);

                await writer.AddAsync(CreateSession());
            }

            await using var readContext =
                CreateContext(connection);

            var repository =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(readContext);

            var session = await repository.FindOwnedAsync(
                SessionId,
                ProfileId,
                AttractionId);

            Assert.NotNull(session);

            Assert.Equal(SessionId, session.SessionId);
            Assert.Equal(
                ProfileId,
                session.OwnerHobbiesModuleProfileId);

            Assert.Equal(
                AttractionId,
                session.AttractionId);

            Assert.Equal(StartUtc, session.StartedAtUtc);
            Assert.Null(session.CompletedAtUtc);

            Assert.Null(await repository.FindOwnedAsync(
                SessionId,
                Guid.NewGuid(),
                AttractionId));

            Assert.Null(await repository.FindOwnedAsync(
                SessionId,
                ProfileId,
                Guid.NewGuid()));

            Assert.Null(await repository.FindOwnedAsync(
                Guid.NewGuid(),
                ProfileId,
                AttractionId));

            Assert.Empty(
                readContext.ChangeTracker.Entries());
        });
    }

    // TEST 03
    // Duplicate primary key protection.
    [MapCapturePostgresFact]
    public async Task Add_DuplicateSessionId_IsRejected()
    {
        await WithTestDatabaseAsync(async connection =>
        {
            await using var context =
                CreateContext(connection);

            var repository =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(context);

            await repository.AddAsync(CreateSession());

            var error =
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => repository.AddAsync(CreateSession()));

            var postgresError =
                Assert.IsType<PostgresException>(
                    error.InnerException);

            Assert.Equal(
                PostgresErrorCodes.UniqueViolation,
                postgresError.SqlState);

            Assert.Equal(
                1,
                await context.CaptureSessions.CountAsync());
        });
    }

    // TEST 04
    // Scoped completion, invalid time and replay protection.
    [MapCapturePostgresFact]
    public async Task Complete_EnforcesScopeTimeAndState()
    {
        await WithTestDatabaseAsync(async connection =>
        {
            await using var context =
                CreateContext(connection);

            var repository =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(context);

            await repository.AddAsync(CreateSession());

            var completedAt = StartUtc.AddSeconds(60);

            Assert.False(await repository.TryCompleteAsync(
                SessionId,
                Guid.NewGuid(),
                AttractionId,
                completedAt));

            Assert.False(await repository.TryCompleteAsync(
                SessionId,
                ProfileId,
                Guid.NewGuid(),
                completedAt));

            Assert.False(await repository.TryCompleteAsync(
                SessionId,
                ProfileId,
                AttractionId,
                StartUtc.AddSeconds(-1)));

            var incomplete = await repository.FindOwnedAsync(
                SessionId,
                ProfileId,
                AttractionId);

            Assert.NotNull(incomplete);
            Assert.Null(incomplete.CompletedAtUtc);

            Assert.True(await repository.TryCompleteAsync(
                SessionId,
                ProfileId,
                AttractionId,
                completedAt.ToOffset(
                    TimeSpan.FromMinutes(330))));

            Assert.False(await repository.TryCompleteAsync(
                SessionId,
                ProfileId,
                AttractionId,
                completedAt.AddSeconds(10)));

            var saved = await repository.FindOwnedAsync(
                SessionId,
                ProfileId,
                AttractionId);

            Assert.NotNull(saved);

            Assert.Equal(
                completedAt,
                saved.CompletedAtUtc);

            Assert.Equal(
                TimeSpan.Zero,
                saved.CompletedAtUtc!.Value.Offset);
        });
    }

    // TEST 05
    // Concurrent completion attempts.
    [MapCapturePostgresFact]
    public async Task Complete_ConcurrentAttempts_OnlyOneWins()
    {
        await WithTestDatabaseAsync(async connection =>
        {
            await using (var seedContext =
                CreateContext(connection))
            {
                var repository =
                    new MapContributionCaptureSessionRepository
                        <HobbiesMapDbContext>(seedContext);

                await repository.AddAsync(CreateSession());
            }

            await using var contextA =
                CreateContext(connection);

            await using var contextB =
                CreateContext(connection);

            var repositoryA =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(contextA);

            var repositoryB =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(contextB);

            var timeA = StartUtc.AddSeconds(30);
            var timeB = StartUtc.AddSeconds(45);

            var resultA = repositoryA.TryCompleteAsync(
                SessionId,
                ProfileId,
                AttractionId,
                timeA);

            var resultB = repositoryB.TryCompleteAsync(
                SessionId,
                ProfileId,
                AttractionId,
                timeB);

            var results = await Task.WhenAll(
                resultA,
                resultB);

            Assert.Single(results.Where(result => result));
            Assert.Single(results.Where(result => !result));

            await using var verifyContext =
                CreateContext(connection);

            var saved = await verifyContext
                .CaptureSessions
                .AsNoTracking()
                .SingleAsync();

            var winningTime =
                results[0] ? timeA : timeB;

            Assert.Equal(
                winningTime,
                saved.CompletedAtUtc);
        });
    }

    // TEST 06
    // Verify database constraints independently of
    // domain model validation.
    [MapCapturePostgresFact]
    public async Task Database_RejectsInvalidStoredValues()
    {
        await WithTestDatabaseAsync(async connection =>
        {
            await using var context =
                CreateContext(connection);

            var repository =
                new MapContributionCaptureSessionRepository
                    <HobbiesMapDbContext>(context);

            await repository.AddAsync(CreateSession());

            var emptyId = Guid.Empty;

            var ownerError =
                await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database
                        .ExecuteSqlInterpolatedAsync(
                            $"UPDATE andril_hobbies.map_contribution_capture_sessions SET owner_hobbies_module_profile_id = {emptyId} WHERE session_id = {SessionId}"));

            Assert.Equal(
                PostgresErrorCodes.CheckViolation,
                ownerError.SqlState);

            var attractionError =
                await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database
                        .ExecuteSqlInterpolatedAsync(
                            $"UPDATE andril_hobbies.map_contribution_capture_sessions SET attraction_id = {emptyId} WHERE session_id = {SessionId}"));

            Assert.Equal(
                PostgresErrorCodes.CheckViolation,
                attractionError.SqlState);

            var invalidTime = StartUtc.AddSeconds(-1);

            var timestampError =
                await Assert.ThrowsAsync<PostgresException>(
                    () => context.Database
                        .ExecuteSqlInterpolatedAsync(
                            $"UPDATE andril_hobbies.map_contribution_capture_sessions SET completed_at_utc = {invalidTime} WHERE session_id = {SessionId}"));

            Assert.Equal(
                PostgresErrorCodes.CheckViolation,
                timestampError.SqlState);

            var saved = await context
                .CaptureSessions
                .AsNoTracking()
                .SingleAsync();

            Assert.Equal(
                ProfileId,
                saved.OwnerHobbiesModuleProfileId);

            Assert.Equal(
                AttractionId,
                saved.AttractionId);

            Assert.Null(saved.CompletedAtUtc);
        });
    }
}
