using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

// User-reported M2/M6 MAP-36C authorization: disposable protocol proof only.
// Real PostgreSQL transactions; synthetic authorization and test-owned lifecycle/receipt/audit DDL.
// This neither changes nor proves the production submission writer or M2 authorization integration.
public sealed class MapAttractionLifecycleTransactionPrototypeTests
{
    [MapCapturePostgresFact]
    public Task SharedLock_SubmissionFirst_CommitsBeforeAuditedDeactivation() => WithFixture(h => Race(h, true));

    [MapCapturePostgresFact]
    public Task SharedLock_DeactivationFirst_RejectsWaitingSubmission() => WithFixture(h => Race(h, false));

    private static async Task Race(Harness h, bool submissionFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new Probe(true);
        var waiting = new Probe();
        var first = submissionFirst ? h.Submit(h.Session, 0, held, deadline.Token) : h.Change(h.Request(false, 0), held, deadline.Token);
        Task<string>? second = null;
        try
        {
            var holder = await held.StateLocked.Task.WaitAsync(deadline.Token);
            second = submissionFirst ? h.Change(h.Request(false, 0), waiting, deadline.Token) : h.Submit(h.Session, 1, waiting, deadline.Token);
            await h.Blocked(holder, waiting, second, deadline.Token);
            Assert.True(waiting.IdentityLocked.Task.IsCompletedSuccessfully);
            Assert.False(waiting.StateLocked.Task.IsCompleted);
            held.Release.TrySetResult();
            Assert.Equal(submissionFirst ? "Created" : "Changed", await first);
            Assert.Equal(submissionFirst ? "Changed" : "Denied", await second);
            await h.Stored(false, 1, submissionFirst ? 1 : 0, 1);
        }
        finally { held.Release.TrySetResult(); await Drain(first); await Drain(second); }
    }

