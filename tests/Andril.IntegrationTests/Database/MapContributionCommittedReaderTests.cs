using System.Data;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using TransactionScope = System.Transactions.TransactionScope;
using TransactionScopeAsyncFlowOption = System.Transactions.TransactionScopeAsyncFlowOption;

namespace Andril.IntegrationTests.Database;

public sealed class MapContributionCommittedReaderTests
{
    private const string Offline = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=private-test-secret;Enlist=false";

    [Theory]
    [InlineData("owner")]
    [InlineData("attraction")]
    [InlineData("session")]
    public async Task EmptyScopeDoesNotOpenAConnection(string empty)
    {
        await using var source = NpgsqlDataSource.Create(Offline);
        IMapContributionCommittedReader reader = new MapContributionCommittedReader(source);
        Assert.Null(await reader.FindOwnedByCaptureSessionAsync(empty == "session" ? Guid.Empty : Guid.NewGuid(),
            empty == "owner" ? Guid.Empty : Guid.NewGuid(), empty == "attraction" ? Guid.Empty : Guid.NewGuid()));
    }

    [Theory]
    [InlineData("enlist")]
    [InlineData("multiplexing")]
    [InlineData("no-reset")]
    public async Task UnsafeConnectionSettingsAreRejectedWithoutConnectingOrExposingSecrets(string unsafeSetting)
    {
        var settings = new NpgsqlConnectionStringBuilder(Offline)
        {
            Enlist = unsafeSetting == "enlist", Multiplexing = unsafeSetting == "multiplexing",
            NoResetOnClose = unsafeSetting == "no-reset"
        };
        await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
        var error = Assert.Throws<ArgumentException>(() => new MapContributionCommittedReader(source));
        Assert.DoesNotContain("private-test-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task AmbientTransactionIsRejectedBeforeOpeningAConnection()
    {
        await using var source = NpgsqlDataSource.Create(Offline);
        using var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MapContributionCommittedReader(source)
            .FindOwnedByCaptureSessionAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("Committed contribution reads do not accept ambient transactions.", error.Message);
    }

    [Fact]
    public async Task AlreadyCancelledReadNeverOpensAConnection()
    {
        await using var source = NpgsqlDataSource.Create(Offline);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MapContributionCommittedReader(source)
            .FindOwnedByCaptureSessionAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [MapCapturePostgresFact]
    public Task CommittedLookupRequiresAllScopeIdentifiersAndReturnsOriginalEvidenceWithoutWrites() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var context = Context(connection);
            var seed = await Seed(context);
            await new MapContributionRepository<HobbiesMapDbContext>(context).AddAsync(seed);
            await using var source = Source(connection);
            var reader = new MapContributionCommittedReader(source);
            var found = Assert.IsType<MapContribution>(await Read(reader, seed));
            Assert.Equal(seed.ContributionId, found.ContributionId);
            Assert.Equal(seed.PrivatePhotoStorageObjectKey, found.PrivatePhotoStorageObjectKey);
            Assert.Equal(seed.Description, found.Description);
            Assert.Equal(seed.LocationEvidence, found.LocationEvidence);
            Assert.Equal(MapContributionModerationStatus.PendingReview, found.ModerationStatus);
            Assert.Null(await reader.FindOwnedByCaptureSessionAsync(seed.CaptureSessionId, Guid.NewGuid(), seed.AttractionId));
            Assert.Null(await reader.FindOwnedByCaptureSessionAsync(seed.CaptureSessionId, seed.OwnerHobbiesModuleProfileId, Guid.NewGuid()));
            Assert.Null(await reader.FindOwnedByCaptureSessionAsync(Guid.NewGuid(), seed.OwnerHobbiesModuleProfileId, seed.AttractionId));
            Assert.Single(await context.MapContributions.AsNoTracking().ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        });

    [MapCapturePostgresFact]
    public Task UncommittedInsertIsInvisibleAndNextExplicitReadSeesCommit() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var writer = Context(connection);
            var seed = await Seed(writer);
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await new MapContributionRepository<HobbiesMapDbContext>(writer).AddAsync(seed);
            Assert.Equal(1, await writer.MapContributions.CountAsync()); // The writer can see its own uncommitted insert.
            await using var source = Source(connection);
            var reader = new MapContributionCommittedReader(source);
            Assert.Null(await Read(reader, seed));
            await transaction.CommitAsync();
            Assert.Equal(seed.ContributionId, (await Read(reader, seed))!.ContributionId);
            Assert.Equal(1, await writer.MapContributions.CountAsync());
            Assert.Empty(await writer.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task RolledBackInsertRemainsMissingAndReaderNeverWritesItBack() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var writer = Context(connection);
            var seed = await Seed(writer);
            await using var source = Source(connection);
            var reader = new MapContributionCommittedReader(source);
            await using (var transaction = await writer.Database.BeginTransactionAsync())
            {
                await new MapContributionRepository<HobbiesMapDbContext>(writer).AddAsync(seed);
                Assert.Null(await Read(reader, seed));
                await transaction.RollbackAsync();
            }
            Assert.Null(await Read(reader, seed));
            Assert.Empty(await writer.MapContributions.ToListAsync());
            Assert.Empty(await writer.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task DatabaseAssertsReadOnlyReadCommittedAndIndependentBackend() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var writer = Context(connection);
            var seed = await Seed(writer);
            await new MapContributionRepository<HobbiesMapDbContext>(writer).AddAsync(seed);
            await writer.Database.OpenConnectionAsync();
            var writerPid = ((NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID;
            // Disposable test-only RLS assertion: the production schema/mappings remain unchanged.
            await using (var setup = new NpgsqlCommand("""
                CREATE TABLE public.m3_reader_expectation (writer_pid integer NOT NULL);
                INSERT INTO public.m3_reader_expectation VALUES (@pid);
                CREATE FUNCTION public.m3_assert_reader_boundary() RETURNS boolean LANGUAGE plpgsql AS $$
                BEGIN
                    IF current_setting('transaction_isolation') <> 'read committed'
                        OR current_setting('transaction_read_only') <> 'on'
                        OR pg_backend_pid() = (SELECT writer_pid FROM public.m3_reader_expectation)
                    THEN RAISE EXCEPTION 'Invalid reader boundary'; END IF;
                    RETURN true;
                END $$;
                ALTER TABLE andril_hobbies.map_contributions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_hobbies.map_contributions FORCE ROW LEVEL SECURITY;
                CREATE POLICY m3_reader_boundary ON andril_hobbies.map_contributions
                    USING (public.m3_assert_reader_boundary());
                """, (NpgsqlConnection)writer.Database.GetDbConnection()))
            {
                setup.Parameters.AddWithValue("pid", writerPid);
                await setup.ExecuteNonQueryAsync();
            }
            var settings = new NpgsqlConnectionStringBuilder(connection)
            {
                Enlist = false, Options = "-c default_transaction_isolation=serializable -c default_transaction_read_only=off"
            };
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            Assert.Equal(seed.ContributionId, (await Read(new(source), seed))!.ContributionId);
        });

    [MapCapturePostgresFact]
    public Task CancellationDuringRealLockWaitDisposesConnectionAndNextReadCanContinue() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var blocker = Context(connection);
            var seed = await Seed(blocker);
            await new MapContributionRepository<HobbiesMapDbContext>(blocker).AddAsync(seed);
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE andril_hobbies.map_contributions IN ACCESS EXCLUSIVE MODE");
            var blockerPid = ((NpgsqlConnection)blocker.Database.GetDbConnection()).ProcessID;
            var application = "m3_committed_reader_" + Guid.NewGuid().ToString("N");
            var settings = new NpgsqlConnectionStringBuilder(connection) { Enlist = false, ApplicationName = application };
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            var reader = new MapContributionCommittedReader(source);
            using var cancellation = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var attempt = Read(reader, seed, cancellation.Token);
            try
            {
                await using var observer = new NpgsqlConnection(connection);
                await observer.OpenAsync(deadline.Token);
                await using var waiting = new NpgsqlCommand("""
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE application_name = @application AND @blocker = ANY(pg_blocking_pids(pid)))
                    """, observer);
                waiting.Parameters.AddWithValue("application", application);
                waiting.Parameters.AddWithValue("blocker", blockerPid);
                while (await waiting.ExecuteScalarAsync(deadline.Token) is not true)
                {
                    Assert.False(attempt.IsCompleted);
                    await Task.Delay(20, deadline.Token);
                }
                cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(deadline.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            finally
            {
                cancellation.Cancel();
                await transaction.RollbackAsync();
                try { await attempt; } catch (OperationCanceledException) { }
            }
            Assert.Equal(seed.ContributionId, (await Read(reader, seed))!.ContributionId);
            Assert.Equal(1, await blocker.MapContributions.CountAsync());
            Assert.Empty(await blocker.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task MappingAndConnectionFailuresAreSanitizedInsteadOfReportedAsMissing() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            await using var context = Context(connection);
            var seed = await Seed(context);
            await new MapContributionRepository<HobbiesMapDbContext>(context).AddAsync(seed);
            const string malformedEvidence = "{}";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE andril_hobbies.map_contributions SET location_evidence = {malformedEvidence}::jsonb");
            await using var source = Source(connection);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(new(source), seed));
            Assert.Equal("Committed contribution retrieval is unavailable.", error.Message);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(seed.PrivatePhotoStorageObjectKey, error.ToString());
            var settings = new NpgsqlConnectionStringBuilder(connection) { Database = "missing_" + Guid.NewGuid().ToString("N"), Enlist = false };
            await using var unavailable = NpgsqlDataSource.Create(settings.ConnectionString);
            var connectionError = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(new(unavailable), seed));
            Assert.Equal(error.Message, connectionError.Message);
            Assert.Null(connectionError.InnerException);
            Assert.DoesNotContain(settings.Database, connectionError.ToString());
            Assert.Equal(1, await context.MapContributions.CountAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        });

    private static async Task<MapContribution> Seed(HobbiesMapDbContext context)
    {
        var session = await context.CaptureSessions.AsNoTracking().SingleAsync();
        return MapContribution.Create(Guid.NewGuid(), session.OwnerHobbiesModuleProfileId, session.AttractionId,
            session.SessionId, "private/committed-reader-photo", "Private evidence", new MapContributionLocationEvidence(
                7, 80, 7, 80, 5, session.StartedAtUtc, session.StartedAtUtc), DateTimeOffset.UtcNow);
    }

    private static HobbiesMapDbContext Context(string connection) => new(new DbContextOptionsBuilder<HobbiesMapDbContext>()
        .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options);
    private static NpgsqlDataSource Source(string connection) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder(connection) { Enlist = false }.ConnectionString);
    private static Task<MapContribution?> Read(MapContributionCommittedReader reader, MapContribution seed, CancellationToken token = default) =>
        reader.FindOwnedByCaptureSessionAsync(seed.CaptureSessionId, seed.OwnerHobbiesModuleProfileId, seed.AttractionId, token);
}
