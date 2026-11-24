using System.Data;
using System.Data.Common;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapContributionSubmissionWriterTests
{
    private static async Task EnsureLifecycleFixtureAsync(HobbiesMapDbContext context, Guid attraction, Guid owner)
    {
        await context.Database.OpenConnectionAsync();
        var c = (NpgsqlConnection)context.Database.GetDbConnection();
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('andril_hobbies.map_attraction_lifecycle') IS NOT NULL", c))
            if (await exists.ExecuteScalarAsync() is not true)
                await context.Database.ExecuteSqlRawAsync(MapAttractionLifecycleTestSchema.Sql);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS public.m3_submission_authorization(profile_id uuid PRIMARY KEY, permitted boolean NOT NULL)");
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.m3_submission_authorization VALUES({owner},true) ON CONFLICT DO NOTHING");
        // Explicit test-owned initial state, never runtime provisioning or a reset of inactive state.
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_hobbies.map_attraction_lifecycle VALUES({attraction},true,0,0,NULL) ON CONFLICT DO NOTHING");
        await context.Database.CloseConnectionAsync();
    }

    // Synthetic M2 fence, derived from server test context rather than the submitted payload.
    private sealed class TrustedSubmissionAuthorization(Guid owner) : IMapSubmissionTransactionAuthorization
    {
        internal bool Unavailable { get; init; }
        public async Task<Guid?> ResolveActiveHobbiesOwnerAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            var c = Assert.IsType<NpgsqlConnection>(connection);
            var t = Assert.IsType<NpgsqlTransaction>(transaction);
            Assert.Same(c, t.Connection);
            Assert.Equal(IsolationLevel.ReadCommitted, t.IsolationLevel);
            if (Unavailable) throw new InvalidOperationException("Synthetic private dependency detail");
            await using var command = LifecycleCommand(c, t, "SELECT permitted FROM public.m3_submission_authorization WHERE profile_id=$1 FOR SHARE", owner);
            return await command.ExecuteScalarAsync(cancellationToken) is true ? owner : null;
        }
    }

    private sealed class LifecycleSubject : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public bool IsAuthenticated => true;
    }

    private sealed class LifecycleAuthorization(LifecycleSubject subject) : IMapAttractionManagementAuthorization
    {
        internal TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MapAttractionManagementAuthorizationResult> AuthorizeAsync(DbConnection connection, DbTransaction transaction,
            Guid attractionId, bool requestedIsActive, CancellationToken token = default)
        {
            var c = Assert.IsType<NpgsqlConnection>(connection);
            var t = Assert.IsType<NpgsqlTransaction>(transaction);
            Assert.Same(c, t.Connection);
            Assert.Equal(IsolationLevel.ReadCommitted, t.IsolationLevel);
            Entered.TrySetResult(c.ProcessID);
            await using var command = LifecycleCommand(c, t, "SELECT permitted FROM public.m3_submission_authorization WHERE profile_id=$1 FOR SHARE", subject.UserId!.Value);
            var allowed = await command.ExecuteScalarAsync(token) is true;
            return new(allowed ? MapAttractionManagementAuthorizationOutcome.Satisfied : MapAttractionManagementAuthorizationOutcome.Denied,
                allowed ? new(subject.UserId.Value, attractionId, requestedIsActive) : null);
        }
    }

    private sealed class CommitGate(bool fail = false) : DbTransactionInterceptor
    {
        internal TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(((NpgsqlConnection)transaction.Connection!).ProcessID);
            await Release.Task.WaitAsync(cancellationToken);
            if (fail) throw new NpgsqlException("Synthetic pre-commit failure");
            return result;
        }
    }

    private sealed class InsertGate(bool fail = false) : SaveChangesInterceptor
    {
        internal TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(((NpgsqlConnection)eventData.Context!.Database.GetDbConnection()).ProcessID);
            await Release.Task.WaitAsync(cancellationToken);
            if (fail) throw new NpgsqlException("Synthetic rollback after insert");
            return result;
        }
    }

    private static NpgsqlCommand LifecycleCommand(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, c, t);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }

    private static async Task BlockedByAsync(string connection, int holder, int waiter, Task pending, CancellationToken token)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync(token);
        await using var command = LifecycleCommand(c, null,
            "WITH RECURSIVE blockers(pid) AS (SELECT unnest(pg_blocking_pids($2)) UNION SELECT unnest(pg_blocking_pids(b.pid)) FROM blockers b) SELECT EXISTS(SELECT 1 FROM blockers WHERE pid=$1)", holder, waiter);
        while (await command.ExecuteScalarAsync(token) is not true)
        {
            Assert.False(pending.IsCompleted);
            await Task.Delay(20, token);
        }
        Assert.False(pending.IsCompleted);
    }

    private static async Task<(MapAttractionLifecycleWriter Writer, LifecycleAuthorization Authorization)> LifecycleWriterAsync(string connection, IInterceptor? interceptor = null)
    {
        var subject = new LifecycleSubject();
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync();
        await using var command = LifecycleCommand(c, null, "INSERT INTO public.m3_submission_authorization VALUES($1,true)", subject.UserId!.Value);
        await command.ExecuteNonQueryAsync();
        var authorization = new LifecycleAuthorization(subject);
        return (new(new Contexts(connection, interceptor), authorization, subject), authorization);
    }

    private static async Task AssertLifecycleStoredAsync(string connection, bool active, long version, long receipts, int contributions)
    {
        await using var context = new Contexts(connection).CreateDbContext();
        var rows = await context.MapContributions.AsNoTracking().ToListAsync();
        Assert.Equal(contributions, rows.Count);
        Assert.All(rows, row => Assert.Equal(MapContributionModerationStatus.PendingReview, row.ModerationStatus));
        Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        await context.Database.OpenConnectionAsync();
        var c = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var state = new NpgsqlCommand("SELECT is_active,version,operation_sequence FROM andril_hobbies.map_attraction_lifecycle", c);
        await using (var reader = await state.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(active, reader.GetBoolean(0));
            Assert.Equal(version, reader.GetInt64(1));
            Assert.Equal(receipts, reader.GetInt64(2));
            Assert.False(await reader.ReadAsync());
        }
        await using var count = new NpgsqlCommand("SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts r JOIN andril_hobbies.map_attraction_lifecycle_audit a ON ROW(r.receipt_id,r.reviewer_subject_id,r.request_id,r.attraction_id,r.requested_is_active,r.expected_version,r.previous_is_active,r.previous_version,r.result_is_active,r.result_version,r.previous_operation_sequence,r.result_operation_sequence,r.outcome,r.accepted_at_utc) = ROW(a.receipt_id,a.reviewer_subject_id,a.request_id,a.attraction_id,a.requested_is_active,a.expected_version,a.previous_is_active,a.previous_version,a.result_is_active,a.result_version,a.previous_operation_sequence,a.result_operation_sequence,a.outcome,a.accepted_at_utc)", c);
        Assert.Equal(receipts, (long)(await count.ExecuteScalarAsync())!);
        await using var totals = new NpgsqlCommand("SELECT (SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts)+(SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_audit)", c);
        Assert.Equal(receipts * 2, (long)(await totals.ExecuteScalarAsync())!);
    }

    [MapCapturePostgresFact]
    public Task SubmissionLifecycleLockSerializesDeactivationAndPreservesCommittedRetry() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var token = deadline.Token;
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            await using var read = factory.CreateDbContext();
            var attraction = await read.MapAttractions.SingleAsync();
            var later = await PrepareAsync(factory, attraction, prepared.Contribution.OwnerHobbiesModuleProfileId);
            var gate = new InsertGate();
            var auth = new TrustedSubmissionAuthorization(prepared.Contribution.OwnerHobbiesModuleProfileId);
            var submit = new MapContributionSubmissionWriter(new Contexts(connection, gate), auth).SubmitAsync(prepared, token);
            var (lifecycle, admin) = await LifecycleWriterAsync(connection);
            Task<MapAttractionLifecycleResult>? deactivate = null;
            try
            {
                var holder = await gate.Entered.Task.WaitAsync(token);
                deactivate = lifecycle.ChangeAsync(new(Guid.NewGuid(), attraction.AttractionId, false, 0), token);
                await BlockedByAsync(connection, holder, await admin.Entered.Task.WaitAsync(token), deactivate, token);
                gate.Release.TrySetResult();
                Assert.Equal(MapSubmissionOutcome.Created, (await submit.WaitAsync(token)).Outcome);
                Assert.Equal(MapAttractionLifecycleOutcome.Changed, (await deactivate.WaitAsync(token)).Outcome);
                var writer = new MapContributionSubmissionWriter(factory, auth);
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, prepared.Contribution.ContributionId), await writer.SubmitAsync(prepared, token));
                Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await writer.SubmitAsync(later, token));
                await AssertLifecycleStoredAsync(connection, false, 1, 1, 1);
            }
            finally { gate.Release.TrySetResult(); await submit; if (deactivate is not null) await deactivate; }
        });

    [MapCapturePostgresFact]
    public Task DeactivationFirstRejectsWaitingSubmission() =>
        LifecycleFirstAsync(false, false);

    [MapCapturePostgresFact]
    public Task RolledBackDeactivationReleasesLifecycleLockForSubmission() =>
        LifecycleFirstAsync(true, false);

    [MapCapturePostgresFact]
    public Task CancellationWhileWaitingForLifecycleLockDoesNotWrite() =>
        LifecycleFirstAsync(false, true);

    private static Task LifecycleFirstAsync(bool rollback, bool cancel) => MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = deadline.Token;
        var prepared = await PrepareAsync(new(connection));
        var gate = new CommitGate(rollback);
        var (lifecycle, _) = await LifecycleWriterAsync(connection, gate);
        var deactivate = lifecycle.ChangeAsync(new(Guid.NewGuid(), prepared.Contribution.AttractionId, false, 0), token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<MapSubmissionResult>? submit = null;
        try
        {
            var holder = await gate.Entered.Task.WaitAsync(token);
            var probe = new SessionLockProbe();
            submit = new MapContributionSubmissionWriter(new Contexts(connection, probe), new TrustedSubmissionAuthorization(prepared.Contribution.OwnerHobbiesModuleProfileId)).SubmitAsync(prepared, canceled.Token);
            await BlockedByAsync(connection, holder, await probe.Attempted.Task.WaitAsync(token), submit, token);
            if (cancel)
            {
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
            }
            gate.Release.TrySetResult();
            Assert.Equal(rollback ? MapAttractionLifecycleOutcome.OutcomeUnknown : MapAttractionLifecycleOutcome.Changed, (await deactivate.WaitAsync(token)).Outcome);
            if (!cancel) Assert.Equal(rollback ? MapSubmissionOutcome.Created : MapSubmissionOutcome.Denied, (await submit.WaitAsync(token)).Outcome);
            await AssertLifecycleStoredAsync(connection, rollback, rollback ? 0 : 1, rollback ? 0 : 1, rollback && !cancel ? 1 : 0);
        }
        finally
        {
            gate.Release.TrySetResult(); await deactivate;
            if (submit is not null) { try { await submit; } catch (OperationCanceledException) when (cancel) { } }
        }
    });

    [MapCapturePostgresFact]
    public Task RolledBackSubmissionReleasesLifecycleLockForDeactivation() =>
        SubmissionRollbackAsync(false);

    [MapCapturePostgresFact]
    public Task CancellationAfterInsertRollsBackBeforeDeactivation() =>
        SubmissionRollbackAsync(true);

    private static Task SubmissionRollbackAsync(bool cancel) => MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var token = deadline.Token;
        var prepared = await PrepareAsync(new(connection));
        var gate = new InsertGate(!cancel);
        var submit = new MapContributionSubmissionWriter(new Contexts(connection, gate), new TrustedSubmissionAuthorization(prepared.Contribution.OwnerHobbiesModuleProfileId)).SubmitAsync(prepared, canceled.Token);
        var (lifecycle, admin) = await LifecycleWriterAsync(connection);
        Task<MapAttractionLifecycleResult>? deactivate = null;
        try
        {
            var holder = await gate.Entered.Task.WaitAsync(token);
            deactivate = lifecycle.ChangeAsync(new(Guid.NewGuid(), prepared.Contribution.AttractionId, false, 0), token);
            await BlockedByAsync(connection, holder, await admin.Entered.Task.WaitAsync(token), deactivate, token);
            if (cancel) canceled.Cancel();
            gate.Release.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit);
            else Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), await submit.WaitAsync(token));
            Assert.Equal(MapAttractionLifecycleOutcome.Changed, (await deactivate.WaitAsync(token)).Outcome);
            await AssertLifecycleStoredAsync(connection, false, 1, 1, 0);
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await submit; } catch (OperationCanceledException) when (cancel) { }
            if (deactivate is not null) await deactivate;
        }
    });

    [MapCapturePostgresFact]
    public Task AmbiguousSubmissionCommitRetainsSinglePendingRecordAcrossDeactivation() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var prepared = await PrepareAsync(new(connection));
            var auth = new TrustedSubmissionAuthorization(prepared.Contribution.OwnerHobbiesModuleProfileId);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.OutcomeUnknown), await new MapContributionSubmissionWriter(new Contexts(connection, new LoseAcknowledgement()), auth).SubmitAsync(prepared));
            var (lifecycle, _) = await LifecycleWriterAsync(connection);
            Assert.Equal(MapAttractionLifecycleOutcome.Changed, (await lifecycle.ChangeAsync(new(Guid.NewGuid(), prepared.Contribution.AttractionId, false, 0))).Outcome);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.ExistingAuthorized, prepared.Contribution.ContributionId), await new MapContributionSubmissionWriter(new Contexts(connection), auth).SubmitAsync(prepared));
            await AssertLifecycleStoredAsync(connection, false, 1, 1, 1);
        });

    [MapCapturePostgresFact]
    public Task MissingLifecycleEvidenceAndUnavailableAuthorizationFailClosedWithoutProvisioning() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var owner = prepared.Contribution.OwnerHobbiesModuleProfileId;
            await using var context = factory.CreateDbContext();
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), await new MapContributionSubmissionWriter(factory, new TrustedSubmissionAuthorization(owner) { Unavailable = true }).SubmitAsync(prepared));
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await new MapContributionSubmissionWriter(factory, new TrustedSubmissionAuthorization(Guid.NewGuid())).SubmitAsync(prepared));
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_hobbies.map_attraction_lifecycle WHERE attraction_id={prepared.Contribution.AttractionId}");
            var writer = new MapContributionSubmissionWriter(factory, new TrustedSubmissionAuthorization(owner));
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await writer.SubmitAsync(prepared));
            Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM andril_hobbies.map_attraction_lifecycle").SingleAsync());
            await context.Database.ExecuteSqlRawAsync("DROP TABLE andril_hobbies.map_attraction_lifecycle");
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.InfrastructureFailure), await writer.SubmitAsync(prepared));
            Assert.Empty(await context.MapContributions.ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task RevokedAndForeignOwnersCannotWriteOrDiscloseCommittedRetry() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            var other = await PrepareAsync(factory);
            var owner = prepared.Contribution.OwnerHobbiesModuleProfileId;
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), await new MapContributionSubmissionWriter(factory, new TrustedSubmissionAuthorization(other.Contribution.OwnerHobbiesModuleProfileId)).SubmitAsync(prepared));
            var writer = new MapContributionSubmissionWriter(factory, new TrustedSubmissionAuthorization(owner));
            Assert.Equal(MapSubmissionOutcome.Created, (await writer.SubmitAsync(prepared)).Outcome);
            await using var context = factory.CreateDbContext();
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.m3_submission_authorization SET permitted=false WHERE profile_id={owner}");
            var denied = await writer.SubmitAsync(prepared);
            Assert.Equal(new MapSubmissionResult(MapSubmissionOutcome.Denied), denied);
            Assert.Null(denied.ContributionId);
            Assert.Single(await context.MapContributions.ToListAsync());
            Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
        });

    [MapCapturePostgresFact]
    public Task LifecycleLockPrecedesCaptureSessionLockWithoutDeadlock() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var token = deadline.Token;
            var factory = new Contexts(connection);
            var prepared = await PrepareAsync(factory);
            await using var blocker = factory.CreateDbContext();
            await using var transaction = await blocker.Database.BeginTransactionAsync(token);
            await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT session_id FROM andril_hobbies.map_contribution_capture_sessions WHERE session_id={prepared.Contribution.CaptureSessionId} FOR UPDATE", token);
            var probe = new SessionLockProbe();
            var submit = new MapContributionSubmissionWriter(new Contexts(connection, probe), new TrustedSubmissionAuthorization(prepared.Contribution.OwnerHobbiesModuleProfileId)).SubmitAsync(prepared, token);
            var (lifecycle, admin) = await LifecycleWriterAsync(connection);
            Task<MapAttractionLifecycleResult>? deactivate = null;
            var released = false;
            try
            {
                var submissionPid = await probe.Attempted.Task.WaitAsync(token);
                await BlockedByAsync(connection, ((NpgsqlConnection)blocker.Database.GetDbConnection()).ProcessID, submissionPid, submit, token);
                deactivate = lifecycle.ChangeAsync(new(Guid.NewGuid(), prepared.Contribution.AttractionId, false, 0), token);
                await BlockedByAsync(connection, submissionPid, await admin.Entered.Task.WaitAsync(token), deactivate, token);
                await transaction.RollbackAsync(token); released = true;
                Assert.Equal(MapSubmissionOutcome.Created, (await submit.WaitAsync(token)).Outcome);
                Assert.Equal(MapAttractionLifecycleOutcome.Changed, (await deactivate.WaitAsync(token)).Outcome);
                await AssertLifecycleStoredAsync(connection, false, 1, 1, 1);
            }
            finally { if (!released) await transaction.RollbackAsync(); await submit; if (deactivate is not null) await deactivate; }
        });
}
