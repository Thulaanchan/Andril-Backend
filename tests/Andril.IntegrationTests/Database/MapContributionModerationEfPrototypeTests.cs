using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Andril.IntegrationTests.Database;

// EXPERIMENTAL EF compatibility proof over the unchanged raw-Npgsql prototype DDL.
// No migrations/EnsureCreated, production entity registration, permissions, media or rewards.
public sealed class MapContributionModerationEfPrototypeTests(ITestOutputHelper output)
{
    private enum Strategy { ConditionalRevision, RowLock }
    private enum Fault { None, ReceiptConstraint, AuditConstraint, AfterReceipt, LostAcknowledgement }
    private enum Outcome { Applied, Replay, Conflict, Denied }
    private sealed record Result(Outcome Outcome, MapContributionModerationSnapshot? Snapshot = null);
    private sealed record Scope(Guid Owner, Guid Attraction, Guid Session, string Module = "hobbies");
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 23, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TestMappingsPreserveConcurrencyAndRelationshipsWithoutProductionActivation()
    {
        using var context = Context("Host=127.0.0.1;Database=model_only;Username=unused", new());
        Assert.Equal(3, context.Model.GetEntityTypes().Count());
        Assert.False(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.True(context.Model.FindEntityType(typeof(StateRow))!.FindProperty(nameof(StateRow.Revision))!.IsConcurrencyToken);
        Assert.Contains(context.Model.FindEntityType(typeof(ReceiptRow))!.GetIndexes(),
            index => index.IsUnique && index.GetDatabaseName() == "ux_receipt_key");
        Assert.Equal(2, context.Model.FindEntityType(typeof(ReceiptRow))!.GetForeignKeys().Count());
        Assert.Equal(2, context.Model.FindEntityType(typeof(AuditRow))!.GetForeignKeys().Count());
        var script = context.Database.GenerateCreateScript(); // Inspected, never applied.
        Assert.DoesNotContain("DEFERRABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TRIGGER", script, StringComparison.OrdinalIgnoreCase);
        using var production = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=model_only;Username=unused", HobbiesDatabaseOptions.ConfigureMapHistory).Options);
        Assert.Null(production.Model.FindEntityType(typeof(StateRow)));
        Assert.Null(production.Model.FindEntityType(typeof(ReceiptRow)));
        Assert.Null(production.Model.FindEntityType(typeof(AuditRow)));
        Assert.Single(Enum.GetValues<MapContributionModerationStatus>());
    }

    [MapCapturePostgresFact]
    public Task EfCommandsParameterizeScopeAndGuardTheOriginalRevision() => EachStrategy(async (h, strategy) =>
    {
        h.Trace.Commands.Clear();
        Assert.Equal(Outcome.Applied, (await h.DecideAsync(h.Request(), Guid.NewGuid(), strategy)).Outcome);
        var commands = h.Trace.Commands.ToArray();
        Assert.All(commands, command => Assert.True(command.InTransaction));
        var update = Assert.Single(commands, command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal));
        var normalized = update.Sql.Replace("\"", "");
        Assert.Matches(@"WHERE contribution_id = @\w+.*AND revision = @\w+", normalized.Replace('\n', ' ').Replace('\r', ' '));
        foreach (var column in new[] { "owner_id", "attraction_id", "session_id", "module" })
            Assert.Matches($@"AND {column} = @\w+", normalized);
        Assert.True(update.Parameters.Length >= 5);
        Assert.Contains(commands, command => command.Sql.Contains("INSERT INTO m3_moderation_prototype.receipts"));
        Assert.Contains(commands, command => command.Sql.Contains("INSERT INTO m3_moderation_prototype.audit"));
        var scoped = Assert.Single(commands, command => command.Sql.Contains("FROM m3_moderation_prototype.contributions") && command.Sql.Contains("owner_id") &&
            command.Sql.Contains("attraction_id") && command.Sql.Contains("session_id") &&
            command.Sql.Contains("module") && command.Sql.Contains("WHERE") && command.Parameters.Length >= 5);
        Assert.Contains("@", scoped.Sql);
        if (strategy == Strategy.RowLock) Assert.Contains("FOR UPDATE", scoped.Sql);
        foreach (var command in commands)
        {
            Assert.DoesNotContain(h.Contribution.OwnerHobbiesModuleProfileId.ToString(), command.Sql);
            Assert.DoesNotContain("synthetic-trusted-reviewer", command.Sql);
            Assert.DoesNotContain(h.Contribution.PrivatePhotoStorageObjectKey, command.Sql);
            output.WriteLine(command.Sql);
            output.WriteLine(string.Join(", ", command.Parameters)); // Names/types only; never parameter values.
        }
    });

