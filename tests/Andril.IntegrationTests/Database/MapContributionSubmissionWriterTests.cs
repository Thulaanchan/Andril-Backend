using System;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class MapContributionSubmissionWriterTests
{
    [MapCapturePostgresFact]
    public Task SubmissionWindowIsRecheckedAfterTheSessionLockAndBeforeCommit() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            foreach (var scenario in new[] { "under", "boundary", "expired", "future", "expires during save" })
            {
                var factory = new Contexts(connection);
                var prepared = await PrepareAsync(factory);
                var completed = prepared.Contribution.LocationEvidence.CaptureCompletedAt!.Value;
                var clock = new TestClock
                {
                    Now = scenario switch
                    {
                        "under" => completed.AddMinutes(10).AddTicks(-1),
                        "expired" => completed.AddMinutes(10).AddTicks(1),
                        "future" => completed.AddTicks(-1),
                        _ => completed.AddMinutes(10)
                    }
                };
                var interceptor = new AdvanceAfterInsert(clock, completed.AddMinutes(10).AddTicks(1),
                    scenario == "expires during save");
                var writer = new MapContributionSubmissionWriter(new Contexts(connection, interceptor), clock);
                var result = await writer.SubmitAsync(prepared);
                var accepted = scenario is "under" or "boundary";
                Assert.Equal(accepted ? MapSubmissionOutcome.Created : MapSubmissionOutcome.Denied, result.Outcome);
                Assert.Equal(scenario is "under" or "boundary" or "expires during save", interceptor.Inserted);
                await using var probe = factory.CreateDbContext();
                var rows = await probe.MapContributions.AsNoTracking()
                    .Where(row => row.CaptureSessionId == prepared.Contribution.CaptureSessionId).ToListAsync();
                if (accepted)
                {
                    Assert.Equal(MapContributionModerationStatus.PendingReview, Assert.Single(rows).ModerationStatus);
                    clock.Now = completed.AddDays(1);
                    Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, result.ContributionId),
                        await writer.SubmitAsync(prepared));
                }
                else
                {
                    Assert.Empty(rows);
                    Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await writer.SubmitAsync(prepared));
                }
                Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            }
        });

    [MapCapturePostgresFact]
    public Task ExpiryWhileWaitingForSessionLockDeniesPreviouslyPreparedSubmission() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var token = deadline.Token;
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var completed = prepared.Contribution.LocationEvidence.CaptureCompletedAt!.Value;
            var clock = new TestClock { Now = completed.AddMinutes(9) };
            await using var blocker = factory.CreateDbContext();
            await using var transaction = await blocker.Database.BeginTransactionAsync(token);
            await blocker.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions
                WHERE session_id = {prepared.Contribution.CaptureSessionId} FOR UPDATE
                """, token);
            var lockProbe = new SessionLockProbe();
            var attempt = new MapContributionSubmissionWriter(new Contexts(connection, lockProbe), clock)
                .SubmitAsync(prepared, token);
            var released = false;
            try
            {
                var waitingPid = await lockProbe.Attempted.Task.WaitAsync(token);
                await using var observer = new NpgsqlConnection(connection);
                await observer.OpenAsync(token);
                await using var blocking = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@waiting))", observer);
                blocking.Parameters.AddWithValue("blocker", ((NpgsqlConnection)blocker.Database.GetDbConnection()).ProcessID);
                blocking.Parameters.AddWithValue("waiting", waitingPid);
                while (await blocking.ExecuteScalarAsync(token) is not true)
                {
                    Assert.False(attempt.IsCompleted);
                    await Task.Delay(20, token);
                }
                clock.Now = completed.AddMinutes(10).AddTicks(1);
                await transaction.RollbackAsync(token);
                released = true;
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await attempt.WaitAsync(token));
                await using var probe = factory.CreateDbContext();
                Assert.Empty(await probe.MapContributions.ToListAsync(token));
                Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync(token));
            }
            finally
            {
                if (!released)
                    await transaction.RollbackAsync(CancellationToken.None);
                await attempt;
            }
        });

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdvanceAfterInsert(TestClock clock, DateTimeOffset next, bool advance) : SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Inserted = true;
            if (advance)
                clock.Now = next;
            return ValueTask.FromResult(result);
        }
    }

    [MapCapturePostgresFact]
    public Task NamedUniqueViolationReconcilesOnlyThePreparedOwnerAndAttraction() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            foreach (var scope in new[] { "matching", "foreign owner", "wrong attraction" })
            {
                var factory = new Contexts(connection);
                var prepared = await PrepareAsync(factory);
                var original = prepared.Contribution;
                var persistedId = Guid.NewGuid();
                var owner = scope == "foreign owner" ? Guid.NewGuid() : original.OwnerHobbiesModuleProfileId;
                var attraction = scope == "wrong attraction" ? Guid.NewGuid() : original.AttractionId;
                var conflict = new RealDatabaseFailure(original, primaryKey: false);
                var reconcileFactory = new Contexts(connection, conflict, created =>
                {
                    if (created != 2)
                        return;
                    // Simulate a committed competing record after the failed writer releases
                    // its session lock. The reconciliation query must use a fresh context.
                    using var seed = factory.CreateDbContext();
                    seed.Database.ExecuteSqlInterpolated($"""
                        UPDATE andril_hobbies.map_contribution_capture_sessions
                        SET owner_hobbies_module_profile_id = {owner}, attraction_id = {attraction}
                        WHERE session_id = {original.CaptureSessionId}
                        """);
                    seed.MapContributions.Add(Copy(original, persistedId, owner, attraction));
                    seed.SaveChanges();
                });

                var result = await new MapContributionSubmissionWriter(reconcileFactory).SubmitAsync(prepared);
                Assert.Equal(2, reconcileFactory.Created);
                var error = Assert.IsType<PostgresException>(conflict.Error);
                Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
                Assert.Equal("andril_hobbies", error.SchemaName);
                Assert.Equal("map_contributions", error.TableName);
                Assert.Equal("ux_map_contributions_capture_session_id", error.ConstraintName);
                Assert.Equal(scope == "matching"
                    ? new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, persistedId)
                    : new MapSubmissionResult(MapSubmissionOutcome.Denied), result);
                await using var probe = factory.CreateDbContext();
                var rows = await probe.MapContributions.AsNoTracking()
                    .Where(row => row.CaptureSessionId == original.CaptureSessionId).ToListAsync();
                Assert.Equal(persistedId, Assert.Single(rows).ContributionId);
                Assert.DoesNotContain(rows, row => row.ContributionId == original.ContributionId);
                Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            }
        });

    [MapCapturePostgresFact]
    public Task UnrelatedPrimaryKeyAndCheckViolationsNeverEnterReconciliation() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            foreach (var primaryKey in new[] { true, false })
            {
                var factory = new Contexts(connection);
                var prepared = await PrepareAsync(factory);
                var failure = new RealDatabaseFailure(prepared.Contribution, primaryKey, check: !primaryKey);
                var failingFactory = new Contexts(connection, failure);
                var result = await new MapContributionSubmissionWriter(failingFactory).SubmitAsync(prepared);
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), result);
                Assert.Equal(1, failingFactory.Created);
                var error = Assert.IsType<PostgresException>(failure.Error);
                Assert.Equal(primaryKey ? PostgresErrorCodes.UniqueViolation : PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal(primaryKey ? "pk_map_contributions" : "ck_map_contribution_description", error.ConstraintName);
                await using var probe = factory.CreateDbContext();
                Assert.Empty(await probe.MapContributions.ToListAsync());
                Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            }
        });

    [MapCapturePostgresFact]
    public Task FailedFirstWriterRollsBackWhileBlockedSecondWriterCommitsExactlyOneRow() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var token = deadline.Token;
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var firstBarrier = new PauseAfterInsert();
            var secondProbe = new SessionLockProbe();
            var first = new MapContributionSubmissionWriter(new Contexts(connection, firstBarrier))
                .SubmitAsync(prepared, token);
            Task<MapSubmissionResult>? second = null;
            try
            {
                var firstPid = await firstBarrier.Inserted.Task.WaitAsync(token);
                await using var observer = new NpgsqlConnection(connection);
                await observer.OpenAsync(token);
                await using (var count = new NpgsqlCommand(
                    "SELECT count(*) FROM andril_hobbies.map_contributions", observer))
                    Assert.Equal(0L, await count.ExecuteScalarAsync(token));
                second = new MapContributionSubmissionWriter(new Contexts(connection, secondProbe))
                    .SubmitAsync(prepared, token);
                var secondPid = await secondProbe.Attempted.Task.WaitAsync(token);
                Assert.NotEqual(firstPid, secondPid);
                await using var blocking = new NpgsqlCommand(
                    "SELECT @first = ANY(pg_blocking_pids(@second))", observer);
                blocking.Parameters.AddWithValue("first", firstPid);
                blocking.Parameters.AddWithValue("second", secondPid);
                while (await blocking.ExecuteScalarAsync(token) is not true)
                {
                    Assert.False(second.IsCompleted, "The contender must remain blocked by the first writer.");
                    await Task.Delay(20, token);
                }
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
                firstBarrier.Release.TrySetResult();
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), await first.WaitAsync(token));
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Created, prepared.Contribution.ContributionId),
                    await second.WaitAsync(token));
                await using var probe = factory.CreateDbContext();
                var row = Assert.Single(await probe.MapContributions.ToListAsync());
                Assert.Equal(prepared.Contribution.ContributionId, row.ContributionId);
                Assert.Equal(MapContributionModerationStatus.PendingReview, row.ModerationStatus);
                Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            }
            finally
            {
                firstBarrier.Release.TrySetResult();
                await first;
                if (second is not null)
                    await second;
            }
        });

    private static MapContribution Copy(MapContribution row, Guid id, Guid owner, Guid attraction) =>
        MapContribution.Create(id, owner, attraction, row.CaptureSessionId,
            row.PrivatePhotoStorageObjectKey, row.Description, row.LocationEvidence, row.SubmittedAtUtc);

    private sealed class RealDatabaseFailure(MapContribution row, bool primaryKey, bool check = false)
        : SaveChangesInterceptor
    {
        public PostgresException? Error { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            if (check)
            {
                context.Entry(row).Property(value => value.Description).CurrentValue = " ";
                return result;
            }
            var capture = primaryKey ? Guid.NewGuid() : row.CaptureSessionId;
            if (primaryKey)
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_hobbies.map_contribution_capture_sessions
                        (session_id, owner_hobbies_module_profile_id, attraction_id, started_at_utc, completed_at_utc)
                    SELECT {capture}, owner_hobbies_module_profile_id, attraction_id, started_at_utc, completed_at_utc
                    FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id = {row.CaptureSessionId}
                    """, cancellationToken);
            var id = primaryKey ? row.ContributionId : Guid.NewGuid();
            var evidence = JsonSerializer.Serialize(row.LocationEvidence);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_hobbies.map_contributions
                    (contribution_id, owner_hobbies_module_profile_id, attraction_id, capture_session_id,
                     private_photo_storage_object_key, description, location_evidence, submitted_at_utc, moderation_status)
                VALUES ({id}, {row.OwnerHobbiesModuleProfileId}, {row.AttractionId}, {capture},
                        {row.PrivatePhotoStorageObjectKey}, {row.Description}, CAST({evidence} AS jsonb), {row.SubmittedAtUtc}, 1)
                """, cancellationToken);
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Error = (eventData.Exception as DbUpdateException)?.InnerException as PostgresException;
            return Task.CompletedTask;
        }
    }

    private sealed class PauseAfterInsert : SaveChangesInterceptor
    {
        public TaskCompletionSource<int> Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Inserted.TrySetResult(((NpgsqlConnection)eventData.Context!.Database.GetDbConnection()).ProcessID);
            await Release.Task.WaitAsync(cancellationToken);
            throw new NpgsqlException("Synthetic failure after insert and before commit.");
        }
    }

    private sealed class SessionLockProbe : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                Attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }

    [MapCapturePostgresFact]
    public Task LockedSessionCancellationAndRollbackLeaveIndependentSessionAvailable() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var independent = await PrepareAsync(factory);
            var writer = new MapContributionSubmissionWriter(factory);
            await using var blocker = factory.CreateDbContext();
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions
                WHERE session_id = {prepared.Contribution.CaptureSessionId} FOR UPDATE
                """);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SubmitAsync(prepared, canceled.Token));
            using var waitingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.SubmitAsync(prepared, waitingCancellation.Token));
            var waiting = writer.SubmitAsync(prepared);
            var separate = await writer.SubmitAsync(independent).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(MapSubmissionOutcome.Created, separate.Outcome);
            Assert.False(waiting.IsCompleted);
            await transaction.RollbackAsync();
            Assert.Equal(MapSubmissionOutcome.Created, (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        });

    [MapCapturePostgresFact]
    public Task ParallelRequestsAndRetriesReturnOnlyTheAuthorizedExistingRecord() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var first = await PrepareAsync(factory);
            var writer = new MapContributionSubmissionWriter(factory);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.SubmitAsync(first)));
            Assert.Single(results, result => result.Outcome == MapSubmissionOutcome.Created);
            Assert.Equal(7, results.Count(result => result.Outcome == MapSubmissionOutcome.ExistingAuthorized));
            Assert.All(results, result => Assert.Equal(first.Contribution.ContributionId, result.ContributionId));
            await using var probe = factory.CreateDbContext();
            Assert.Equal(1, await probe.MapContributions.CountAsync());
            Assert.Equal(MapContributionModerationStatus.PendingReview,
                (await probe.MapContributions.SingleAsync()).ModerationStatus);
            Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
            Assert.Equal(MapSubmissionOutcome.ExistingAuthorized, (await writer.SubmitAsync(first)).Outcome);
        });

    [MapCapturePostgresFact]
    public Task ChangedSessionScopeTimingOrCanonicalCoordinatesDenyWithoutWriting() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            foreach (var column in new[] { "owner_hobbies_module_profile_id", "attraction_id", "completed_at_utc", "started_at_utc", "missing", "gps" })
            {
                var prepared = await PrepareAsync(factory);
                var row = prepared.Contribution;
                await using var probe = factory.CreateDbContext();
                FormattableString mutation = column switch
                {
                    "missing" =>
                        $"DELETE FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id = {row.CaptureSessionId}",
                    "gps" =>
                        $"UPDATE andril_hobbies.map_attractions SET latitude = 45 WHERE attraction_id = {row.AttractionId}",
                    "owner_hobbies_module_profile_id" =>
                        $"UPDATE andril_hobbies.map_contribution_capture_sessions SET owner_hobbies_module_profile_id = {Guid.NewGuid()} WHERE session_id = {row.CaptureSessionId}",
                    "attraction_id" =>
                        $"UPDATE andril_hobbies.map_contribution_capture_sessions SET attraction_id = {Guid.NewGuid()} WHERE session_id = {row.CaptureSessionId}",
                    "completed_at_utc" =>
                        $"UPDATE andril_hobbies.map_contribution_capture_sessions SET completed_at_utc = NULL WHERE session_id = {row.CaptureSessionId}",
                    "started_at_utc" =>
                        $"UPDATE andril_hobbies.map_contribution_capture_sessions SET started_at_utc = started_at_utc - interval '1 second' WHERE session_id = {row.CaptureSessionId}",
                    _ => throw new InvalidOperationException("Unknown test mutation.")
                };
                await probe.Database.ExecuteSqlInterpolatedAsync(mutation);
                var result = await new MapContributionSubmissionWriter(factory).SubmitAsync(prepared);
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), result);
                Assert.Empty(await probe.MapContributions.ToListAsync());
            }
        });

    [MapCapturePostgresFact]
    public Task FailedInsertRollsBackAndCommitAcknowledgementLossIsUnknown() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var failing = new Contexts(connection, new FailInsert());
            Assert.Equal(MapSubmissionOutcome.InfrastructureFailure,
                (await new MapContributionSubmissionWriter(failing).SubmitAsync(prepared)).Outcome);
            await using var probe = factory.CreateDbContext();
            Assert.Empty(await probe.MapContributions.ToListAsync());
            var ambiguous = new Contexts(connection, new LoseAcknowledgement());
            Assert.Equal(MapSubmissionOutcome.OutcomeUnknown,
                (await new MapContributionSubmissionWriter(ambiguous).SubmitAsync(prepared)).Outcome);
            Assert.Equal(1, await probe.MapContributions.CountAsync());
            Assert.Equal(MapSubmissionOutcome.ExistingAuthorized,
                (await new MapContributionSubmissionWriter(factory).SubmitAsync(prepared)).Outcome);
        });

    private sealed class FailInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new NpgsqlException("Synthetic insert failure");
    }

    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            throw new NpgsqlException("Synthetic acknowledgement loss");
    }

    private static async Task<MapContributionSubmissionPreflight.PreparedSubmission> PrepareAsync(Contexts factory)
    {
        await using var context = factory.CreateDbContext();
        var owner = Guid.NewGuid();
        var attraction = MapAttraction.Create(Guid.NewGuid(), "Synthetic", 0, 0);
        var session = MapContributionCaptureSession.Create(Guid.NewGuid(), owner, attraction.AttractionId, DateTimeOffset.UtcNow.AddSeconds(-10));
        session.Complete(session.StartedAtUtc.AddSeconds(5));
        context.MapAttractions.Add(attraction);
        context.CaptureSessions.Add(session);
        await context.SaveChangesAsync();
        var ports = new TrustedTestPorts(owner);
        var preflight = new MapContributionSubmissionPreflight(ports,
            new MapContributionCaptureSessionRepository<HobbiesMapDbContext>(context),
            new MapAttractionRepository<HobbiesMapDbContext>(context), ports);
        return Assert.IsType<MapContributionSubmissionPreflight.PreparedSubmission>(
            await preflight.PrepareAsync(attraction.AttractionId, session.SessionId,
                new MapContributionLocationEvidence(0, 0, 90, 90, 5, null, null), "private/photo", "Description"));
    }

    private sealed class TrustedTestPorts(Guid owner) : IMapCaptureSessionAccessReader, IMapContributionPhotoEvidenceReader
    {
        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(owner);
        public Task<MapContributionPhotoVerificationResult> VerifyAsync(Guid profile, Guid attraction,
            Guid session, string? key, CancellationToken cancellationToken) =>
            Task.FromResult(MapContributionPhotoVerificationResult.Verified);
    }

    private sealed class Contexts(string connection, IInterceptor? interceptor = null, Action<int>? beforeCreate = null)
        : IDbContextFactory<HobbiesMapDbContext>
    {
        public int Created { get; private set; }

        public HobbiesMapDbContext CreateDbContext()
        {
            Created++;
            beforeCreate?.Invoke(Created);
            var options = new DbContextOptionsBuilder<HobbiesMapDbContext>()
                .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return new(options.Options);
        }
    }
}
