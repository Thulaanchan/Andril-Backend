using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

// Candidate protocol only: real PostgreSQL locks, synthetic authorization and test-owned receipts.
// This does not extend or invoke the production submission writer, verify photos, or grant rewards.
public sealed class MapAttractionStatePrototypeTests
{
    [Fact]
    public void PrototypeDoesNotChangeTheProductionModel()
    {
        using var context = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Username=unused", HobbiesDatabaseOptions.ConfigureMapHistory).Options);
        Assert.Null(context.Model.FindEntityType(typeof(MapAttractionStatePrototype)));
        Assert.Equal(new[] { "AttractionId", "Latitude", "Longitude", "Name" },
            context.Model.FindEntityType(typeof(MapAttraction))!.GetProperties().Select(property => property.Name).Order().ToArray());
    }

    [MapCapturePostgresFact]
    public Task SubmissionBeforeDeactivationPreservesOnlyTheCommittedScopedReceipt() => WithHarness(async h =>
    {
        Assert.Equal("Created", await h.SubmitAsync(h.FirstSession, 0));
        Assert.Equal("Changed", await h.ChangeAsync(Guid.NewGuid(), false, 0));
        Assert.Equal("Existing", await h.SubmitAsync(h.FirstSession, 0));
        Assert.Equal("Conflict", await h.SubmitAsync(h.FirstSession, 0, "changed payload"));
        Assert.Equal("Denied", await h.SubmitAsync(h.SecondSession, 1));
        await h.AssertStoredAsync(false, 1, 1, 1);
    });

    [MapCapturePostgresFact]
    public Task DeactivationBeforeSubmissionRejectsBothStaleAndCurrentClientSnapshots() => WithHarness(async h =>
    {
        Assert.Equal("Changed", await h.ChangeAsync(Guid.NewGuid(), false, 0));
        Assert.Equal("Conflict", await h.SubmitAsync(h.FirstSession, 0));
        Assert.Equal("Denied", await h.SubmitAsync(h.FirstSession, 1));
        await h.AssertStoredAsync(false, 1, 0, 1);
    });

    [MapCapturePostgresFact]
    public Task CompetingSubmissionAndDeactivationAreSerializedInBothLockOrders() => WithHarness(async h =>
    {
        // Each order uses a fresh database, so the second half is executed below in its own fixture.
        await RaceAsync(h, submissionFirst: true);
    }, async h => await RaceAsync(h, submissionFirst: false));

    private static async Task RaceAsync(Harness h, bool submissionFirst)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var held = new Probe(pause: true);
        var contender = new Probe();
        var request = Guid.NewGuid();
        var first = submissionFirst ? h.SubmitAsync(h.FirstSession, 0, probe: held, token: token)
            : h.ChangeAsync(request, false, 0, held, token);
        Task<string>? second = null;
        try
        {
            var firstPid = await held.Locked.Task.WaitAsync(token);
            second = submissionFirst ? h.ChangeAsync(request, false, 0, contender, token)
                : h.SubmitAsync(h.FirstSession, 0, probe: contender, token: token);
            await h.AssertBlockedAsync(firstPid, contender, second, token);
            held.Release.SetResult();
            Assert.Equal(submissionFirst ? "Created" : "Changed", await first);
            Assert.Equal(submissionFirst ? "Changed" : "Conflict", await second);
            await h.AssertStoredAsync(false, 1, submissionFirst ? 1 : 0, 1);
        }
        finally
        {
            held.Release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
    }

    [MapCapturePostgresFact]
    public Task ReactivationCannotMakeAnOldVersionEligibleAgain() => WithHarness(async h =>
    {
        Assert.Equal("Changed", await h.ChangeAsync(Guid.NewGuid(), false, 0));
        Assert.Equal("Changed", await h.ChangeAsync(Guid.NewGuid(), true, 1));
        Assert.Equal("Conflict", await h.SubmitAsync(h.FirstSession, 0));
        Assert.Equal("Created", await h.SubmitAsync(h.FirstSession, 2));
        await h.AssertStoredAsync(true, 2, 1, 2);
    });

    [MapCapturePostgresFact]
    public Task IdenticalStateRequestReconcilesButChangedPayloadAndStaleNewRequestsConflict() => WithHarness(async h =>
    {
        var request = Guid.NewGuid();
        Assert.Equal("Changed", await h.ChangeAsync(request, false, 0));
        Assert.Equal("Existing", await h.ChangeAsync(request, false, 0));
        Assert.Equal("Conflict", await h.ChangeAsync(request, true, 0));
        Assert.Equal("Conflict", await h.ChangeAsync(request, false, 1));
        Assert.Equal("Conflict", await h.ChangeAsync(Guid.NewGuid(), false, 0));
        Assert.Equal("Unchanged", await h.ChangeAsync(Guid.NewGuid(), false, 1));
        h.AdminAuthorized = false;
        Assert.Equal("Denied", await h.ChangeAsync(request, false, 0));
        await h.AssertStoredAsync(false, 1, 0, 2);
    });

