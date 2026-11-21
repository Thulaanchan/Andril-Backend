using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class MapContributionPersistenceTests
{
    private const string ModelOnly = "Host=127.0.0.1;Database=andril_model_only;Username=unused";
    private static readonly Guid Id = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Owner = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Attraction = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Session = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Other = new("55555555-5555-4555-8555-555555555555");
    private static readonly DateTimeOffset SubmittedAt = new(2026, 11, 17, 10, 0, 0, TimeSpan.FromHours(5.5));
    private static readonly MapContributionLocationEvidence Evidence =
        new(7.1234, 80.5678, 7.1235, 80.5679, 12.5, SubmittedAt.AddSeconds(-30), SubmittedAt.AddSeconds(-5));

    [Fact]
    public async Task FindOwned_BindsRequestedOwnerAndContributionInTheDatabasePredicateOffline()
    {
        var probe = new OfflineOwnerQueryProbe();
        await using var context = CreateContext(interceptor: probe);
        var storedContribution = CreateContribution();
        context.Attach(storedContribution);

        var failure = await Assert.ThrowsAsync<OfflineReadStopped>(
            () => Repository(context).FindOwnedAsync(storedContribution.ContributionId, Other));

        Assert.Same(probe.Stop, failure);
        Assert.NotNull(probe.Sql);
        // The requested owner must constrain the database read even when the contribution
        // is already tracked under a different owner.
        var predicate = Regex.Match(probe.Sql,
            @"\bFROM map_contributions AS (?<alias>\w+)\s+WHERE \k<alias>\.contribution_id = @(?<id>\w+) AND \k<alias>\.owner_hobbies_module_profile_id = @(?<owner>\w+)\s+LIMIT 2\s*$");
        Assert.True(predicate.Success, probe.Sql);
        Assert.Equal(Id, probe.Parameters[predicate.Groups["id"].Value]);
        Assert.Equal(Other, probe.Parameters[predicate.Groups["owner"].Value]);
        var tracked = Assert.Single(context.ChangeTracker.Entries());
        Assert.Same(storedContribution, tracked.Entity);
        Assert.Equal(EntityState.Unchanged, tracked.State);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Model_RegistersContributionWithRequiredScalarMappingsAndNamedChecks()
    {
        using var context = CreateContext();
        var entity = context.MapContributions.EntityType;
        Assert.Same(entity, context.Model.FindEntityType(typeof(MapContribution)));
        Assert.Equal("map_contributions", entity.GetTableName());
        var key = entity.FindPrimaryKey()!;
        Assert.Equal("pk_map_contributions", key.GetName());
        var id = Assert.Single(key.Properties);
        Assert.Equal(nameof(MapContribution.ContributionId), id.Name);
        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);

        foreach (var (name, column, type) in new[]
        {
            (nameof(MapContribution.ContributionId), "contribution_id", "uuid"),
            (nameof(MapContribution.OwnerHobbiesModuleProfileId), "owner_hobbies_module_profile_id", "uuid"),
            (nameof(MapContribution.AttractionId), "attraction_id", "uuid"),
            (nameof(MapContribution.CaptureSessionId), "capture_session_id", "uuid"),
            (nameof(MapContribution.PrivatePhotoStorageObjectKey), "private_photo_storage_object_key", "text"),
            (nameof(MapContribution.Description), "description", "text"),
            (nameof(MapContribution.LocationEvidence), "location_evidence", "jsonb"),
            (nameof(MapContribution.SubmittedAtUtc), "submitted_at_utc", "timestamp with time zone"),
            (nameof(MapContribution.ModerationStatus), "moderation_status", "integer")
        })
        {
            var property = entity.FindProperty(name);
            Assert.NotNull(property);
            Assert.Equal(column, property.GetColumnName());
            Assert.Equal(type, property.GetColumnType());
            Assert.False(property.IsNullable);
        }

        var converter = entity.FindProperty(nameof(MapContribution.ModerationStatus))!.GetTypeMapping().Converter!;
        Assert.Equal(1, Assert.IsType<int>(converter.ConvertToProvider(MapContributionModerationStatus.PendingReview)));
        Assert.Equal(MapContributionModerationStatus.PendingReview,
            Assert.IsType<MapContributionModerationStatus>(converter.ConvertFromProvider(1)));
        Assert.Null(context.Model.FindEntityType(typeof(MapContributionLocationEvidence)));
        var design = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(MapContribution))!;
        Assert.Equal(new[]
        {
            "ck_map_contribution_attraction_id", "ck_map_contribution_capture_session_id",
            "ck_map_contribution_description", "ck_map_contribution_id", "ck_map_contribution_location_evidence",
            "ck_map_contribution_moderation_status", "ck_map_contribution_owner_id", "ck_map_contribution_photo_key"
        }, design.GetCheckConstraints().Select(check => check.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Model_ModerationCheckMatchesPendingReviewAndAppearsInGeneratedSql()
    {
        using var context = CreateContext();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(MapContribution))!;
        var check = Assert.Single(entity.GetCheckConstraints(),
            constraint => constraint.Name == "ck_map_contribution_moderation_status");

        Assert.Equal(MapContributionModerationStatus.PendingReview, Assert.Single(Enum.GetValues<MapContributionModerationStatus>()));
        Assert.Equal("moderation_status = 1", check.Sql);
        Assert.Contains("CONSTRAINT ck_map_contribution_moderation_status CHECK (moderation_status = 1)",
            context.Database.GenerateCreateScript());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void Model_PreservesAttractionAndCaptureSessionMappings()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var attraction = model.FindEntityType(typeof(MapAttraction))!;
        Assert.Equal("map_attractions", attraction.GetTableName());
        Assert.Equal(nameof(MapAttraction.AttractionId), Assert.Single(attraction.FindPrimaryKey()!.Properties).Name);
        Assert.Equal("pk_map_attractions", attraction.FindPrimaryKey()!.GetName());
        Assert.Equal("double precision", attraction.FindProperty(nameof(MapAttraction.Latitude))!.GetColumnType());
        Assert.Equal("double precision", attraction.FindProperty(nameof(MapAttraction.Longitude))!.GetColumnType());
        Assert.Equal(4, attraction.GetCheckConstraints().Count());

        var session = model.FindEntityType(typeof(MapContributionCaptureSession))!;
        Assert.Equal("map_contribution_capture_sessions", session.GetTableName());
        Assert.Equal(nameof(MapContributionCaptureSession.SessionId), Assert.Single(session.FindPrimaryKey()!.Properties).Name);
        Assert.False(session.FindProperty(nameof(MapContributionCaptureSession.StartedAtUtc))!.IsNullable);
        Assert.True(session.FindProperty(nameof(MapContributionCaptureSession.CompletedAtUtc))!.IsNullable);
        Assert.Equal("ix_map_capture_owner_attraction", Assert.Single(session.GetIndexes()).GetDatabaseName());
        Assert.Equal(4, session.GetCheckConstraints().Count());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Model_RepositoryAcceptsScalarEvidenceAndDetachesAfterInterceptedSave()
    {
        await using var context = CreateContext(interceptor: new StopBeforeSave());
        var contribution = CreateContribution();
        await Assert.ThrowsAsync<OfflineSaveStopped>(() => Repository(context).AddAsync(contribution));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void EvidenceConverter_PreservesAllFieldsAndExplicitNulls()
    {
        using var context = CreateContext();
        var converter = EvidenceConverter(context);
        Assert.Equal(typeof(string), converter.ProviderClrType);
        foreach (var original in new[] { Evidence, new MapContributionLocationEvidence(null, null, null, null, null, null, null) })
        {
            var stored = Assert.IsType<string>(converter.ConvertToProvider(original));
            using var json = JsonDocument.Parse(stored);
            Assert.Equal(7, json.RootElement.EnumerateObject().Count());
            var restored = Assert.IsType<MapContributionLocationEvidence>(converter.ConvertFromProvider(stored));
            AssertEvidence(original, restored);
            Assert.NotSame(original, restored);
        }
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"UserLatitude\":7.1}")]
    public void EvidenceConverter_RejectsMalformedOrIncompleteJson(string json)
    {
        using var context = CreateContext();
        Assert.Throws<JsonException>(() => EvidenceConverter(context).ConvertFromProvider(json));
    }

    [Theory]
    [InlineData("\"Unexpected\":null,")]
    [InlineData("\"UserLatitude\":null,")]
    public void EvidenceConverter_RejectsUnexpectedOrDuplicateFields(string prefix)
    {
        using var context = CreateContext();
        var converter = EvidenceConverter(context);
        var json = Assert.IsType<string>(converter.ConvertToProvider(Evidence)).Insert(1, prefix);
        Assert.Throws<JsonException>(() => converter.ConvertFromProvider(json));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EvidenceConverter_RejectsNonFiniteValuesOnWrite(double number)
    {
        using var context = CreateContext();
        Assert.Throws<JsonException>(() => EvidenceConverter(context)
            .ConvertToProvider(Evidence with { HorizontalAccuracyMetres = number }));
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("\"NaN\"")]
    public void EvidenceConverter_RejectsUnsupportedNumbersOnRead(string number)
    {
        using var context = CreateContext();
        var converter = EvidenceConverter(context);
        var json = Assert.IsType<string>(converter.ConvertToProvider(Evidence))
            .Replace("\"HorizontalAccuracyMetres\":12.5", $"\"HorizontalAccuracyMetres\":{number}", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => converter.ConvertFromProvider(json));
    }

    [MapCapturePostgresFact]
    public Task AddAndFind_RoundTripsImmutableContributionAndEnforcesOwnerScope() =>
        WithDatabaseAsync(async connection =>
        {
            var contribution = CreateContribution();
            await using (var writer = CreateContext(connection))
            {
                await Repository(writer).AddAsync(contribution);
                Assert.Empty(writer.ChangeTracker.Entries());
            }

            await using var reader = CreateContext(connection);
            var repository = Repository(reader);
            var saved = await repository.FindOwnedAsync(Id, Owner);
            Assert.NotSame(contribution, saved);
            AssertContribution(contribution, saved);
            Assert.Null(await repository.FindOwnedAsync(Id, Other));
            Assert.Null(await repository.FindOwnedAsync(Other, Owner));
            Assert.Null(await repository.FindOwnedAsync(Guid.Empty, Owner));
            Assert.Null(await repository.FindOwnedAsync(Id, Guid.Empty));
            Assert.Empty(reader.ChangeTracker.Entries());
        });

    [MapCapturePostgresFact]
    public Task Add_DuplicateIdRejectsInsertAndPreservesOriginal() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var original = CreateContribution();
            await repository.AddAsync(original);
            var duplicate = CreateContribution("Replacement");
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(duplicate));
            AssertDuplicateKey(error);
            Assert.Equal(EntityState.Detached, context.Entry(duplicate).State);
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(1, await context.MapContributions.CountAsync());
            AssertContribution(original, await repository.FindOwnedAsync(Id, Owner));
        });

    [MapCapturePostgresFact]
    public Task Add_ConcurrentDuplicateIdAllowsExactlyOneInsert() =>
        WithDatabaseAsync(async connection =>
        {
            await using var firstContext = CreateContext(connection);
            await using var secondContext = CreateContext(connection);
            await firstContext.Database.OpenConnectionAsync();
            await secondContext.Database.OpenConnectionAsync();
            var first = CreateContribution("First");
            var second = CreateContribution("Second");
            var errors = await Task.WhenAll(
                Record.ExceptionAsync(() => Repository(firstContext).AddAsync(first)),
                Record.ExceptionAsync(() => Repository(secondContext).AddAsync(second)));

            Assert.Contains(errors, error => error is null);
            AssertDuplicateKey(Assert.IsType<DbUpdateException>(Assert.Single(errors, error => error is not null)));
            Assert.Empty(firstContext.ChangeTracker.Entries());
            Assert.Empty(secondContext.ChangeTracker.Entries());
            await using var reader = CreateContext(connection);
            AssertContribution(errors[0] is null ? first : second, await reader.MapContributions.AsNoTracking().SingleAsync());
        });

    [MapCapturePostgresFact]
    public Task Database_RejectsInvalidShapesAndNullsWithExistingConstraints() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var json = Assert.IsType<string>(EvidenceConverter(context).ConvertToProvider(Evidence));
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO map_contributions (contribution_id, owner_hobbies_module_profile_id, attraction_id,
                    capture_session_id, private_photo_storage_object_key, description, location_evidence,
                    submitted_at_utc, moderation_status)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                """, database);
            var columns = new (string Name, NpgsqlDbType Type, object Value)[]
            {
                ("contribution_id", NpgsqlDbType.Uuid, Id),
                ("owner_hobbies_module_profile_id", NpgsqlDbType.Uuid, Owner),
                ("attraction_id", NpgsqlDbType.Uuid, Attraction),
                ("capture_session_id", NpgsqlDbType.Uuid, Session),
                ("private_photo_storage_object_key", NpgsqlDbType.Text, "map/photo.jpg"),
                ("description", NpgsqlDbType.Text, "Synthetic"),
                ("location_evidence", NpgsqlDbType.Jsonb, json),
                ("submitted_at_utc", NpgsqlDbType.TimestampTz, SubmittedAt.ToUniversalTime()),
                ("moderation_status", NpgsqlDbType.Integer, (int)MapContributionModerationStatus.PendingReview)
            };
            foreach (var (_, type, value) in columns)
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value });

            foreach (var (index, invalid, constraint) in new (int Index, object Value, string Constraint)[]
            {
                (0, Guid.Empty, "ck_map_contribution_id"),
                (1, Guid.Empty, "ck_map_contribution_owner_id"),
                (2, Guid.Empty, "ck_map_contribution_attraction_id"),
                (3, Guid.Empty, "ck_map_contribution_capture_session_id"),
                (4, " \t\r\n", "ck_map_contribution_photo_key"),
                (5, "", "ck_map_contribution_description"),
                (5, " \t\r\n", "ck_map_contribution_description"),
                (6, "[]", "ck_map_contribution_location_evidence"),
                (6, "null", "ck_map_contribution_location_evidence")
            })
            {
                insert.Parameters[index].Value = invalid;
                var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal(constraint, error.ConstraintName);
                insert.Parameters[index].Value = columns[index].Value;
            }

            for (var index = 0; index < columns.Length; index++)
            {
                insert.Parameters[index].Value = DBNull.Value;
                var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
                Assert.Equal(columns[index].Name, error.ColumnName);
                insert.Parameters[index].Value = columns[index].Value;
            }
            Assert.Equal(0, await context.MapContributions.CountAsync());
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        });

    [MapCapturePostgresFact]
    public Task Database_AcceptsPendingReviewAndRejectsUndefinedModerationIntegers() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var original = CreateContribution();
            await repository.AddAsync(original);

            Task<int> SetStatusAsync(int status) => context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE map_contributions SET moderation_status = {status} WHERE contribution_id = {Id}");

            Assert.Equal(1, await SetStatusAsync(1));
            foreach (var unsupported in new[] { 0, -1, 2, 42, int.MinValue, int.MaxValue })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => SetStatusAsync(unsupported));
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal("ck_map_contribution_moderation_status", error.ConstraintName);
            }

            AssertContribution(original, await repository.FindOwnedAsync(Id, Owner));
        });

    [MapCapturePostgresFact]
    public Task Add_PreservesUnrelatedEntriesAndRejectsAlreadyTrackedInput() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var contribution = CreateContribution();
            var attraction = MapAttraction.Create(Attraction, "Synthetic attraction", 7.1235, 80.5679);
            var unrelated = context.MapAttractions.Add(attraction);

            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(contribution));
            Assert.Equal(EntityState.Added, unrelated.State);
            Assert.Same(attraction, Assert.Single(context.ChangeTracker.Entries()).Entity);
            Assert.Equal(0, await context.MapContributions.CountAsync());
            Assert.Equal(0, await context.MapAttractions.CountAsync());

            await context.SaveChangesAsync();
            var tracked = context.Attach(contribution);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(contribution));
            Assert.Equal(EntityState.Unchanged, tracked.State);
            Assert.Equal(EntityState.Unchanged, unrelated.State);
            Assert.Equal(0, await context.MapContributions.CountAsync());
            tracked.State = EntityState.Detached;

            await repository.AddAsync(contribution);
            Assert.Equal(EntityState.Detached, context.Entry(contribution).State);
            Assert.Same(attraction, Assert.Single(context.ChangeTracker.Entries()).Entity);
            Assert.Equal(1, await context.MapContributions.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Repository_PreCancelledOperationsThrowWithoutWriting() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var token = new CancellationToken(canceled: true);
            var add = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AddAsync(CreateContribution(), token));
            var find = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindOwnedAsync(Id, Owner, token));
            Assert.Equal(token, add.CancellationToken);
            Assert.Equal(token, find.CancellationToken);
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(0, await context.MapContributions.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Repository_PropagatesWriteAndReadFailures() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            await context.Database.ExecuteSqlRawAsync("DROP TABLE map_contributions");
            var repository = Repository(context);
            var contribution = CreateContribution();
            var write = await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(contribution));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, Assert.IsType<PostgresException>(write.InnerException).SqlState);
            Assert.Equal(EntityState.Detached, context.Entry(contribution).State);
            Assert.Empty(context.ChangeTracker.Entries());
            var read = await Assert.ThrowsAsync<PostgresException>(() => repository.FindOwnedAsync(Id, Owner));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, read.SqlState);
        });

    private static MapContribution CreateContribution(string description = "  Synthetic contribution  ") =>
        MapContribution.Create(Id, Owner, Attraction, Session, "map/capture/photo.jpg", description, Evidence, SubmittedAt);

    private static IMapContributionRepository Repository(HobbiesMapDbContext context) =>
        new MapContributionRepository<HobbiesMapDbContext>(context);

    private static ValueConverter EvidenceConverter(HobbiesMapDbContext context) =>
        context.MapContributions.EntityType.FindProperty(nameof(MapContribution.LocationEvidence))!.GetTypeMapping().Converter!;

    private static void AssertEvidence(MapContributionLocationEvidence expected, MapContributionLocationEvidence actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(expected.CaptureStartedAt?.Offset, actual.CaptureStartedAt?.Offset);
        Assert.Equal(expected.CaptureCompletedAt?.Offset, actual.CaptureCompletedAt?.Offset);
    }

    private static void AssertContribution(MapContribution expected, MapContribution? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ContributionId, actual.ContributionId);
        Assert.Equal(expected.OwnerHobbiesModuleProfileId, actual.OwnerHobbiesModuleProfileId);
        Assert.Equal(expected.AttractionId, actual.AttractionId);
        Assert.Equal(expected.CaptureSessionId, actual.CaptureSessionId);
        Assert.Equal(expected.PrivatePhotoStorageObjectKey, actual.PrivatePhotoStorageObjectKey);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.SubmittedAtUtc, actual.SubmittedAtUtc);
        Assert.Equal(TimeSpan.Zero, actual.SubmittedAtUtc.Offset);
        Assert.Equal(MapContributionModerationStatus.PendingReview, actual.ModerationStatus);
        AssertEvidence(expected.LocationEvidence, actual.LocationEvidence);
    }

    private static void AssertDuplicateKey(DbUpdateException error)
    {
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("pk_map_contributions", postgres.ConstraintName);
    }

    private static HobbiesMapDbContext CreateContext(string connection = ModelOnly, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(connection);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new HobbiesMapDbContext(options.Options);
    }

    private sealed class OfflineReadStopped : Exception;

    private sealed class OfflineOwnerQueryProbe : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public OfflineReadStopped Stop { get; } = new();
        public string? Sql { get; private set; }
        public IReadOnlyDictionary<string, object?> Parameters { get; private set; } =
            new Dictionary<string, object?>();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            using (result)
            {
                Sql = result.CommandText;
                Parameters = result.Parameters.Cast<DbParameter>()
                    .ToDictionary(parameter => parameter.ParameterName.TrimStart('@'), parameter => parameter.Value);
                throw Stop;
            }
        }
    }

    private sealed class OfflineSaveStopped : Exception;

    private sealed class StopBeforeSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entry = Assert.Single(eventData.Context!.ChangeTracker.Entries());
            Assert.IsType<MapContribution>(entry.Entity);
            Assert.Equal(EntityState.Added, entry.State);
            throw new OfflineSaveStopped();
        }
    }

    // Reuse the M3 opt-in attribute. The existing database lifecycle helpers are private to their fixtures.
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

        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Use a local PostgreSQL server and its postgres maintenance database.");

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

        var databaseName = $"andril_m3_contribution_test_{Guid.NewGuid():N}";
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