    [MapCapturePostgresFact]
    public async Task BothAcceptedActionsRoundTripThroughFreshEfContexts()
    {
        foreach (var strategy in Enum.GetValues<Strategy>())
        foreach (var action in new[] { MapContributionReviewAction.Approve, MapContributionReviewAction.Reject })
            await WithHarness(async h =>
            {
                var request = h.Request(action);
                var key = Guid.NewGuid();
                var accepted = await h.DecideAsync(request, key, strategy);
                Assert.Equal(Outcome.Applied, accepted.Outcome);
                var restored = await h.RestoreAsync();
                Assert.Equal(request, restored.Snapshot.Decision!.Request);
                Assert.Equal(AcceptedAt, restored.Snapshot.Decision.DecidedAtUtc);
                Assert.Same(restored.Snapshot, restored.Decide(request, AcceptedAt.AddHours(1)));
                Assert.Equal(Outcome.Replay, (await h.DecideAsync(request, key, strategy)).Outcome);
                await h.AssertStoredAsync(action == MapContributionReviewAction.Approve ? 2 : 3, 2, 1);
            });
    }

    [MapCapturePostgresFact]
    public Task FailedReceiptAndAuditSaveChangesRollBackEverything() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request();
        var key = Guid.NewGuid();
        foreach (var fault in new[] { Fault.ReceiptConstraint, Fault.AuditConstraint })
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => h.DecideAsync(request, key, strategy, fault: fault));
            var pg = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
            if (fault == Fault.AuditConstraint) Assert.Equal("ck_audit_reviewer", pg.ConstraintName);
            Assert.DoesNotContain(h.Contribution.PrivatePhotoStorageObjectKey, error.ToString());
            Assert.DoesNotContain(request.ReviewerSubject, error.ToString());
            Assert.DoesNotContain(h.Scope.Owner.ToString(), error.ToString());
            await h.AssertStoredAsync(1, 1, 0);
        }
        Assert.Equal(Outcome.Applied, (await h.DecideAsync(request, key, strategy)).Outcome);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task ConcurrentApproveApproveHasOneEfWinner() => Race(MapContributionReviewAction.Approve);

    [MapCapturePostgresFact]
    public Task ConcurrentApproveRejectHasOneEfWinner() => Race(MapContributionReviewAction.Reject);

    [MapCapturePostgresFact]
    public Task IdenticalConcurrentEfRetryReconcilesAfterTheRealLockWait() => Race(MapContributionReviewAction.Approve, identical: true);

    [MapCapturePostgresFact]
    public Task FailedEfWriterReleasesTheBlockedValidContender() => Race(MapContributionReviewAction.Reject, rollback: true);

    [MapCapturePostgresFact]
    public Task EfCancellationDuringPostgresLockWaitCannotCommit() => Race(MapContributionReviewAction.Reject, cancel: true);

    private static Task Race(MapContributionReviewAction secondAction, bool identical = false, bool rollback = false, bool cancel = false) =>
        EachStrategy(async (h, strategy) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var firstProbe = new Probe(pause: true);
            var secondProbe = new Probe();
            var request = h.Request();
            var key = Guid.NewGuid();
            var first = h.DecideAsync(request, key, strategy, firstProbe,
                rollback ? Fault.AfterReceipt : Fault.None, token: deadline.Token);
            Task<Result>? second = null;
            try
            {
                await firstProbe.Holding.Task.WaitAsync(deadline.Token);
                second = h.DecideAsync(identical ? request : h.Request(secondAction), identical ? key : Guid.NewGuid(),
                    strategy, secondProbe, token: secondCancellation.Token);
                await h.AssertBlockedAsync(firstProbe, secondProbe, second, deadline.Token);
                if (cancel)
                {
                    secondCancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
                }
                firstProbe.Release.TrySetResult();
                if (rollback) await Assert.ThrowsAsync<IOException>(() => first);
                else Assert.Equal(Outcome.Applied, (await first).Outcome);
                if (!cancel)
                {
                    var result = await second;
                    Assert.Equal(rollback ? Outcome.Applied : identical ? Outcome.Replay : Outcome.Conflict, result.Outcome);
                    if (identical) Assert.Equal(request, result.Snapshot!.Decision!.Request);
                }
                await h.AssertStoredAsync(rollback ? 3 : 2, 2, 1);
            }
            finally
            {
                firstProbe.Release.TrySetResult();
                await Observe(first);
                if (second is not null) await Observe(second);
            }
        });

    [MapCapturePostgresFact]
    public Task ChangedPayloadAndStaleOrFutureRevisionsDoNotCreateEfSideEffects() => EachStrategy(async (h, strategy) =>
    {
        foreach (var revision in new long[] { 0, 2 })
            Assert.Equal(Outcome.Conflict, (await h.DecideAsync(h.Request(revision: revision), Guid.NewGuid(), strategy)).Outcome);
        await h.AssertStoredAsync(1, 1, 0);
        var request = h.Request(); var key = Guid.NewGuid();
        await h.DecideAsync(request, key, strategy);
        foreach (var changed in new[]
        {
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, MapContributionReviewAction.Reject, 1, request.ReviewerSubject),
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, request.Action, 2, request.ReviewerSubject),
            new MapContributionReviewRequest(request.DecisionId, request.ContributionId, request.Action, 1, "other-trusted-reviewer"),
            h.Request(revision: 2)
        }) Assert.Equal(Outcome.Conflict, (await h.DecideAsync(changed, key, strategy)).Outcome);
        Assert.Equal(Outcome.Replay, (await h.DecideAsync(request, key, strategy)).Outcome);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task ScopedEfQueriesDenyForeignContributionsAndModulesEvenOnReplay() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request(); var key = Guid.NewGuid();
        var scopes = new[]
        {
            h.Scope with { Owner = Guid.NewGuid() }, h.Scope with { Attraction = Guid.NewGuid() },
            h.Scope with { Session = Guid.NewGuid() }, h.Scope with { Module = "learning" },
            h.Scope with { Module = "social" }, h.Scope with { Module = "gallery" }
        };
        foreach (var scope in scopes)
            Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(request, key, strategy, scope: scope));
        var foreign = new MapContributionReviewRequest(request.DecisionId, Guid.NewGuid(), request.Action, 1, request.ReviewerSubject);
        Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(foreign, key, strategy));
        await h.AssertStoredAsync(1, 1, 0);
        await h.DecideAsync(request, key, strategy);
        foreach (var scope in scopes)
            Assert.Equal(new Result(Outcome.Denied), await h.DecideAsync(request, key, strategy, scope: scope));
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task EfCommitAcknowledgementLossReconcilesWithoutAnotherDecision() => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request(); var key = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => h.DecideAsync(request, key, strategy, fault: Fault.LostAcknowledgement));
        Assert.Equal(Outcome.Replay, (await h.DecideAsync(request, key, strategy)).Outcome);
        Assert.Equal(request, (await h.RestoreAsync()).Snapshot.Decision!.Request);
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task EfCannotCommitStateWithoutReceiptOrReceiptWithoutAudit() => EachStrategy(async (h, _) =>
    {
        foreach (var includeReceipt in new[] { false, true })
        {
            await using (var context = h.NewContext())
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var state = await context.States.SingleAsync();
                var request = h.Request(); var auditId = Guid.NewGuid();
                state.Status = 2; state.Revision = 2; state.DecisionId = request.DecisionId;
                await context.SaveChangesAsync();
                if (includeReceipt)
                {
                    context.Receipts.Add(h.Receipt(request, Guid.NewGuid(), auditId));
                    await context.SaveChangesAsync();
                }
                var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
                Assert.Equal(includeReceipt ? "fk_receipt_audit" : "fk_state_receipt", error.ConstraintName);
            }
            await h.AssertStoredAsync(1, 1, 0);
        }
    });

    [MapCapturePostgresFact]
    public Task DatabaseTriggersRejectEfReceiptAndAuditMutation() => EachStrategy(async (h, strategy) =>
    {
        await h.DecideAsync(h.Request(), Guid.NewGuid(), strategy);
        foreach (var receipt in new[] { false, true })
        foreach (var delete in new[] { false, true })
        {
            await using var context = h.NewContext();
            if (receipt)
            {
                var row = await context.Receipts.SingleAsync();
                if (delete) context.Receipts.Remove(row); else row.Reviewer = "changed-reviewer";
            }
            else
            {
                var row = await context.Audits.SingleAsync();
                if (delete) context.Audits.Remove(row); else row.Reviewer = "changed-reviewer";
            }
            h.Trace.Commands.Clear();
            // Npgsql's non-retrying execution strategy wraps SQLSTATE 55000 as likely transient.
            // Assert the real chain and a single attempted write; never reinterpret it as success.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            var update = Assert.IsType<DbUpdateException>(error.InnerException);
            Assert.Equal("55000", Assert.IsType<PostgresException>(update.InnerException).SqlState);
            Assert.Single(h.Trace.Commands, command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal) ||
                command.Sql.StartsWith("DELETE", StringComparison.Ordinal));
        }
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task EfPropagatesNamedReceiptUniquenessAndPositiveRevisionFailures() => EachStrategy(async (h, strategy) =>
    {
        await h.DecideAsync(h.Request(), Guid.NewGuid(), strategy);
        await using (var context = h.NewContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var duplicate = await context.Receipts.AsNoTracking().SingleAsync();
            duplicate.Id = Guid.NewGuid(); duplicate.ExpectedRevision = 2; duplicate.ResultRevision = 3; duplicate.AuditId = Guid.NewGuid();
            context.Receipts.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var pg = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
            Assert.Equal("ux_receipt_key", pg.ConstraintName);
        }
        await using (var context = h.NewContext())
        {
            var state = await context.States.SingleAsync(); state.Revision = 0;
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await h.AssertStoredAsync(2, 2, 1);
    });

    [MapCapturePostgresFact]
    public Task CyclicEfGraphNeedsStagedSaveChangesDespiteDeferredDatabaseConstraints() => EachStrategy(async (h, _) =>
    {
        await using (var context = h.NewContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var state = await context.States.SingleAsync();
            var request = h.Request(); var auditId = Guid.NewGuid();
            state.Status = 2; state.Revision = 2; state.DecisionId = request.DecisionId;
            context.Receipts.Add(h.Receipt(request, Guid.NewGuid(), auditId));
            context.Audits.Add(Harness.Audit(request, auditId));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            Assert.Contains("circular", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        await h.AssertStoredAsync(1, 1, 0);
    });

    [MapCapturePostgresFact]
    public Task AuditConsistency_RestorationAndReplayRejectMismatchedContribution() => AssertRejectedAuditMismatchAsync("contribution");

    [MapCapturePostgresFact]
    public Task AuditConsistency_RestorationAndReplayRejectMismatchedReviewer() => AssertRejectedAuditMismatchAsync("reviewer");

    [MapCapturePostgresFact]
    public Task AuditConsistency_RestorationAndReplayRejectMismatchedAcceptanceTimestamp() => AssertRejectedAuditMismatchAsync("timestamp");

    private static Task AssertRejectedAuditMismatchAsync(string mismatch) => EachStrategy(async (h, strategy) =>
    {
        var request = h.Request(); var key = Guid.NewGuid(); var auditId = Guid.NewGuid();
        var auditContribution = h.Contribution.ContributionId;
        await using (var context = h.NewContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            // Insert the contradiction before immutability triggers apply; do not disable them.
            if (mismatch == "contribution")
            {
                auditContribution = Guid.NewGuid();
                context.States.Add(new StateRow { Id = auditContribution, OwnerId = Guid.NewGuid(),
                    AttractionId = Guid.NewGuid(), SessionId = Guid.NewGuid() });
                await context.SaveChangesAsync();
            }
            var state = await context.States.SingleAsync(row => row.Id == request.ContributionId);
            state.Status = 2; state.Revision = 2; state.DecisionId = request.DecisionId;
            await context.SaveChangesAsync();
            context.Receipts.Add(h.Receipt(request, key, auditId));
            await context.SaveChangesAsync();
            var audit = Harness.Audit(request, auditId);
            audit.ContributionId = auditContribution;
            if (mismatch == "reviewer") audit.Reviewer = "different-synthetic-reviewer";
            if (mismatch == "timestamp") audit.AcceptedAt = AcceptedAt.AddSeconds(1);
            context.Audits.Add(audit);
            await context.SaveChangesAsync();
            await transaction.CommitAsync(); // Demonstrates that unchanged DDL permits this contradiction.
        }
        await using (var verify = h.NewContext())
        {
            var audit = await verify.Audits.AsNoTracking().SingleAsync();
            Assert.Equal(auditContribution, audit.ContributionId);
            Assert.Equal(mismatch == "reviewer" ? "different-synthetic-reviewer" : request.ReviewerSubject, audit.Reviewer);
            Assert.Equal(mismatch == "timestamp" ? AcceptedAt.AddSeconds(1) : AcceptedAt, audit.AcceptedAt);
        }
        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() => h.RestoreAsync());
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => h.DecideAsync(request, key, strategy));
        Assert.Equal("Experimental accepted evidence is incomplete.", restore.Message);
        Assert.Equal(restore.Message, replay.Message);
        Assert.DoesNotContain(request.ReviewerSubject, restore.Message);
        Assert.DoesNotContain(h.Contribution.PrivatePhotoStorageObjectKey, restore.Message);
        await using var unchanged = h.NewContext();
        var stored = await unchanged.States.AsNoTracking().SingleAsync(row => row.Id == request.ContributionId);
        Assert.Equal(2, stored.Status); Assert.Equal(2, stored.Revision);
        Assert.Equal(request.DecisionId, stored.DecisionId);
        Assert.Equal(1, await unchanged.Receipts.CountAsync()); Assert.Equal(1, await unchanged.Audits.CountAsync());
    });

    private static async Task Observe(Task task) { try { await task; } catch { /* Cleanup only; test body observes expected failures. */ } }
    private static async Task EachStrategy(Func<Harness, Strategy, Task> test)
    {
        foreach (var strategy in Enum.GetValues<Strategy>()) await WithHarness(h => test(h, strategy));
    }
    private static Task WithHarness(Func<Harness, Task> test) => MapContributionModerationPersistencePrototypeTests.WithDatabase(async connection =>
    {
        var harness = new Harness(connection);
        await harness.InitializeAsync();
        await test(harness);
    });

    private sealed class Probe(bool pause = false)
    {
        internal bool Pause { get; } = pause;
        internal TaskCompletionSource<(int Pid, Guid Context)> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record SqlStatement(string Sql, string[] Parameters, bool InTransaction);
    private sealed class SqlCapture : DbCommandInterceptor
    {
        internal ConcurrentQueue<SqlStatement> Commands { get; } = new();
        private void Capture(DbCommand command) => Commands.Enqueue(new(command.CommandText,
            command.Parameters.Cast<DbParameter>().Select(p => $"{p.ParameterName}:{p.DbType}").ToArray(), command.Transaction is not null));
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Capture(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Capture(command); return ValueTask.FromResult(result); }
    }

    private sealed class Harness(string connection)
    {
        internal MapContribution Contribution { get; } = MapContribution.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "synthetic/private/photo.jpg", "Synthetic", new MapContributionLocationEvidence(7, 80, 7, 80, 5, AcceptedAt.AddSeconds(-30), AcceptedAt.AddSeconds(-5)), AcceptedAt);
        internal Scope Scope => new(Contribution.OwnerHobbiesModuleProfileId, Contribution.AttractionId, Contribution.CaptureSessionId);
        internal SqlCapture Trace { get; } = new();
        internal EfContext NewContext() => Context(connection, Trace);
        internal MapContributionReviewRequest Request(MapContributionReviewAction action = MapContributionReviewAction.Approve, long revision = 1) =>
            new(Guid.NewGuid(), Contribution.ContributionId, action, revision, "synthetic-trusted-reviewer");
        internal async Task InitializeAsync()
        {
            await using var database = new NpgsqlConnection(connection);
            await database.OpenAsync();
            await using var ddl = new NpgsqlCommand(MapContributionModerationPersistencePrototypeTests.Schema, database);
            await ddl.ExecuteNonQueryAsync();
            await using var context = NewContext();
            context.States.Add(new StateRow { Id = Contribution.ContributionId, OwnerId = Scope.Owner,
                AttractionId = Scope.Attraction, SessionId = Scope.Session });
            await context.SaveChangesAsync();
        }
        private IQueryable<StateRow> Scoped(EfContext context, Guid id, Scope scope) => context.States.Where(row =>
            row.Id == id && row.OwnerId == scope.Owner && row.AttractionId == scope.Attraction && row.SessionId == scope.Session && row.Module == scope.Module);

        internal async Task<Result> DecideAsync(MapContributionReviewRequest request, Guid key, Strategy strategy,
            Probe? probe = null, Fault fault = Fault.None, Scope? scope = null, CancellationToken token = default)
        {
            scope ??= Scope; // Synthetic resource authorization only; no real reviewer permission claim.
            try
            {
                await using var context = NewContext();
                await context.Database.OpenConnectionAsync(token);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                probe?.Started.TrySetResult((((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID, context.ContextId.InstanceId));
                var row = strategy == Strategy.RowLock
                    ? await context.States.FromSqlInterpolated($"""
                        SELECT * FROM m3_moderation_prototype.contributions
                        WHERE contribution_id={request.ContributionId} AND owner_id={scope.Owner}
                          AND attraction_id={scope.Attraction} AND session_id={scope.Session} AND module={scope.Module}
                        FOR UPDATE
                        """).SingleOrDefaultAsync(token)
                    : await Scoped(context, request.ContributionId, scope).SingleOrDefaultAsync(token);
                if (row is null) return new(Outcome.Denied);
                var replay = await ReplayAsync(context, request, key, scope, token);
                if (replay is not null) return replay;
                if (row.Status != 1 || row.Revision != request.ExpectedRevision) return new(Outcome.Conflict);
                var domain = MapContributionModeration.Restore(Contribution, Snapshot(row, null), null);
                var accepted = domain.Decide(request, AcceptedAt);
                row.Status = request.Action == MapContributionReviewAction.Approve ? 2 : 3;
                row.Revision = accepted.Revision; row.DecisionId = request.DecisionId;
                await context.SaveChangesAsync(token); // EF guards the key, original revision and original immutable scope.
                if (probe is not null)
                {
                    probe.Holding.TrySetResult();
                    if (probe.Pause) await probe.Release.Task.WaitAsync(token);
                }
                var auditId = Guid.NewGuid();
                context.Receipts.Add(Receipt(request, fault == Fault.ReceiptConstraint ? Guid.Empty : key, auditId));
                await context.SaveChangesAsync(token);
                if (fault == Fault.AfterReceipt) throw new IOException("Synthetic failure after EF receipt save.");
                var audit = Audit(request, auditId);
                if (fault == Fault.AuditConstraint) audit.Reviewer = "";
                context.Audits.Add(audit);
                await context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                if (fault == Fault.LostAcknowledgement) throw new IOException("Synthetic acknowledgement loss after real EF commit.");
                return new(Outcome.Applied, accepted);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Failed context/transaction are disposed first. Never retry the transition blindly.
                await using var fresh = NewContext();
                if (!await Scoped(fresh, request.ContributionId, scope).AnyAsync(token)) return new(Outcome.Denied);
                return await ReplayAsync(fresh, request, key, scope, token) ?? new(Outcome.Conflict);
            }
        }

        internal ReceiptRow Receipt(MapContributionReviewRequest request, Guid key, Guid auditId) => new()
        {
            Id = request.DecisionId, Key = key, ContributionId = request.ContributionId,
            OwnerId = Scope.Owner, AttractionId = Scope.Attraction, SessionId = Scope.Session,
            Action = (int)request.Action, ExpectedRevision = request.ExpectedRevision, ResultRevision = checked(request.ExpectedRevision + 1),
            ResultStatus = request.Action == MapContributionReviewAction.Approve ? 2 : 3,
            Reviewer = request.ReviewerSubject, AcceptedAt = AcceptedAt, AuditId = auditId
        };
        internal static AuditRow Audit(MapContributionReviewRequest request, Guid auditId) => new()
        { Id = auditId, DecisionId = request.DecisionId, ContributionId = request.ContributionId, Reviewer = request.ReviewerSubject, AcceptedAt = AcceptedAt };

        private async Task<Result?> ReplayAsync(EfContext context, MapContributionReviewRequest request, Guid key, Scope scope, CancellationToken token)
        {
            var receipt = await context.Receipts.AsNoTracking().SingleOrDefaultAsync(row => row.ContributionId == request.ContributionId &&
                row.Key == key && row.OwnerId == scope.Owner && row.AttractionId == scope.Attraction && row.SessionId == scope.Session && row.Module == scope.Module, token);
            if (receipt is null) return null;
            if (RequestFrom(receipt) != request) return new(Outcome.Conflict);
            var current = await Scoped(context, request.ContributionId, scope).AsNoTracking().SingleAsync(token);
            return new(Outcome.Replay, (await RestoreRowsAsync(context, current, receipt, token)).Snapshot);
        }
        internal async Task<MapContributionModeration> RestoreAsync()
        {
            await using var context = NewContext();
            var state = await Scoped(context, Contribution.ContributionId, Scope).AsNoTracking().SingleAsync();
            var receipt = await context.Receipts.AsNoTracking().SingleAsync(row => row.Id == state.DecisionId);
            return await RestoreRowsAsync(context, state, receipt, CancellationToken.None);
        }
        private async Task<MapContributionModeration> RestoreRowsAsync(EfContext context, StateRow state, ReceiptRow receipt, CancellationToken token)
        {
            if (state.DecisionId != receipt.Id || state.Revision != receipt.ResultRevision || state.Status != receipt.ResultStatus ||
                !await context.Audits.AsNoTracking().AnyAsync(a => a.Id == receipt.AuditId && a.DecisionId == receipt.Id &&
                    a.ContributionId == state.Id && a.Reviewer == receipt.Reviewer && a.AcceptedAt == receipt.AcceptedAt, token))
                throw new InvalidOperationException("Experimental accepted evidence is incomplete.");
            var request = RequestFrom(receipt);
            return MapContributionModeration.Restore(Contribution, Snapshot(state, new(request, receipt.AcceptedAt)), request.Action);
        }
        private static MapContributionReviewRequest RequestFrom(ReceiptRow row) =>
            new(row.Id, row.ContributionId, (MapContributionReviewAction)row.Action, row.ExpectedRevision, row.Reviewer);
        private static MapContributionModerationSnapshot Snapshot(StateRow row, MapContributionReviewDecision? decision) =>
            new(row.Id, row.OwnerId, row.AttractionId, row.SessionId, row.Revision, decision);

        internal async Task AssertStoredAsync(int status, long revision, int count)
        {
            await using var context = NewContext();
            var row = await context.States.AsNoTracking().SingleAsync();
            Assert.Equal(status, row.Status); Assert.Equal(revision, row.Revision);
            Assert.Equal(Contribution.ContributionId, row.Id); Assert.Equal(Scope.Owner, row.OwnerId);
            Assert.Equal(Scope.Attraction, row.AttractionId); Assert.Equal(Scope.Session, row.SessionId); Assert.Equal("hobbies", row.Module);
            Assert.Equal(count, await context.Receipts.CountAsync()); Assert.Equal(count, await context.Audits.CountAsync());
            await context.Database.OpenConnectionAsync();
            await using var check = new NpgsqlCommand("SELECT to_regnamespace('andril_hobbies') IS NULL AND to_regnamespace('andril_identity') IS NULL",
                (NpgsqlConnection)context.Database.GetDbConnection());
            Assert.Equal(true, await check.ExecuteScalarAsync());
        }
        internal async Task AssertBlockedAsync(Probe first, Probe second, Task operation, CancellationToken token)
        {
            var a = await first.Started.Task.WaitAsync(token); var b = await second.Started.Task.WaitAsync(token);
            Assert.NotEqual(a.Context, b.Context); Assert.NotEqual(a.Pid, b.Pid);
            await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync(token);
            while (true)
            {
                token.ThrowIfCancellationRequested(); Assert.False(operation.IsCompleted);
                await using var query = new NpgsqlCommand("SELECT $1=ANY(pg_blocking_pids($2))", observer);
                query.Parameters.AddWithValue(a.Pid); query.Parameters.AddWithValue(b.Pid);
                if (await query.ExecuteScalarAsync(token) is true) return;
                await Task.Delay(20, token); // Wait for a database-observed condition, not an assumed race duration.
            }
        }
    }

    private static EfContext Context(string connection, SqlCapture trace) => new(new DbContextOptionsBuilder<EfContext>()
        .UseNpgsql(connection, options => options.CommandTimeout(20)).AddInterceptors(trace).Options);

    private sealed class StateRow
    {
        public Guid Id { get; set; } public Guid OwnerId { get; set; } public Guid AttractionId { get; set; } public Guid SessionId { get; set; }
        public string Module { get; set; } = "hobbies"; public int Status { get; set; } = 1;
        public long Revision { get; set; } = 1; public Guid? DecisionId { get; set; }
    }
    private sealed class ReceiptRow
    {
        public Guid Id { get; set; } public Guid Key { get; set; } public Guid ContributionId { get; set; }
        public Guid OwnerId { get; set; } public Guid AttractionId { get; set; } public Guid SessionId { get; set; }
        public string Module { get; set; } = "hobbies"; public int Action { get; set; }
        public long ExpectedRevision { get; set; } public long ResultRevision { get; set; } public int ResultStatus { get; set; }
        public string Reviewer { get; set; } = ""; public DateTimeOffset AcceptedAt { get; set; }
        public Guid AuditId { get; set; } public int PreviousStatus { get; set; } = 1;
    }
    private sealed class AuditRow
    {
        public Guid Id { get; set; } public Guid DecisionId { get; set; } public Guid ContributionId { get; set; }
        public string Reviewer { get; set; } = ""; public DateTimeOffset AcceptedAt { get; set; }
    }
    private sealed class EfContext(DbContextOptions<EfContext> options) : DbContext(options)
    {
        internal DbSet<StateRow> States => Set<StateRow>(); internal DbSet<ReceiptRow> Receipts => Set<ReceiptRow>(); internal DbSet<AuditRow> Audits => Set<AuditRow>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var state = model.Entity<StateRow>(); state.ToTable("contributions", "m3_moderation_prototype",
                table => table.HasCheckConstraint("contributions_revision_check", "revision>0")); state.HasKey(x => x.Id);
            state.Property(x => x.Id).HasColumnName("contribution_id").ValueGeneratedNever();
            state.Property(x => x.OwnerId).HasColumnName("owner_id").IsConcurrencyToken(); state.Property(x => x.AttractionId).HasColumnName("attraction_id").IsConcurrencyToken();
            state.Property(x => x.SessionId).HasColumnName("session_id").IsConcurrencyToken(); state.Property(x => x.Module).HasColumnName("module").IsRequired().IsConcurrencyToken();
            state.Property(x => x.Status).HasColumnName("status"); state.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken();
            state.Property(x => x.DecisionId).HasColumnName("decision_id");
            state.HasAlternateKey(x => new { x.Id, x.OwnerId, x.AttractionId, x.SessionId, x.Module });
            var receipt = model.Entity<ReceiptRow>(); receipt.ToTable("receipts", "m3_moderation_prototype"); receipt.HasKey(x => x.Id);
            receipt.Property(x => x.Id).HasColumnName("decision_id").ValueGeneratedNever(); receipt.Property(x => x.Key).HasColumnName("idempotency_key");
            receipt.Property(x => x.ContributionId).HasColumnName("contribution_id"); receipt.Property(x => x.OwnerId).HasColumnName("owner_id");
            receipt.Property(x => x.AttractionId).HasColumnName("attraction_id"); receipt.Property(x => x.SessionId).HasColumnName("session_id");
            receipt.Property(x => x.Module).HasColumnName("module").IsRequired(); receipt.Property(x => x.Action).HasColumnName("action");
            receipt.Property(x => x.ExpectedRevision).HasColumnName("expected_revision"); receipt.Property(x => x.ResultRevision).HasColumnName("result_revision");
            receipt.Property(x => x.ResultStatus).HasColumnName("result_status"); receipt.Property(x => x.Reviewer).HasColumnName("reviewer").IsRequired();
            receipt.Property(x => x.AcceptedAt).HasColumnName("accepted_at"); receipt.Property(x => x.AuditId).HasColumnName("audit_id");
            receipt.Property(x => x.PreviousStatus).HasColumnName("previous_status");
            receipt.HasIndex(x => new { x.ContributionId, x.Key }).IsUnique().HasDatabaseName("ux_receipt_key");
            receipt.HasIndex(x => new { x.ContributionId, x.ExpectedRevision }).IsUnique().HasDatabaseName("ux_receipt_revision");
            receipt.HasAlternateKey(x => new { x.Id, x.ContributionId, x.ResultStatus, x.ResultRevision });
            var audit = model.Entity<AuditRow>(); audit.ToTable("audit", "m3_moderation_prototype"); audit.HasKey(x => x.Id);
            audit.Property(x => x.Id).HasColumnName("audit_id").ValueGeneratedNever(); audit.Property(x => x.DecisionId).HasColumnName("decision_id");
            audit.Property(x => x.ContributionId).HasColumnName("contribution_id"); audit.Property(x => x.Reviewer).HasColumnName("reviewer").IsRequired();
            audit.Property(x => x.AcceptedAt).HasColumnName("accepted_at"); audit.HasAlternateKey(x => new { x.Id, x.DecisionId });
            audit.HasIndex(x => x.DecisionId).IsUnique();
            state.HasOne<ReceiptRow>().WithMany().HasForeignKey(x => new { x.DecisionId, x.Id, x.Status, x.Revision })
                .HasPrincipalKey(x => new { x.Id, x.ContributionId, x.ResultStatus, x.ResultRevision }).OnDelete(DeleteBehavior.NoAction);
            receipt.HasOne<StateRow>().WithMany().HasForeignKey(x => new { x.ContributionId, x.OwnerId, x.AttractionId, x.SessionId, x.Module })
                .HasPrincipalKey(x => new { x.Id, x.OwnerId, x.AttractionId, x.SessionId, x.Module }).OnDelete(DeleteBehavior.NoAction);
            receipt.HasOne<AuditRow>().WithMany().HasForeignKey(x => new { x.AuditId, x.Id })
                .HasPrincipalKey(x => new { x.Id, x.DecisionId }).OnDelete(DeleteBehavior.NoAction);
            audit.HasOne<ReceiptRow>().WithMany().HasForeignKey(x => x.DecisionId).OnDelete(DeleteBehavior.NoAction);
            audit.HasOne<StateRow>().WithMany().HasForeignKey(x => x.ContributionId).OnDelete(DeleteBehavior.NoAction);
        }
    }
}
