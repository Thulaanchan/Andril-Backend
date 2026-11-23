using System.Data;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

// EXPERIMENTAL test-owned schema, outcomes, receipt scope and transaction protocols.
// Not a production repository, migration, Admin authorization or reward implementation.
public sealed class MapContributionModerationPersistencePrototypeTests
{
    private enum Strategy { ConditionalUpdate, RowLock }
    private enum Fault { None, AfterState, AfterReceipt, AuditConstraint, LostAcknowledgement }
    private enum Outcome { Applied, Replay, Conflict, Denied }
    private sealed record Result(Outcome Outcome, MapContributionModerationSnapshot? Snapshot = null);
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExperimentalTypesAreAbsentFromTheProductionEfModel()
    {
        using var context = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Username=unused", HobbiesDatabaseOptions.ConfigureMapHistory).Options);
        Assert.Null(context.Model.FindEntityType(typeof(MapContributionModerationSnapshot)));
        Assert.Null(context.Model.FindEntityType(typeof(MapContributionReviewDecision)));
        Assert.Single(Enum.GetValues<MapContributionModerationStatus>());
    }

    [MapCapturePostgresFact]
    public Task ConcurrentApproveApproveCommitsOneDecision() => RaceAsync(MapContributionReviewAction.Approve);

    [MapCapturePostgresFact]
    public Task ConcurrentApproveRejectCommitsOneDecision() => RaceAsync(MapContributionReviewAction.Reject);

    private static Task RaceAsync(MapContributionReviewAction secondAction) => EachStrategy(async (h, strategy) =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var firstProbe = new Probe(pause: true);
        var secondProbe = new Probe();
        var first = h.DecideAsync(h.Request(), Guid.NewGuid(), strategy, firstProbe, token: deadline.Token);
        Task<Result>? second = null;
        try
        {
            await firstProbe.Holding.Task.WaitAsync(deadline.Token);
            second = h.DecideAsync(h.Request(secondAction), Guid.NewGuid(), strategy, secondProbe, token: deadline.Token);
            await h.AssertBlockedAsync(firstProbe, secondProbe, second, deadline.Token);
            firstProbe.Release.TrySetResult();
            Assert.Equal(Outcome.Applied, (await first).Outcome);
            Assert.Equal(Outcome.Conflict, (await second).Outcome);
            await h.AssertStoredAsync(2, 2, 1);
        }
        finally
        {
            firstProbe.Release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
    });

    [MapCapturePostgresFact]
    public Task ConcurrentIdenticalRetryReturnsTheSameDurableReceipt() => EachStrategy(async (h, strategy) =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var request = h.Request();
        var key = Guid.NewGuid();
        var held = new Probe(pause: true);
        var contender = new Probe();
        var first = h.DecideAsync(request, key, strategy, held, token: deadline.Token);
        Task<Result>? second = null;
        try
        {
            await held.Holding.Task.WaitAsync(deadline.Token);
            second = h.DecideAsync(request, key, strategy, contender, token: deadline.Token);
            await h.AssertBlockedAsync(held, contender, second, deadline.Token);
            held.Release.TrySetResult();
            var accepted = await first;
            var replay = await second;
            Assert.Equal(Outcome.Applied, accepted.Outcome);
            Assert.Equal(Outcome.Replay, replay.Outcome);
            Assert.Equal(accepted.Snapshot!.Decision!.Request, replay.Snapshot!.Decision!.Request);
            Assert.Equal(accepted.Snapshot.Decision.DecidedAtUtc, replay.Snapshot.Decision.DecidedAtUtc);
            await h.AssertStoredAsync(2, 2, 1);
        }
        finally
        {
            held.Release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
    });