    [MapCapturePostgresFact]
    public Task Lifecycle_CompetingVersions_HaveOneAuditedWinner() => WithFixture(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new Probe(true);
        var first = h.Change(h.Request(false, 0), held, deadline.Token);
        Task<string>? second = null;
        try
        {
            var holder = await held.StateLocked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            second = h.Change(h.Request(false, 0), waiting, deadline.Token);
            await h.Blocked(holder, waiting, second, deadline.Token);
            Assert.False(waiting.IdentityLocked.Task.IsCompleted);
            held.Release.TrySetResult();
            Assert.Equal("Changed", await first);
            Assert.Equal("Conflict", await second);
            Assert.Equal("Conflict", await h.Change(h.Request(true, 2)));
            await h.Stored(false, 1, 0, 1);
        }
        finally { held.Release.TrySetResult(); await Drain(first); await Drain(second); }
    });

    [MapCapturePostgresFact]
    public Task Lifecycle_ExactRetryAndNoOp_RetainOriginalAuditEvidence() => WithFixture(async h =>
    {
        var request = h.Request(false, 0);
        Assert.Equal("Changed", await h.Change(request));
        var original = await h.Receipt(request.RequestId);
        Assert.Equal("Existing", await h.Change(request));
        Assert.Equal("Conflict", await h.Change(new(request.RequestId, request.AttractionId, true, 0)));
        Assert.Equal("Conflict", await h.Change(new(request.RequestId, request.AttractionId, false, 1)));
        Assert.Equal("Conflict", await h.Change(h.Request(false, 0)));
        Assert.Equal("Unchanged", await h.Change(h.Request(false, 1)));
        Assert.Equal(original, await h.Receipt(request.RequestId));
        await h.Stored(false, 1, 0, 2);
    });

    [MapCapturePostgresFact]
    public Task SharedLock_FailedDeactivation_RollsBackAuditAndReleasesSubmission() => WithFixture(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new Probe(true) { BeforeCommit = async (c, t, ct) => { await using var cmd = new NpgsqlCommand("SELECT 1 / 0", c, t); await cmd.ExecuteScalarAsync(ct); } };
        var first = h.Change(h.Request(false, 0), held, deadline.Token);
        Task<string>? second = null;
        try
        {
            var holder = await held.StateLocked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            second = h.Submit(h.Session, 0, waiting, deadline.Token);
            await h.Blocked(holder, waiting, second, deadline.Token);
            held.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<PostgresException>(() => first);
            Assert.Equal(PostgresErrorCodes.DivisionByZero, error.SqlState);
            Assert.Equal("Created", await second);
            await h.Stored(true, 0, 1, 0);
        }
        finally { held.Release.TrySetResult(); await Drain(first); await Drain(second); }
    });

    [MapCapturePostgresFact]
    public Task SharedLock_CancelledDeactivation_RollsBackAndReleasesSubmission() => WithFixture(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var held = new Probe(true) { BeforeCommit = (_, _, _) => { cancel.Cancel(); return Task.CompletedTask; } };
        var first = h.Change(h.Request(false, 0), held, cancel.Token);
        Task<string>? second = null;
        try
        {
            var holder = await held.StateLocked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            second = h.Submit(h.Session, 0, waiting, deadline.Token);
            await h.Blocked(holder, waiting, second, deadline.Token);
            held.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal("Created", await second);
            await h.Stored(true, 0, 1, 0);
        }
        finally { held.Release.TrySetResult(); await Drain(first); await Drain(second); }
    });

    [MapCapturePostgresFact]
    public Task SharedLock_CancelledWaiter_WritesNothingAndReleasesLocks() => WithFixture(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var held = new Probe(true);
        var first = h.Change(h.Request(false, 0), held, deadline.Token);
        Task<string>? second = null;
        try
        {
            var holder = await held.StateLocked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            second = h.Submit(h.Session, 0, waiting, cancel.Token);
            await h.Blocked(holder, waiting, second, deadline.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            held.Release.TrySetResult();
            Assert.Equal("Changed", await first);
            Assert.Equal("Denied", await h.Submit(h.Session, 1));
            await h.Stored(false, 1, 0, 1);
        }
        finally { cancel.Cancel(); held.Release.TrySetResult(); await Drain(first); await Drain(second); }
    });

    [MapCapturePostgresFact]
    public Task Audit_MissingOrContradictoryInsert_FailsAtDeferredCommitAndRollsBack() => WithFixture(async h =>
    {
        foreach (var fault in new[] { 1, 2, 3, 4 })
        {
            var probe = new Probe { AuditFault = fault };
            var request = h.Request(false, 0);
            var error = await Assert.ThrowsAsync<PostgresException>(() => h.Change(request, probe));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            Assert.Equal("fk_lifecycle_receipt_audit_evidence", error.ConstraintName);
            Assert.True(probe.CommitAttempted);
            await h.Stored(true, 0, 0, 0);
            Assert.Null(await h.Receipt(request.RequestId));
        }
        Assert.Equal("Changed", await h.Change(h.Request(false, 0)));
        await h.Stored(false, 1, 0, 1);
    });

    [MapCapturePostgresFact]
    public Task Audit_AcceptedEvidenceAndReceipts_RejectMutation() => WithFixture(async h =>
    {
        Assert.Equal("Changed", await h.Change(h.Request(false, 0)));
        foreach (var sql in new[] {
            "UPDATE m3_lifecycle_proof.audit SET accepted_at = accepted_at + interval '1 microsecond'",
            "DELETE FROM m3_lifecycle_proof.audit",
            "UPDATE m3_lifecycle_proof.requests SET result_version = result_version + 1",
            "DELETE FROM m3_lifecycle_proof.requests" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => h.Execute(sql));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_lifecycle_evidence_immutable", error.ConstraintName);
        }
        await h.Stored(false, 1, 0, 1);
    });

    [MapCapturePostgresFact]
    public Task Authorization_TransactionFence_RejectsRevokedChangeAndAcceptedRetry() => WithFixture(async h =>
    {
        var accepted = h.Request(true, 0);
        Assert.Equal("Unchanged", await h.Change(accepted));
        await using var connection = await h.Open();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var revoke = Cmd(connection, transaction,
            "UPDATE m3_lifecycle_proof.authorization SET permitted = false WHERE subject_id = $1", h.Admin))
            Assert.Equal(1, await revoke.ExecuteNonQueryAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiting = new Probe();
        var change = h.Change(h.Request(false, 0), waiting, deadline.Token);
        try
        {
            await h.Blocked(connection.ProcessID, waiting, change, deadline.Token);
            Assert.False(waiting.IdentityLocked.Task.IsCompleted);
            await transaction.CommitAsync();
            Assert.Equal("Denied", await change);
            Assert.Equal("Denied", await h.Change(accepted));
            await h.Stored(true, 0, 0, 1);
        }
        finally { await transaction.DisposeAsync(); await Drain(change); }
    });

    [MapCapturePostgresFact]
    public Task Lifecycle_MissingStateAndForeignSession_FailClosed() => WithFixture(async h =>
    {
        Assert.Equal("Denied", await h.Submit(Guid.NewGuid(), 0));
        h.SubmissionOwner = Guid.NewGuid();
        Assert.Equal("Denied", await h.Submit(h.Session, 0));
        h.SubmissionOwner = h.Owner;
        await h.Execute("DELETE FROM m3_lifecycle_proof.states");
        Assert.Equal("Denied", await h.Submit(h.Session, 0));
        Assert.Equal("Denied", await h.Change(h.Request(true, 0)));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM m3_lifecycle_proof.requests"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM m3_lifecycle_proof.audit"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM m3_lifecycle_proof.submissions"));
    });

    [MapCapturePostgresFact]
    public Task Lifecycle_LostAcknowledgement_ReconcilesWithoutSecondTransition() => WithFixture(async h =>
    {
        var request = h.Request(false, 0);
        await Assert.ThrowsAsync<NpgsqlException>(() => h.Change(request, new Probe { LoseAcknowledgement = true }));
        var original = await h.Receipt(request.RequestId);
        Assert.NotNull(original);
        Assert.Equal("Existing", await h.Change(request));
        Assert.Equal(original, await h.Receipt(request.RequestId));
        await h.Stored(false, 1, 0, 1);
    });

    [MapCapturePostgresFact]
    public Task SharedLock_StatePrecedesSession_WithoutAnInvertedWaitCycle() => WithFixture(async h =>
    {
        await using var holder = await h.Open();
        await using var heldSession = await holder.BeginTransactionAsync();
        await using (var cmd = Cmd(holder, heldSession,
            "SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id = $1 FOR UPDATE", h.Session))
            Assert.Equal(h.Session, await cmd.ExecuteScalarAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var submissionProbe = new Probe();
        var submission = h.Submit(h.Session, 0, submissionProbe, deadline.Token);
        Task<string>? change = null;
        try
        {
            var submitPid = await submissionProbe.StateLocked.Task.WaitAsync(deadline.Token);
            await h.Blocked(holder.ProcessID, submissionProbe, submission, deadline.Token);
            Assert.False(submissionProbe.SessionLocked.Task.IsCompleted);
            var changeProbe = new Probe();
            change = h.Change(h.Request(false, 0), changeProbe, deadline.Token);
            await h.Blocked(submitPid, changeProbe, change, deadline.Token);
            await heldSession.CommitAsync();
            Assert.Equal("Created", await submission);
            Assert.Equal("Changed", await change);
            await h.Stored(false, 1, 1, 1);
        }
        finally { await heldSession.DisposeAsync(); await Drain(submission); await Drain(change); }
    });

    [MapCapturePostgresFact]
    public Task SharedLock_FailedSubmission_ReleasesAuditedDeactivation() => WithFixture(async h =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var held = new Probe(true) { BeforeCommit = (_, _, _) => throw new NpgsqlException("Synthetic pre-commit failure") };
        var first = h.Submit(h.Session, 0, held, deadline.Token);
        Task<string>? change = null;
        try
        {
            var pid = await held.StateLocked.Task.WaitAsync(deadline.Token);
            var waiting = new Probe();
            change = h.Change(h.Request(false, 0), waiting, deadline.Token);
            await h.Blocked(pid, waiting, change, deadline.Token);
            held.Release.TrySetResult();
            await Assert.ThrowsAsync<NpgsqlException>(() => first);
            Assert.Equal("Changed", await change);
            await h.Stored(false, 1, 0, 1);
        }
        finally { held.Release.TrySetResult(); await Drain(first); await Drain(change); }
    });

    private static Task WithFixture(Func<Harness, Task> test) => MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
    {
        var harness = new Harness(connection);
        await harness.Initialize();
        await test(harness);
    });

    private static async Task Drain(Task? task) { if (task is not null) try { await task; } catch { /* Cleanup after assertions; do not strand contenders. */ } }

    private sealed class Probe
    {
        internal Probe(bool pause = false) { if (!pause) Release.SetResult(); }
        internal TaskCompletionSource<int> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> IdentityLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> StateLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> SessionLocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? BeforeCommit { get; init; }
        internal int AuditFault { get; init; }
        internal bool LoseAcknowledgement { get; init; }
        internal bool CommitAttempted { get; set; }
    }

    private sealed class Harness(string connection)
    {
        internal Guid Admin { get; } = Guid.NewGuid();
        internal Guid Owner { get; } = Guid.NewGuid();
        internal Guid Session { get; } = Guid.NewGuid();
        internal Guid SubmissionOwner { get; set; }
        private readonly MapAttraction _attraction = MapAttraction.Create(Guid.NewGuid(), "Synthetic shared-lock proof", 0, 0);
        internal MapAttractionLifecycleChange Request(bool active, long expected) => new(Guid.NewGuid(), _attraction.AttractionId, active, expected);
        internal async Task<NpgsqlConnection> Open(CancellationToken token = default)
        { var c = new NpgsqlConnection(connection); try { await c.OpenAsync(token); return c; } catch { await c.DisposeAsync(); throw; } }
        private HobbiesMapDbContext Context() => new(new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory).Options);

        internal async Task Initialize()
        {
            SubmissionOwner = Owner;
            await using (var context = Context())
            {
                context.MapAttractions.Add(_attraction);
                var capture = MapContributionCaptureSession.Create(Session, Owner, _attraction.AttractionId, DateTimeOffset.UtcNow);
                capture.Complete(capture.StartedAtUtc.AddSeconds(1));
                context.CaptureSessions.Add(capture);
                await context.SaveChangesAsync();
            }
            await Execute("""
                CREATE SCHEMA m3_lifecycle_proof;
                CREATE TABLE m3_lifecycle_proof.authorization (subject_id uuid PRIMARY KEY, permitted boolean NOT NULL);
                CREATE TABLE m3_lifecycle_proof.states (
                    attraction_id uuid PRIMARY KEY REFERENCES andril_hobbies.map_attractions(attraction_id),
                    is_active boolean NOT NULL, version bigint NOT NULL CHECK (version >= 0));
                CREATE TABLE m3_lifecycle_proof.submissions (
                    session_id uuid PRIMARY KEY, owner_id uuid NOT NULL, attraction_id uuid NOT NULL,
                    status integer NOT NULL CHECK (status = 1),
                    FOREIGN KEY (session_id, owner_id, attraction_id) REFERENCES andril_hobbies.map_contribution_capture_sessions
                        (session_id, owner_hobbies_module_profile_id, attraction_id));
                CREATE TABLE m3_lifecycle_proof.requests (
                    actor_id uuid NOT NULL, request_id uuid NOT NULL, attraction_id uuid NOT NULL,
                    desired boolean NOT NULL, expected_version bigint NOT NULL CHECK (expected_version >= 0),
                    result_version bigint NOT NULL CHECK (result_version >= 0), outcome text COLLATE "C" NOT NULL CHECK (outcome IN ('Changed','Unchanged')),
                    accepted_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY (actor_id, request_id));
                CREATE TABLE m3_lifecycle_proof.audit (
                    actor_id uuid NOT NULL, request_id uuid NOT NULL, attraction_id uuid NOT NULL,
                    desired boolean NOT NULL, expected_version bigint NOT NULL, result_version bigint NOT NULL,
                    outcome text COLLATE "C" NOT NULL, accepted_at timestamptz NOT NULL,
                    PRIMARY KEY (actor_id, request_id),
                    CONSTRAINT ak_lifecycle_audit_evidence UNIQUE (actor_id, request_id, attraction_id, desired, expected_version, result_version, outcome, accepted_at),
                    CONSTRAINT fk_lifecycle_audit_request FOREIGN KEY (actor_id,request_id)
                        REFERENCES m3_lifecycle_proof.requests(actor_id,request_id) DEFERRABLE INITIALLY DEFERRED);
                ALTER TABLE m3_lifecycle_proof.requests ADD CONSTRAINT fk_lifecycle_receipt_audit_evidence
                    FOREIGN KEY (actor_id, request_id, attraction_id, desired, expected_version, result_version, outcome, accepted_at)
                    REFERENCES m3_lifecycle_proof.audit(actor_id, request_id, attraction_id, desired, expected_version, result_version, outcome, accepted_at)
                    DEFERRABLE INITIALLY DEFERRED;
                CREATE FUNCTION m3_lifecycle_proof.reject_evidence_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'Test evidence is immutable' USING ERRCODE = '23514', CONSTRAINT = 'ck_lifecycle_evidence_immutable'; END $$;
                CREATE TRIGGER audit_immutable BEFORE UPDATE OR DELETE ON m3_lifecycle_proof.audit
                    FOR EACH ROW EXECUTE FUNCTION m3_lifecycle_proof.reject_evidence_mutation();
                CREATE TRIGGER request_immutable BEFORE UPDATE OR DELETE ON m3_lifecycle_proof.requests
                    FOR EACH ROW EXECUTE FUNCTION m3_lifecycle_proof.reject_evidence_mutation();
                """);
            await using var c = await Open();
            await using (var seed = Cmd(c, null, "INSERT INTO m3_lifecycle_proof.states VALUES ($1, true, 0);", _attraction.AttractionId)) await seed.ExecuteNonQueryAsync();
            await using (var seed = Cmd(c, null, "INSERT INTO m3_lifecycle_proof.authorization VALUES ($1,true),($2,true)", Admin, Owner)) await seed.ExecuteNonQueryAsync();
            Assert.Equal(2L, await Scalar("""
                SELECT count(*) FROM pg_constraint WHERE connamespace = 'm3_lifecycle_proof'::regnamespace
                AND conname IN ('fk_lifecycle_receipt_audit_evidence','fk_lifecycle_audit_request')
                AND condeferrable AND condeferred AND convalidated
                """));
            Assert.Equal(1L, await Scalar("SELECT count(*) FROM pg_constraint WHERE connamespace = 'm3_lifecycle_proof'::regnamespace AND conname = 'ak_lifecycle_audit_evidence' AND NOT condeferrable AND convalidated"));
        }

        internal Task<string> Change(MapAttractionLifecycleChange request, Probe? probe = null, CancellationToken token = default) =>
            Run(true, request, Guid.Empty, request.ExpectedVersion, probe ?? new(), token);
        internal Task<string> Submit(Guid session, long expected, Probe? probe = null, CancellationToken token = default) =>
            Run(false, null, session, expected, probe ?? new(), token);

        private async Task<string> Run(bool management, MapAttractionLifecycleChange? request, Guid session,
            long expected, Probe probe, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await using var c = await Open(token);
            await using var t = await c.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token);
            probe.Opened.TrySetResult(c.ProcessID);
            var actor = management ? Admin : SubmissionOwner;
            await using (var auth = Cmd(c, t, "SELECT permitted FROM m3_lifecycle_proof.authorization WHERE subject_id = $1 FOR UPDATE", actor))
                if (await auth.ExecuteScalarAsync(token) is not true) return "Denied";
            probe.IdentityLocked.TrySetResult(c.ProcessID);
            MapAttractionStatePrototype state;
            await using (var locked = Cmd(c, t, "SELECT is_active, version FROM m3_lifecycle_proof.states WHERE attraction_id = $1 FOR UPDATE", _attraction.AttractionId))
            await using (var reader = await locked.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token)) return "Denied";
                state = MapAttractionStatePrototype.CreateSnapshot(_attraction, reader.GetBoolean(0), reader.GetInt64(1));
            }
            probe.StateLocked.TrySetResult(c.ProcessID);
            await probe.Release.Task.WaitAsync(token);
            string outcome;
            if (management)
            {
                if (request!.AttractionId != state.AttractionId) return "Denied";
                await using (var prior = Cmd(c, t, "SELECT attraction_id,desired,expected_version FROM m3_lifecycle_proof.requests WHERE actor_id=$1 AND request_id=$2", actor, request.RequestId))
                await using (var reader = await prior.ExecuteReaderAsync(token))
                    if (await reader.ReadAsync(token)) return reader.GetGuid(0) == request.AttractionId && reader.GetBoolean(1) == request.IsActive
                        && reader.GetInt64(2) == request.ExpectedVersion ? "Existing" : "Conflict";
                if (state.Version != request.ExpectedVersion) return "Conflict";
                var next = state.Transition(request.IsActive, request.ExpectedVersion);
                outcome = ReferenceEquals(next, state) ? "Unchanged" : "Changed";
                await using (var update = Cmd(c, t, "UPDATE m3_lifecycle_proof.states SET is_active=$1,version=$2 WHERE attraction_id=$3 AND version=$4", next.IsActive,next.Version,state.AttractionId,state.Version))
                    Assert.Equal(1, await update.ExecuteNonQueryAsync(token));
                await using (var receipt = Cmd(c,t,"INSERT INTO m3_lifecycle_proof.requests(actor_id,request_id,attraction_id,desired,expected_version,result_version,outcome) VALUES($1,$2,$3,$4,$5,$6,$7)",actor,request.RequestId,state.AttractionId,request.IsActive,state.Version,next.Version,outcome))
                    await receipt.ExecuteNonQueryAsync(token);
                if (probe.AuditFault != 1)
                {
                    await using var audit = Cmd(c,t,"""
                        INSERT INTO m3_lifecycle_proof.audit
                        SELECT actor_id,request_id,CASE WHEN $3=2 THEN $4 ELSE attraction_id END,desired,expected_version,
                            result_version + CASE WHEN $3=3 THEN 1 ELSE 0 END,outcome,
                            accepted_at + CASE WHEN $3=4 THEN interval '1 microsecond' ELSE interval '0' END
                        FROM m3_lifecycle_proof.requests WHERE actor_id=$1 AND request_id=$2
                        """,actor,request.RequestId,probe.AuditFault,Guid.NewGuid());
                    await audit.ExecuteNonQueryAsync(token);
                }
            }
            else
            {
                await using (var capture = Cmd(c,t,"SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id=$1 AND owner_hobbies_module_profile_id=$2 AND attraction_id=$3 AND completed_at_utc IS NOT NULL FOR UPDATE",session,actor,state.AttractionId))
                    if (await capture.ExecuteScalarAsync(token) is null) return "Denied";
                probe.SessionLocked.TrySetResult(c.ProcessID);
                if (!state.IsActive) return "Denied";
                if (state.Version != expected) return "Conflict";
                await using var insert = Cmd(c,t,"INSERT INTO m3_lifecycle_proof.submissions VALUES($1,$2,$3,1)",session,actor,state.AttractionId);
                await insert.ExecuteNonQueryAsync(token);
                outcome = "Created";
            }
            if (probe.BeforeCommit is not null) await probe.BeforeCommit(c,t,token);
            token.ThrowIfCancellationRequested();
            probe.CommitAttempted = true;
            await t.CommitAsync(token);
            if (probe.LoseAcknowledgement) throw new NpgsqlException("Synthetic acknowledgement loss after commit");
            return outcome;
        }

        internal async Task Blocked(int holder, Probe waiter, Task operation, CancellationToken token)
        {
            var pid = await waiter.Opened.Task.WaitAsync(token);
            Assert.NotEqual(holder,pid);
            await using var c = await Open(token);
            await using var query = Cmd(c,null,"SELECT $1 = ANY(pg_blocking_pids($2))",holder,pid);
            while (await query.ExecuteScalarAsync(token) is not true)
            { Assert.False(operation.IsCompleted); await Task.Delay(20,token); }
            Assert.False(operation.IsCompleted);
        }

        internal async Task Stored(bool active,long version,int submissions,int requests)
        {
            await using var c = await Open();
            await using (var cmd = Cmd(c,null,"SELECT is_active,version FROM m3_lifecycle_proof.states"))
            await using (var reader = await cmd.ExecuteReaderAsync())
            { Assert.True(await reader.ReadAsync()); Assert.Equal(active,reader.GetBoolean(0)); Assert.Equal(version,reader.GetInt64(1)); }
            Assert.Equal((long)submissions,await Scalar("SELECT count(*) FROM m3_lifecycle_proof.submissions"));
            Assert.Equal((long)requests,await Scalar("SELECT count(*) FROM m3_lifecycle_proof.requests"));
            Assert.Equal((long)requests,await Scalar("SELECT count(*) FROM m3_lifecycle_proof.audit"));
            Assert.Equal(0L,await Scalar("""
                SELECT count(*) FROM m3_lifecycle_proof.requests r FULL JOIN m3_lifecycle_proof.audit a USING(actor_id,request_id)
                WHERE ROW(r.attraction_id,r.desired,r.expected_version,r.result_version,r.outcome,r.accepted_at)
                    IS DISTINCT FROM ROW(a.attraction_id,a.desired,a.expected_version,a.result_version,a.outcome,a.accepted_at)
                """));
            await using var context = Context();
            Assert.Empty(await context.MapContributions.ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        }

        internal async Task<string?> Receipt(Guid request)
        {
            await using var c = await Open();
            await using var cmd = Cmd(c,null,"SELECT ROW(attraction_id,desired,expected_version,result_version,outcome,accepted_at)::text FROM m3_lifecycle_proof.requests WHERE actor_id=$1 AND request_id=$2",Admin,request);
            return await cmd.ExecuteScalarAsync() as string;
        }
        internal async Task Execute(string sql) { await using var c=await Open(); await using var cmd=new NpgsqlCommand(sql,c); await cmd.ExecuteNonQueryAsync(); }
        internal async Task<long> Scalar(string sql) { await using var c=await Open(); await using var cmd=new NpgsqlCommand(sql,c); return (long)(await cmd.ExecuteScalarAsync())!; }
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection c,NpgsqlTransaction? t,string sql,params object[] values)
    { var cmd=new NpgsqlCommand(sql,c,t); foreach(var value in values) cmd.Parameters.AddWithValue(value); return cmd; }
}