    [MapCapturePostgresFact]
    public Task ConcurrentStateRequestsWithTheSameVersionHaveOnlyOneWinner() => WithHarness(async h =>
    {
        var results = await Task.WhenAll(h.ChangeAsync(Guid.NewGuid(), false, 0), h.ChangeAsync(Guid.NewGuid(), false, 0));
        Assert.Single(results, result => result == "Changed");
        Assert.Single(results, result => result == "Conflict");
        await h.AssertStoredAsync(false, 1, 0, 1);
    });

    [MapCapturePostgresFact]
    public Task FailedDeactivationRollsBackAndReleasesTheBlockedSubmission() => WithHarness(async h =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var held = new Probe(pause: true)
        {
            BeforeCommit = (_, _, _) => throw new NpgsqlException("Synthetic failure before commit.")
        };
        var first = h.ChangeAsync(Guid.NewGuid(), false, 0, held, timeout.Token);
        Task<string>? second = null;
        try
        {
            var pid = await held.Locked.Task.WaitAsync(timeout.Token);
            var contender = new Probe();
            second = h.SubmitAsync(h.FirstSession, 0, probe: contender, token: timeout.Token);
            await h.AssertBlockedAsync(pid, contender, second, timeout.Token);
            held.Release.SetResult();
            await Assert.ThrowsAsync<NpgsqlException>(() => first);
            Assert.Equal("Created", await second);
            await h.AssertStoredAsync(true, 0, 1, 0);
        }
        finally
        {
            held.Release.TrySetResult();
            try { await first; } catch (NpgsqlException) { }
            if (second is not null) await second;
        }
    });

    [MapCapturePostgresFact]
    public Task CancellationAfterWritesRollsBackSubmissionAndStateOperations() => WithHarness(async h =>
    {
        foreach (var submission in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            var probe = new Probe { BeforeCommit = (_, _, _) => { cancellation.Cancel(); return Task.CompletedTask; } };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission
                ? h.SubmitAsync(h.FirstSession, 0, probe: probe, token: cancellation.Token)
                : h.ChangeAsync(Guid.NewGuid(), false, 0, probe, cancellation.Token));
            await h.AssertStoredAsync(true, 0, 0, 0);
        }
    });

    [MapCapturePostgresFact]
    public Task CancellationWhileWaitingForStateLockCannotCommitAReceipt() => WithHarness(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var held = new Probe(pause: true);
        var first = h.ChangeAsync(Guid.NewGuid(), false, 0, held, deadline.Token);
        Task<string>? second = null;
        try
        {
            var pid = await held.Locked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            second = h.SubmitAsync(h.FirstSession, 0, probe: waiting, token: cancellation.Token);
            await h.AssertBlockedAsync(pid, waiting, second, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            held.Release.SetResult();
            Assert.Equal("Changed", await first);
            await h.AssertStoredAsync(false, 1, 0, 1);
        }
        finally
        {
            cancellation.Cancel();
            held.Release.TrySetResult();
            await first;
            if (second is not null)
                try { await second; } catch (OperationCanceledException) { }
        }
    });

    [MapCapturePostgresFact]
    public Task PostgreSqlFailureRollsBackTheStateAndRequestRecordAndAllowsRetry() => WithHarness(async h =>
    {
        var request = Guid.NewGuid();
        var probe = new Probe
        {
            BeforeCommit = async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT 1 / 0", connection, transaction);
                await command.ExecuteScalarAsync(token);
            }
        };
        var failure = await Assert.ThrowsAsync<PostgresException>(() => h.ChangeAsync(request, false, 0, probe));
        Assert.Equal(PostgresErrorCodes.DivisionByZero, failure.SqlState);
        await h.AssertStoredAsync(true, 0, 0, 0);
        Assert.Equal("Changed", await h.ChangeAsync(request, false, 0));
    });

    [MapCapturePostgresFact]
    public Task MissingAuthorizationAndForeignSessionScopeNeverCommitReceipts() => WithHarness(async h =>
    {
        foreach (var owner in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
        {
            h.Access.Owner = owner;
            Assert.Equal("Denied", await h.SubmitAsync(h.FirstSession, 0));
        }
        h.Access.Owner = h.Owner;
        Assert.Equal("Denied", await h.SubmitAsync(Guid.NewGuid(), 0));
        h.AdminAuthorized = false;
        Assert.Equal("Denied", await h.ChangeAsync(Guid.NewGuid(), false, 0));
        await h.AssertStoredAsync(true, 0, 0, 0);
    });

    [MapCapturePostgresFact]
    public Task LostCommitAcknowledgementRequiresFreshScopedReconciliation() => WithHarness(async h =>
    {
        var request = Guid.NewGuid();
        var lost = new Probe { LoseAcknowledgement = true };
        await Assert.ThrowsAsync<NpgsqlException>(() => h.SubmitAsync(h.FirstSession, 0, probe: lost));
        Assert.Equal("Existing", await h.SubmitAsync(h.FirstSession, 0));
        await Assert.ThrowsAsync<NpgsqlException>(() => h.ChangeAsync(request, false, 0, lost));
        Assert.Equal("Existing", await h.ChangeAsync(request, false, 0));
        await h.AssertStoredAsync(false, 1, 1, 1);
    });

    private static async Task WithHarness(Func<Harness, Task> test, Func<Harness, Task>? second = null)
    {
        foreach (var scenario in second is null ? new[] { test } : new[] { test, second })
            await MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
            {
                var harness = new Harness(connection);
                await harness.InitializeAsync();
                await scenario(harness);
            });
    }

    private sealed class Probe
    {
        public Probe(bool pause = false) { if (!pause) Release.SetResult(); }
        public TaskCompletionSource<int> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Locked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? BeforeCommit { get; init; }
        public bool LoseAcknowledgement { get; init; }
    }

    private sealed class SyntheticAccess(Guid owner) : IMapCaptureSessionAccessReader
    {
        public Guid? Owner { get; set; } = owner;
        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Owner);
        }
    }

    // All state/receipt/request DDL and transaction coordination below are test-owned.
    // They deliberately do not define a production repository, Admin permission contract or API outcome.
    private sealed class Harness(string connection)
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid FirstSession { get; } = Guid.NewGuid();
        public Guid SecondSession { get; } = Guid.NewGuid();
        private readonly MapAttraction _attraction = MapAttraction.Create(Guid.NewGuid(), "Prototype", 0, 0);
        private readonly Guid _admin = Guid.NewGuid();
        public SyntheticAccess Access { get; private set; } = null!;
        public bool AdminAuthorized { get; set; } = true;

        public async Task InitializeAsync()
        {
            Access = new(Owner);
            await using var context = Context();
            context.MapAttractions.Add(_attraction);
            foreach (var id in new[] { FirstSession, SecondSession })
            {
                var capture = MapContributionCaptureSession.Create(id, Owner, _attraction.AttractionId, DateTimeOffset.UtcNow);
                capture.Complete(capture.StartedAtUtc.AddSeconds(1));
                context.CaptureSessions.Add(capture);
            }
            await context.SaveChangesAsync();
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var ddl = new NpgsqlCommand("""
                CREATE SCHEMA m3_state_prototype;
                CREATE TABLE m3_state_prototype.states (
                    attraction_id uuid PRIMARY KEY, is_active boolean NOT NULL,
                    version bigint NOT NULL CHECK (version >= 0));
                CREATE TABLE m3_state_prototype.receipts (
                    session_id uuid PRIMARY KEY, owner_id uuid NOT NULL, attraction_id uuid NOT NULL,
                    payload text NOT NULL, status integer NOT NULL CHECK (status = 1),
                    FOREIGN KEY (session_id, owner_id, attraction_id) REFERENCES
                    andril_hobbies.map_contribution_capture_sessions
                    (session_id, owner_hobbies_module_profile_id, attraction_id));
                CREATE TABLE m3_state_prototype.requests (
                    actor_id uuid NOT NULL, request_id uuid NOT NULL, attraction_id uuid NOT NULL,
                    expected_version bigint NOT NULL, desired boolean NOT NULL, result_version bigint NOT NULL,
                    PRIMARY KEY (actor_id, request_id));
                """, database);
            await ddl.ExecuteNonQueryAsync();
            await using var seed = new NpgsqlCommand("INSERT INTO m3_state_prototype.states VALUES ($1, true, 0)", database);
            seed.Parameters.AddWithValue(_attraction.AttractionId);
            await seed.ExecuteNonQueryAsync();
        }

        public async Task<string> SubmitAsync(Guid session, long expectedVersion, string payload = "synthetic payload",
            Probe? probe = null, CancellationToken token = default)
        {
            var owner = await Access.ResolveAuthorizedProfileIdAsync(_attraction.AttractionId, token);
            if (owner is null || owner == Guid.Empty) return "Denied";
            return await UnderLockAsync(async (database, transaction, state) =>
            {
                await using var scope = Command(database, transaction, """
                    SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions
                    WHERE session_id = $1 AND owner_hobbies_module_profile_id = $2 AND attraction_id = $3
                      AND completed_at_utc IS NOT NULL FOR UPDATE
                    """, session, owner.Value, _attraction.AttractionId);
                if (await scope.ExecuteScalarAsync(token) is null) return "Denied";
                await using var existing = Command(database, transaction, """
                    SELECT payload FROM m3_state_prototype.receipts
                    WHERE session_id = $1 AND owner_id = $2 AND attraction_id = $3
                    """, session, owner.Value, _attraction.AttractionId);
                if (await existing.ExecuteScalarAsync(token) is string stored)
                    return stored == payload ? "Existing" : "Conflict";
                if (state.Version != expectedVersion) return "Conflict";
                if (!state.IsActive) return "Denied";
                await using var insert = Command(database, transaction,
                    "INSERT INTO m3_state_prototype.receipts VALUES ($1, $2, $3, $4, 1)",
                    session, owner.Value, _attraction.AttractionId, payload);
                await insert.ExecuteNonQueryAsync(token);
                return "Created";
            }, probe, token);
        }

        public Task<string> ChangeAsync(Guid request, bool active, long expectedVersion,
            Probe? probe = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (!AdminAuthorized) return Task.FromResult("Denied");
            return UnderLockAsync(async (database, transaction, state) =>
            {
                await using var prior = Command(database, transaction, """
                    SELECT attraction_id, expected_version, desired FROM m3_state_prototype.requests
                    WHERE actor_id = $1 AND request_id = $2
                    """, _admin, request);
                await using (var reader = await prior.ExecuteReaderAsync(token))
                    if (await reader.ReadAsync(token))
                        return reader.GetGuid(0) == state.AttractionId && reader.GetInt64(1) == expectedVersion
                            && reader.GetBoolean(2) == active ? "Existing" : "Conflict";
                if (state.Version != expectedVersion) return "Conflict";
                var next = state.Transition(active, expectedVersion);
                await using var update = Command(database, transaction, """
                    UPDATE m3_state_prototype.states SET is_active = $1, version = $2
                    WHERE attraction_id = $3 AND version = $4
                    """, next.IsActive, next.Version, next.AttractionId, expectedVersion);
                Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
                await using var log = Command(database, transaction,
                    "INSERT INTO m3_state_prototype.requests VALUES ($1, $2, $3, $4, $5, $6)",
                    _admin, request, state.AttractionId, expectedVersion, active, next.Version);
                await log.ExecuteNonQueryAsync(token);
                return ReferenceEquals(next, state) ? "Unchanged" : "Changed";
            }, probe, token);
        }

        private async Task<string> UnderLockAsync(
            Func<NpgsqlConnection, NpgsqlTransaction, MapAttractionStatePrototype, Task<string>> action,
            Probe? probe, CancellationToken token)
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync(token);
            await using var transaction = await database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
            probe?.Opened.TrySetResult(database.ProcessID);
            await using var locked = Command(database, transaction,
                "SELECT is_active, version FROM m3_state_prototype.states WHERE attraction_id = $1 FOR UPDATE",
                _attraction.AttractionId);
            MapAttractionStatePrototype state;
            await using (var reader = await locked.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) return "Denied";
                state = MapAttractionStatePrototype.CreateSnapshot(_attraction, reader.GetBoolean(0), reader.GetInt64(1));
            }
            probe?.Locked.TrySetResult(database.ProcessID);
            if (probe is not null) await probe.Release.Task.WaitAsync(token);
            var result = await action(database, transaction, state);
            if (probe?.BeforeCommit is not null) await probe.BeforeCommit(database, transaction, token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token);
            if (probe?.LoseAcknowledgement == true) throw new NpgsqlException("Synthetic acknowledgement loss after commit.");
            return result;
        }

        public async Task AssertBlockedAsync(int holder, Probe waiting, Task<string> operation, CancellationToken token)
        {
            var contender = await waiting.Opened.Task.WaitAsync(token);
            Assert.NotEqual(holder, contender);
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync(token);
            await using var blocked = new NpgsqlCommand("SELECT $1 = ANY(pg_blocking_pids($2))", database);
            blocked.Parameters.AddWithValue(holder);
            blocked.Parameters.AddWithValue(contender);
            while (await blocked.ExecuteScalarAsync(token) is not true)
            {
                Assert.False(operation.IsCompleted);
                await Task.Delay(20, token);
            }
            Assert.False(operation.IsCompleted);
        }

        public async Task AssertStoredAsync(bool active, long version, int receipts, int requests)
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT is_active, version,
                    (SELECT count(*) FROM m3_state_prototype.receipts),
                    (SELECT count(*) FROM m3_state_prototype.requests)
                FROM m3_state_prototype.states
                """, database);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(active, reader.GetBoolean(0));
            Assert.Equal(version, reader.GetInt64(1));
            Assert.Equal(receipts, reader.GetInt64(2));
            Assert.Equal(requests, reader.GetInt64(3));
            await using var context = Context();
            Assert.Empty(await context.MapContributions.ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        }

        private HobbiesMapDbContext Context() => new(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options);

        private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string sql, params object[] parameters)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in parameters) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return command;
        }
    }
}