    [MapCapturePostgresFact]
    public Task ChangedPayloadOrDecisionIdentityCannotReuseAnAcceptedKey() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        var key = Guid.NewGuid();
        Assert.Equal(Outcome.Applied, (await h.DecideAsync(request, key, strategy)).Outcome);
        foreach (var changed in new[]
        {
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, MapContributionReviewAction.Reject, 1, request.ReviewerSubject),
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, request.Action, 2, request.ReviewerSubject),
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, request.Action, 1, "other-trusted-reviewer"),
            h.Request()
        })
            Assert.Equal(Outcome.Conflict, (await h.DecideAsync(changed, key, strategy)).Outcome);
        Assert.Equal(Outcome.Replay, (await h.DecideAsync(request, key, strategy)).Outcome);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task StaleAndFutureRevisionsLeavePendingStateUnchanged() => EachStrategy(async (h, strategy) =>
    {
        foreach (var revision in new long[] { 0, 2 })
            Assert.Equal(Outcome.Conflict, (await h.DecideAsync(h.Request(revision: revision), Guid.NewGuid(), strategy)).Outcome);
        await h.AssertStoredAsync(1, 1, 0);
    });

    [MapCapturePostgresFact]
    public Task FailuresAfterStateReceiptOrAuditWorkRollBackTheWholeDecision() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        var key = Guid.NewGuid();
        foreach (var fault in new[] { Fault.AfterState, Fault.AfterReceipt, Fault.AuditConstraint })
        {
            var error = await Record.ExceptionAsync(() => h.DecideAsync(request, key, strategy, fault: fault));
            Assert.NotNull(error);
            if (fault == Fault.AuditConstraint)
            {
                var pg = Assert.IsType<PostgresException>(error);
                Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
                Assert.Equal("ck_audit_reviewer", pg.ConstraintName);
            }
            else Assert.IsType<IOException>(error);
            await h.AssertStoredAsync(1, 1, 0);
        }
        Assert.Equal(Outcome.Applied, (await h.DecideAsync(request, key, strategy)).Outcome);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task FailedFirstDecisionReleasesTheBlockedValidReviewer() => EachStrategy(async (h, strategy) =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var held = new Probe(pause: true);
        var contender = new Probe();
        var first = h.DecideAsync(h.Request(), Guid.NewGuid(), strategy, held, Fault.AfterReceipt, token: deadline.Token);
        Task<Result>? second = null;
        try
        {
            await held.Holding.Task.WaitAsync(deadline.Token);
            second = h.DecideAsync(h.Request(MapContributionReviewAction.Reject), Guid.NewGuid(), strategy, contender, token: deadline.Token);
            await h.AssertBlockedAsync(held, contender, second, deadline.Token);
            held.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => first);
            Assert.Equal(Outcome.Applied, (await second).Outcome);
            await h.AssertStoredAsync(3, 2, 1);
        }
        finally
        {
            held.Release.TrySetResult();
            await Observe(first);
            if (second is not null) await Observe(second);
        }
    });

    [MapCapturePostgresFact]
    public Task LostCommitAcknowledgementReconcilesOnAFreshConnection() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        var key = Guid.NewGuid();
        // A real commit followed by an injected acknowledgement failure, not a network fault proof.
        await Assert.ThrowsAsync<IOException>(() => h.DecideAsync(request, key, strategy, fault: Fault.LostAcknowledgement));
        var restored = await h.RestoreAsync();
        Assert.Same(restored.Snapshot, restored.Decide(request, AcceptedAt.AddHours(1)));
        Assert.Equal(Outcome.Replay, (await h.DecideAsync(request, key, strategy)).Outcome);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task ForeignContributionAndScopeAreDeniedWithoutReceipts() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        var scopes = new[]
        {
            h.Scope with { Owner = Guid.NewGuid() }, h.Scope with { Attraction = Guid.NewGuid() },
            h.Scope with { Session = Guid.NewGuid() }, h.Scope with { Module = "learning" },
            h.Scope with { Module = "social" }, h.Scope with { Module = "gallery" }
        };
        foreach (var scope in scopes)
            Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(request, Guid.NewGuid(), strategy, scope: scope));
        var foreign = new MapContributionReviewRequest(request.DecisionId, Guid.NewGuid(), request.Action, 1, request.ReviewerSubject);
        Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(foreign, Guid.NewGuid(), strategy));
        await h.AssertStoredAsync(1, 1, 0);
        var key = Guid.NewGuid();
        await h.DecideAsync(request, key, strategy);
        Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(request, key, strategy, scope: scopes[0]));
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task CancellationDuringRealLockWaitWritesNoPartialDecision() => EachStrategy(async (h, strategy) =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var held = new Probe(pause: true);
        var contender = new Probe();
        var first = h.DecideAsync(h.Request(), Guid.NewGuid(), strategy, held, token: deadline.Token);
        Task<Result>? second = null;
        try
        {
            await held.Holding.Task.WaitAsync(deadline.Token);
            second = h.DecideAsync(h.Request(MapContributionReviewAction.Reject), Guid.NewGuid(), strategy, contender, token: cancel.Token);
            await h.AssertBlockedAsync(held, contender, second, deadline.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            held.Release.TrySetResult();
            Assert.Equal(Outcome.Applied, (await first).Outcome);
            await h.AssertStoredAsync(2, 2, 1);
        }
        finally
        {
            held.Release.TrySetResult();
            await Observe(first);
            if (second is not null) await Observe(second);
        }
    });

    [MapCapturePostgresFact]
    public Task FreshTestContextRestoresBothAcceptedActionsWithoutPrivateMedia() => EachStrategy(async (h, strategy) =>
    {
        // Both actions are tested across the two independent fixture databases.
        var action = strategy == Strategy.RowLock ? MapContributionReviewAction.Reject : MapContributionReviewAction.Approve;
        var request = h.Request(action);
        await h.DecideAsync(request, Guid.NewGuid(), strategy);
        var restored = await h.RestoreAsync();
        Assert.Equal(request, restored.Snapshot.Decision!.Request);
        Assert.Equal(AcceptedAt, restored.Snapshot.Decision.DecidedAtUtc);
        Assert.Same(restored.Snapshot, restored.Decide(request, AcceptedAt.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => restored.Decide(h.Request(action, 2), AcceptedAt));
        var properties = typeof(MapContributionModerationSnapshot).GetProperties(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(properties, p => p.Name.Contains("Photo") || p.Name.Contains("Location") || p.Name.Contains("Points"));
        await h.AssertStoredAsync(action == MapContributionReviewAction.Approve ? 2 : 3, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task NamedUniqueAndScopeConstraintsRejectUnrelatedDuplicateShapes() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        await h.DecideAsync(request, Guid.NewGuid(), strategy);
        // Each probe isolates its immediate constraint; deferred references are never committed.
        await h.AssertReceiptErrorAsync("decision_id", "pk_receipts", PostgresErrorCodes.UniqueViolation);
        await h.AssertReceiptErrorAsync("idempotency_key", "ux_receipt_key", PostgresErrorCodes.UniqueViolation);
        await h.AssertReceiptErrorAsync("expected_revision", "ux_receipt_revision", PostgresErrorCodes.UniqueViolation);
        await h.AssertReceiptErrorAsync("scope", "fk_receipt_scope", PostgresErrorCodes.ForeignKeyViolation);
        await h.AssertReceiptErrorAsync("empty_decision", "ck_receipt_decision", PostgresErrorCodes.CheckViolation);
        foreach (var sql in new[]
        {
            "UPDATE m3_moderation_prototype.contributions SET revision=0",
            "UPDATE m3_moderation_prototype.contributions SET status=99"
        })
        {
            await using var database = await h.OpenAsync();
            await using var command = new NpgsqlCommand(sql, database);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task AcceptedReceiptAndAuditRejectOrdinaryUpdateAndDelete() => EachStrategy(async (h, strategy) =>
    {
        await h.DecideAsync(h.Request(), Guid.NewGuid(), strategy);
        foreach (var table in new[] { "receipts", "audit" })
        foreach (var verb in new[] { "UPDATE", "DELETE" })
        {
            await using var database = await h.OpenAsync();
            var sql = verb == "UPDATE" ? $"UPDATE m3_moderation_prototype.{table} SET reviewer = reviewer" : $"DELETE FROM m3_moderation_prototype.{table}";
            var error = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(sql, database).ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task DeferredReferencesPreventCommittingStatusWithoutReceiptOrReceiptWithoutAudit() => EachStrategy(async (h, _) =>
    {
        await using var database = await h.OpenAsync();
        await using var transaction = await database.BeginTransactionAsync();
        await using var update = new NpgsqlCommand("UPDATE m3_moderation_prototype.contributions SET status=2, revision=2, decision_id=$1", database, transaction);
        update.Parameters.AddWithValue(Guid.NewGuid());
        await update.ExecuteNonQueryAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_state_receipt", error.ConstraintName);
        await h.AssertStoredAsync(1, 1, 0);
        await h.AssertMissingAuditCommitFailsAsync();
        await h.AssertStoredAsync(1, 1, 0);
    });

    private static async Task Observe(Task task) { try { await task; } catch { /* Cleanup only; assertions observe expected outcomes above. */ } }
    private static async Task EachStrategy(Func<Harness, Strategy, Task> test)
    {
        foreach (var strategy in Enum.GetValues<Strategy>())
            await WithDatabase(async connection =>
            {
                var harness = new Harness(connection);
                await harness.InitializeAsync();
                await test(harness, strategy);
            });
    }

    private sealed class Probe(bool pause = false)
    {
        internal bool Pause { get; } = pause;
        internal TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Scope(Guid Owner, Guid Attraction, Guid Session, string Module = "hobbies");
    private sealed class PrototypeContext(DbContextOptions<PrototypeContext> options) : DbContext(options);

    private sealed class Harness(string connection)
    {
        private readonly MapContribution _contribution = MapContribution.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "synthetic/private/photo.jpg", "Synthetic", new MapContributionLocationEvidence(7, 80, 7, 80, 5, AcceptedAt.AddSeconds(-30), AcceptedAt.AddSeconds(-5)), AcceptedAt);
        internal Scope Scope => new(_contribution.OwnerHobbiesModuleProfileId, _contribution.AttractionId, _contribution.CaptureSessionId);
        internal MapContributionReviewRequest Request(MapContributionReviewAction action = MapContributionReviewAction.Approve, long revision = 1) =>
            new(Guid.NewGuid(), _contribution.ContributionId, action, revision, "synthetic-trusted-reviewer");
        internal async Task<NpgsqlConnection> OpenAsync(CancellationToken token = default)
        {
            var database = new NpgsqlConnection(connection);
            try { await database.OpenAsync(token); return database; }
            catch { await database.DisposeAsync(); throw; }
        }
        private static NpgsqlCommand Command(NpgsqlConnection database, NpgsqlTransaction? transaction, string sql, params object[] values)
        {
            var command = new NpgsqlCommand(sql, database, transaction) { CommandTimeout = 20 };
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return command;
        }
        internal async Task InitializeAsync()
        {
            await using var database = await OpenAsync();
            await using var ddl = new NpgsqlCommand(Schema, database);
            await ddl.ExecuteNonQueryAsync();
            await using var seed = Command(database, null, "INSERT INTO m3_moderation_prototype.contributions VALUES ($1,$2,$3,$4,'hobbies',1,1,NULL)",
                _contribution.ContributionId, Scope.Owner, Scope.Attraction, Scope.Session);
            await seed.ExecuteNonQueryAsync();
        }

        internal async Task<Result> DecideAsync(MapContributionReviewRequest request, Guid key, Strategy strategy,
            Probe? probe = null, Fault fault = Fault.None, Scope? scope = null, CancellationToken token = default)
        {
            scope ??= Scope; // Synthetic trusted authorization fixture, never a production permission check.
            await using var database = await OpenAsync(token);
            await using var transaction = await database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            probe?.Started.TrySetResult(database.ProcessID);
            await using var select = Command(database, transaction, """
                SELECT status, revision FROM m3_moderation_prototype.contributions
                WHERE contribution_id=$1 AND owner_id=$2 AND attraction_id=$3 AND session_id=$4 AND module=$5
                """ + (strategy == Strategy.RowLock ? " FOR UPDATE" : ""),
                request.ContributionId, scope.Owner, scope.Attraction, scope.Session, scope.Module);
            int status; long revision;
            await using (var reader = await select.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) return new(Outcome.Denied);
                status = reader.GetInt32(0); revision = reader.GetInt64(1);
            }
            var replay = await ReplayAsync(database, transaction, request, key, token);
            if (replay is not null) return replay;
            if (status != 1 || revision != request.ExpectedRevision) return new(Outcome.Conflict);
            var domain = MapContributionModeration.Restore(_contribution, Snapshot(revision, null), null);
            var accepted = domain.Decide(request, AcceptedAt);
            var resultStatus = request.Action == MapContributionReviewAction.Approve ? 2 : 3;
            await using var update = Command(database, transaction, """
                UPDATE m3_moderation_prototype.contributions SET status=$1,revision=$2,decision_id=$3
                WHERE contribution_id=$4 AND status=1 AND revision=$5
                  AND owner_id=$6 AND attraction_id=$7 AND session_id=$8 AND module=$9
                """, resultStatus, accepted.Revision, request.DecisionId, request.ContributionId, request.ExpectedRevision,
                scope.Owner, scope.Attraction, scope.Session, scope.Module);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                return await ReplayAsync(database, transaction, request, key, token) ?? new(Outcome.Conflict);
            if (probe is not null)
            {
                probe.Holding.TrySetResult();
                if (probe.Pause) await probe.Release.Task.WaitAsync(token);
            }
            if (fault == Fault.AfterState) throw new IOException("Synthetic failure after state update.");
            var auditId = Guid.NewGuid();
            await InsertReceiptAsync(database, transaction, request, key, auditId, resultStatus, token);
            if (fault == Fault.AfterReceipt) throw new IOException("Synthetic failure after receipt insertion.");
            await using var audit = Command(database, transaction,
                "INSERT INTO m3_moderation_prototype.audit VALUES ($1,$2,$3,$4,$5)",
                auditId, request.DecisionId, request.ContributionId, fault == Fault.AuditConstraint ? "" : request.ReviewerSubject, AcceptedAt);
            await audit.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
            if (fault == Fault.LostAcknowledgement) throw new IOException("Synthetic lost acknowledgement after real commit.");
            return new(Outcome.Applied, accepted);
        }

        private async Task InsertReceiptAsync(NpgsqlConnection database, NpgsqlTransaction transaction,
            MapContributionReviewRequest request, Guid key, Guid auditId, int status, CancellationToken token = default)
        {
            await using var insert = Command(database, transaction,
                "INSERT INTO m3_moderation_prototype.receipts VALUES ($1,$2,$3,$4,$5,$6,'hobbies',$7,$8,$9,$10,$11,$12,$13,$14)",
                request.DecisionId, key, request.ContributionId, Scope.Owner, Scope.Attraction, Scope.Session,
                (int)request.Action, request.ExpectedRevision, checked(request.ExpectedRevision + 1), status,
                request.ReviewerSubject, AcceptedAt, auditId, 1);
            await insert.ExecuteNonQueryAsync(token);
        }

        private async Task<Result?> ReplayAsync(NpgsqlConnection database, NpgsqlTransaction transaction,
            MapContributionReviewRequest request, Guid key, CancellationToken token)
        {
            await using var query = Command(database, transaction, """
                SELECT decision_id,action,expected_revision,result_revision,reviewer,accepted_at,result_status
                FROM m3_moderation_prototype.receipts WHERE contribution_id=$1 AND idempotency_key=$2
                """, request.ContributionId, key);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            var saved = new MapContributionReviewRequest(reader.GetGuid(0), request.ContributionId,
                (MapContributionReviewAction)reader.GetInt32(1), reader.GetInt64(2), reader.GetString(4));
            if (saved != request) return new(Outcome.Conflict);
            var evidence = new MapContributionReviewDecision(saved, reader.GetFieldValue<DateTimeOffset>(5));
            return new(Outcome.Replay, MapContributionModeration.Restore(_contribution,
                Snapshot(reader.GetInt64(3), evidence), saved.Action).Snapshot);
        }

        private MapContributionModerationSnapshot Snapshot(long revision, MapContributionReviewDecision? decision) =>
            new(_contribution.ContributionId, Scope.Owner, Scope.Attraction, Scope.Session, revision, decision);

        internal async Task<MapContributionModeration> RestoreAsync()
        {
            await using var context = new PrototypeContext(new DbContextOptionsBuilder<PrototypeContext>().UseNpgsql(connection).Options);
            await context.Database.OpenConnectionAsync();
            var database = (NpgsqlConnection)context.Database.GetDbConnection();
            await using var query = Command(database, null, """
                SELECT r.decision_id,r.action,r.expected_revision,r.result_revision,r.reviewer,r.accepted_at
                FROM m3_moderation_prototype.receipts r
                JOIN m3_moderation_prototype.contributions c ON c.decision_id=r.decision_id AND c.revision=r.result_revision
                JOIN m3_moderation_prototype.audit a ON a.audit_id=r.audit_id AND a.decision_id=r.decision_id
                WHERE c.contribution_id=$1 AND c.owner_id=$2 AND c.attraction_id=$3 AND c.session_id=$4 AND c.module='hobbies'
                """, _contribution.ContributionId, Scope.Owner, Scope.Attraction, Scope.Session);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var request = new MapContributionReviewRequest(reader.GetGuid(0), _contribution.ContributionId,
                (MapContributionReviewAction)reader.GetInt32(1), reader.GetInt64(2), reader.GetString(4));
            return MapContributionModeration.Restore(_contribution, Snapshot(reader.GetInt64(3),
                new(request, reader.GetFieldValue<DateTimeOffset>(5))), request.Action);
        }

        internal async Task AssertBlockedAsync(Probe first, Probe second, Task operation, CancellationToken token)
        {
            var firstPid = await first.Started.Task.WaitAsync(token);
            var secondPid = await second.Started.Task.WaitAsync(token);
            Assert.NotEqual(firstPid, secondPid);
            await using var observer = await OpenAsync(token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Assert.False(operation.IsCompleted, "Contender completed before PostgreSQL blocking was observed.");
                await using var check = Command(observer, null, "SELECT $1 = ANY(pg_blocking_pids($2))", firstPid, secondPid);
                if (await check.ExecuteScalarAsync(token) is true) return;
                await Task.Delay(20, token); // Polls an authoritative lock condition, not a timing assumption.
            }
        }

        internal async Task AssertStoredAsync(int status, long revision, long receipts)
        {
            await using var database = await OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT status,revision,owner_id,attraction_id,session_id,module,
                    (SELECT count(*) FROM m3_moderation_prototype.receipts),
                    (SELECT count(*) FROM m3_moderation_prototype.audit),
                    to_regnamespace('andril_hobbies') IS NULL AND to_regnamespace('andril_identity') IS NULL
                FROM m3_moderation_prototype.contributions
                """, database);
            await using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(status, reader.GetInt32(0)); Assert.Equal(revision, reader.GetInt64(1));
            Assert.Equal(Scope.Owner, reader.GetGuid(2)); Assert.Equal(Scope.Attraction, reader.GetGuid(3));
            Assert.Equal(Scope.Session, reader.GetGuid(4)); Assert.Equal("hobbies", reader.GetString(5));
            Assert.Equal(receipts, reader.GetInt64(6)); Assert.Equal(receipts, reader.GetInt64(7));
            Assert.True(reader.GetBoolean(8)); // No production schema, ledger, rewards or publication are created.
        }

        internal async Task AssertReceiptErrorAsync(string duplicate, string constraint, string sqlState)
        {
            await using var database = await OpenAsync();
            await using var transaction = await database.BeginTransactionAsync();
            // Identifiers below are fixed test-owned expressions, never caller-supplied SQL.
            var id = duplicate == "decision_id" ? "decision_id" : duplicate == "empty_decision"
                ? "'00000000-0000-0000-0000-000000000000'::uuid" : "gen_random_uuid()";
            var key = duplicate == "idempotency_key" ? "idempotency_key" : "gen_random_uuid()";
            var expected = duplicate == "expected_revision" ? "expected_revision" : "expected_revision+1";
            var owner = duplicate == "scope" ? "gen_random_uuid()" : "owner_id";
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO m3_moderation_prototype.receipts
                SELECT {id},{key},contribution_id,{owner},attraction_id,session_id,module,action,
                    {expected},{expected}+1,result_status,reviewer,accepted_at,gen_random_uuid(),1
                FROM m3_moderation_prototype.receipts
                """, database, transaction);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(sqlState, error.SqlState); Assert.Equal(constraint, error.ConstraintName);
        }

        internal async Task AssertMissingAuditCommitFailsAsync()
        {
            await using var database = await OpenAsync();
            await using var transaction = await database.BeginTransactionAsync();
            await InsertReceiptAsync(database, transaction, Request(), Guid.NewGuid(), Guid.NewGuid(), 2);
            var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            Assert.Equal("fk_receipt_audit", error.ConstraintName);
        }
    }

    private const string Schema = """
        CREATE SCHEMA m3_moderation_prototype;
        CREATE TABLE m3_moderation_prototype.contributions (
            contribution_id uuid PRIMARY KEY CHECK (contribution_id <> '00000000-0000-0000-0000-000000000000'),
            owner_id uuid NOT NULL CHECK (owner_id <> '00000000-0000-0000-0000-000000000000'),
            attraction_id uuid NOT NULL CHECK (attraction_id <> '00000000-0000-0000-0000-000000000000'),
            session_id uuid NOT NULL CHECK (session_id <> '00000000-0000-0000-0000-000000000000'),
            module text NOT NULL CHECK (module='hobbies'), status integer NOT NULL CHECK (status IN (1,2,3)),
            revision bigint NOT NULL CHECK (revision>0), decision_id uuid,
            UNIQUE(contribution_id,owner_id,attraction_id,session_id,module),
            CHECK ((status=1 AND decision_id IS NULL) OR (status IN (2,3) AND decision_id IS NOT NULL)));
        CREATE TABLE m3_moderation_prototype.receipts (
            decision_id uuid CONSTRAINT pk_receipts PRIMARY KEY
                CONSTRAINT ck_receipt_decision CHECK (decision_id <> '00000000-0000-0000-0000-000000000000'),
            idempotency_key uuid NOT NULL CHECK (idempotency_key <> '00000000-0000-0000-0000-000000000000'),
            contribution_id uuid NOT NULL, owner_id uuid NOT NULL, attraction_id uuid NOT NULL, session_id uuid NOT NULL,
            module text NOT NULL CHECK (module='hobbies'), action integer NOT NULL CHECK (action IN (1,2)),
            expected_revision bigint NOT NULL CHECK (expected_revision>0),
            result_revision bigint NOT NULL CHECK (result_revision=expected_revision+1),
            result_status integer NOT NULL CHECK (result_status=CASE action WHEN 1 THEN 2 ELSE 3 END),
            reviewer text NOT NULL CHECK (length(btrim(reviewer))>0), accepted_at timestamptz NOT NULL,
            audit_id uuid NOT NULL, previous_status integer NOT NULL CHECK (previous_status=1),
            CONSTRAINT ux_receipt_key UNIQUE(contribution_id,idempotency_key),
            CONSTRAINT ux_receipt_revision UNIQUE(contribution_id,expected_revision),
            UNIQUE(decision_id,contribution_id,result_status,result_revision),
            CONSTRAINT fk_receipt_scope FOREIGN KEY(contribution_id,owner_id,attraction_id,session_id,module)
                REFERENCES m3_moderation_prototype.contributions(contribution_id,owner_id,attraction_id,session_id,module));
        CREATE TABLE m3_moderation_prototype.audit (
            audit_id uuid PRIMARY KEY, decision_id uuid NOT NULL UNIQUE REFERENCES m3_moderation_prototype.receipts(decision_id),
            contribution_id uuid NOT NULL REFERENCES m3_moderation_prototype.contributions(contribution_id),
            reviewer text NOT NULL CONSTRAINT ck_audit_reviewer CHECK (length(btrim(reviewer))>0),
            accepted_at timestamptz NOT NULL, UNIQUE(audit_id,decision_id));
        ALTER TABLE m3_moderation_prototype.contributions ADD CONSTRAINT fk_state_receipt
            FOREIGN KEY(decision_id,contribution_id,status,revision)
            REFERENCES m3_moderation_prototype.receipts(decision_id,contribution_id,result_status,result_revision)
            DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE m3_moderation_prototype.receipts ADD CONSTRAINT fk_receipt_audit
            FOREIGN KEY(audit_id,decision_id) REFERENCES m3_moderation_prototype.audit(audit_id,decision_id)
            DEFERRABLE INITIALLY DEFERRED;
        CREATE FUNCTION m3_moderation_prototype.immutable_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Experimental accepted evidence is immutable' USING ERRCODE='55000'; END $$;
        CREATE TRIGGER immutable_receipt BEFORE UPDATE OR DELETE ON m3_moderation_prototype.receipts
            FOR EACH ROW EXECUTE FUNCTION m3_moderation_prototype.immutable_evidence();
        CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE ON m3_moderation_prototype.audit
            FOR EACH ROW EXECUTE FUNCTION m3_moderation_prototype.immutable_evidence();
        """;

    // Same loopback/maintenance guard as the existing M3 fixtures, but no EnsureCreated/Migrate:
    // only the experimental schema is created. No credentials are logged or stored in this file.
    private static async Task WithDatabase(Func<string, Task> test)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ANDRIL_M3_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Disposable PostgreSQL is not configured."));
        if (settings.Host is not ("127.0.0.1" or "localhost" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Only a loopback maintenance database is permitted.");
        if (settings.Host == "localhost") settings.Host = "127.0.0.1";
        settings.Pooling = false; settings.Enlist = false; settings.IncludeErrorDetail = false; settings.SearchPath = "public";
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand("SELECT inet_server_addr() IN ('127.0.0.1'::inet,'::1'::inet)", admin))
            if (await verify.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Non-loopback server denied.");
        var name = $"andril_m3_moderation_prototype_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", admin))
            await create.ExecuteNonQueryAsync();
        try { settings.Database = name; await test(settings.ConnectionString); }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
