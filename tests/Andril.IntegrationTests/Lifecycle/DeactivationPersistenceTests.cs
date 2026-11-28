using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.IntegrationTests.Database;
using Andril.Persistence.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

[Collection("ControlledLifecyclePostgreSQL")]
public sealed class DeactivationPersistenceTests
{
    private const string Previous = "20261123113056_AddControlledModuleLifecycle";
    private const string Current = "20261124130000_AddControlledModuleDeactivation";
    private static ModuleLifecycleCommand Stop(ControlledLifecycleDatabase db, long revision = 1, Guid? key = null) =>
        new("learning", key ?? Guid.NewGuid(), false, revision, ModuleLifecycleOperation.Deactivate, new(db.AccountId, "learning"));
    private static ModuleLifecycleCommand Start(long? revision = null, Guid? key = null, string module = "learning") =>
        new(module, key ?? Guid.NewGuid(), module == "social", revision);
    private static Task<ModuleLifecycleResult> Execute(ControlledLifecycleDatabase db, ModuleLifecycleCommand command,
        IModuleLifecycleStore? store = null, CancellationToken ct = default) =>
        db.Service(store ?? Enabled(db)).ExecuteAsync(db.Subject, command, Guid.NewGuid(), ct);
    private static IModuleLifecycleStore Enabled(ControlledLifecycleDatabase db, IControlledDeactivationAuthority? authority = null)
    {
        var parallel = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = 16 };
        return new ControlledModuleLifecycleStore(parallel.ConnectionString, db.Authority, db.Authority,
            db.Authority, true, authority ?? db.DeactivationAuthority);
    }

    [LocalPostgresFact]
    public async Task RoundTripAndExactReplayPreserveProfileStarterOtherModuleAndFirstActivation()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var activation = await Execute(db, Start());
        var hobbies = await Execute(db, Start(module: "hobbies"));
        await using (var owner = db.Open())
        {
            owner.PrivateProfiles.Add(PrivateModuleProfile.Create(db.AccountId, "learning", activation.StateChangedAtUtc));
            owner.StarterMemberships.Add(StarterMembership.Create(db.AccountId, "learning", false, activation.StateChangedAtUtc));
            await owner.SaveChangesAsync();
        }
        var command = Stop(db);
        var inactive = await Execute(db, command);
        Assert.Equal("inactive", inactive.State);
        Assert.Equal(2, inactive.Revision);
        Assert.True(inactive.StateChangedAtUtc >= activation.StateChangedAtUtc);
        var snapshot = await Enabled(db).ReadAsync(db.Subject, "learning", command.RequestKey, default);
        Assert.IsType<InactiveModuleMembership>(snapshot.Current!.Membership);
        Assert.Equal(activation.StateChangedAtUtc, snapshot.Current.FirstActivatedAtUtc);
        Assert.Equal(inactive, snapshot.Receipt!.Result);
        Assert.Equal(inactive, await Execute(db, command));
        Assert.Equal(3, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(3, await db.Count("account_module_lifecycle_audit"));
        Assert.Equal("active", await Scalar<string>(db, "SELECT previous_state FROM andril_identity.account_module_lifecycle_audit WHERE operation='deactivate'"));
        Assert.Equal("deactivate", await Scalar<string>(db, "SELECT operation FROM andril_identity.account_module_lifecycle_receipts WHERE revision=2"));
        Assert.Equal(hobbies, (await Enabled(db).ReadAsync(db.Subject, "hobbies", Guid.NewGuid(), default)).Current is { } other
            ? new ModuleLifecycleResult("hobbies", other.Membership.Revision, other.StateChangedAtUtc) : null);
        await using var check = db.Open();
        Assert.Equal("private", (await check.PrivateProfiles.SingleAsync()).Visibility);
        Assert.Equal("learning", (await check.StarterMemberships.SingleAsync()).Module);
        Assert.False(check.Database.HasPendingModelChanges());
    }

    [LocalPostgresFact]
    public async Task ChangedIntentAndSupersededReceiptCannotClaimCurrentState()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var command = Stop(db);
        await Execute(db, command);
        var before = await Artifacts(db);
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, command with { ExpectedRevision = 2 }));
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Start(2, command.RequestKey)));
        Assert.Equal(before, await Artifacts(db));
        Assert.Equal(3, (await Execute(db, Start(2))).Revision);
        before = await Artifacts(db);
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, command));
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db)));
        Assert.Equal(before, await Artifacts(db));
        Assert.Equal(3, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task DeactivationAuthorityFailsClosedIndependentlyOfActivationEligibility()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var before = await Artifacts(db);
        foreach (var outcome in new[] { ModuleAuthorizationOutcome.Denied, ModuleAuthorizationOutcome.Unavailable })
        {
            db.DeactivationAuthority.Outcome = outcome;
            await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db)));
            Assert.Equal(before, await Artifacts(db));
        }
        db.DeactivationAuthority.Outcome = ModuleAuthorizationOutcome.Satisfied;
        db.DeactivationAuthority.CurrentVersion = Guid.NewGuid();
        Assert.Equal(ModuleLifecycleDenial.StaleEvidence,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db)))).Denial);
        db.DeactivationAuthority.CurrentVersion = null;
        var absent = db.Store();
        Assert.Equal(ModuleLifecycleDenial.EvidenceUnavailable,
            (await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db), absent))).Denial);
        Assert.Equal(before, await Artifacts(db));
        db.Authority.Outcome = ModuleAuthorizationOutcome.Unavailable;
        db.Authority.Restrictions = false;
        Assert.Equal("inactive", (await Execute(db, Stop(db))).State);
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task ForeignScopeOrSubjectCannotMutateAnyJournal()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var before = await Artifacts(db);
        foreach (var scope in new[] { new ModuleMembershipScope(Guid.NewGuid(), "learning"), new(db.AccountId, "hobbies") })
        {
            db.DeactivationAuthority.ScopeOverride = scope;
            await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db)));
            Assert.Equal(before, await Artifacts(db));
        }
        db.DeactivationAuthority.ScopeOverride = null;
        db.DeactivationAuthority.SubjectOverride = Guid.NewGuid();
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db, Stop(db)));
        db.DeactivationAuthority.SubjectOverride = null;
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(db,
            Stop(db) with { TargetScope = new(Guid.NewGuid(), "learning") }));
        Assert.Equal(before, await Artifacts(db));
    }

    [LocalPostgresFact]
    public async Task MissingInactiveAndOverflowRemainDistinctAndNoOpsPreserveMembership()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        var missing = Stop(db);
        var absent = await Execute(db, missing);
        Assert.Equal("neverActivated", absent.NoTransitionOutcome);
        Assert.Equal(absent, await Execute(db, missing));
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
        Assert.Equal(1, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(0, await db.Count("account_module_lifecycle_audit"));
        await Execute(db, Start());
        var changed = await Execute(db, Stop(db));
        var stop = Stop(db, 2);
        var unchanged = await Execute(db, stop);
        Assert.Equal("alreadyInactive", unchanged.NoTransitionOutcome);
        Assert.Equal(changed.Revision, unchanged.Revision);
        Assert.Equal(changed.StateChangedAtUtc, unchanged.StateChangedAtUtc);
        Assert.Equal(unchanged, await Execute(db, stop));
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
        var before = await Artifacts(db);
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(db, Stop(db, long.MaxValue)));
        Assert.Equal(before, await Artifacts(db));
    }

    [LocalPostgresFact]
    public async Task ConcurrentIdenticalDeactivationHasOneRevisionReceiptAndAudit()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var command = Stop(db);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Execute(db, command)));
        Assert.All(results, value => Assert.Equal(results[0], value));
        Assert.Equal(2, results[0].Revision);
        Assert.Equal(2, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task ConcurrentDifferentKeysHaveOneEffectiveDeactivation()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await Execute(db, Stop(db)); return true; }
            catch (ModuleLifecycleException e) when (e.Denial == ModuleLifecycleDenial.ConflictingTransition) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(x => x));
        Assert.Equal(2, await db.Count("account_module_lifecycle_receipts"));
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task FixtureCleanupTerminatesOnlyItsOwnRuntimeSessionsWithoutPrivilegeExpansion()
    {
        await using var target = await ControlledLifecycleDatabase.CreateAsync();
        await using var unrelated = await ControlledLifecycleDatabase.CreateAsync();
        await using var held = new NpgsqlConnection(target.RuntimeConnection);
        await using var survivor = new NpgsqlConnection(unrelated.RuntimeConnection);
        await held.OpenAsync();
        await survivor.OpenAsync();
        await using (var privileges = new NpgsqlCommand("""
            SELECT rolsuper OR rolbypassrls OR pg_has_role(current_user, 'pg_signal_backend', 'USAGE')
            FROM pg_roles WHERE rolname = current_user
            """, held))
            Assert.False((bool)(await privileges.ExecuteScalarAsync())!);

        await target.DisposeAsync();

        await using (var alive = new NpgsqlCommand("SELECT 1", survivor))
            Assert.Equal(1, (int)(await alive.ExecuteScalarAsync())!);
        await using var admin = new NpgsqlConnection(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES"));
        await admin.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", admin);
        exists.Parameters.AddWithValue("name", new NpgsqlConnectionStringBuilder(target.RuntimeConnection).Database!);
        Assert.False((bool)(await exists.ExecuteScalarAsync())!);
    }

    [LocalPostgresFact]
    public async Task DeactivationAndReactivationSerializeInBothDatabaseLockOrders()
    {
        foreach (var stopFirst in new[] { true, false })
        {
            await using var db = await ControlledLifecycleDatabase.CreateAsync();
            await Execute(db, Start());
            var gate = new GatedAuthority(db.DeactivationAuthority);
            var first = Execute(db, stopFirst ? Stop(db) : Start(2), Enabled(db, gate));
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var second = Execute(db, stopFirst ? Start(2) : Stop(db));
            gate.Release.TrySetResult();
            if (stopFirst)
            {
                Assert.Equal(2, (await first).Revision);
                Assert.Equal(3, (await second).Revision);
                Assert.Equal("active", await Scalar<string>(db, "SELECT state FROM andril_identity.account_module_lifecycles"));
            }
            else
            {
                await Assert.ThrowsAsync<ModuleLifecycleException>(() => first);
                Assert.Equal(2, (await second).Revision);
                Assert.Equal("inactive", await Scalar<string>(db, "SELECT state FROM andril_identity.account_module_lifecycles"));
            }
            Assert.Equal(stopFirst ? 3 : 2, await db.Count("account_module_lifecycle_audit"));
        }
    }

    [LocalPostgresFact]
    public async Task AuditInsertFailureRollsBackMembershipAndReceipt()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        await db.Sql("ALTER TABLE andril_identity.account_module_lifecycle_audit ADD CONSTRAINT b2_fault CHECK(operation <> 'deactivate')");
        var before = await Artifacts(db);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => Execute(db, Stop(db)))).SqlState);
        Assert.Equal(before, await Artifacts(db));
        await db.Sql("ALTER TABLE andril_identity.account_module_lifecycle_audit DROP CONSTRAINT b2_fault");
        Assert.Equal("inactive", (await Execute(db, Stop(db))).State);
    }

    [LocalPostgresFact]
    public async Task LostAcknowledgementReconcilesRealCommittedStateAndUnavailableReadDoesNotRewrite()
    {
        foreach (var unavailable in new[] { false, true })
        {
            await using var db = await ControlledLifecycleDatabase.CreateAsync();
            await Execute(db, Start());
            var command = Stop(db);
            var fault = new CommitFault(Enabled(db), unavailable);
            if (unavailable) await Assert.ThrowsAsync<IOException>(() => Execute(db, command, fault));
            else Assert.Equal("inactive", (await Execute(db, command, fault)).State);
            Assert.Equal(1, fault.Writes);
            Assert.Equal(1, fault.Reads);
            Assert.Equal("inactive", await Scalar<string>(db, "SELECT state FROM andril_identity.account_module_lifecycles"));
            Assert.Equal(2, (await Execute(db, command)).Revision);
            Assert.Equal(2, await db.Count("account_module_lifecycle_receipts"));
            Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
        }
    }

    [LocalPostgresFact]
    public async Task CancellationWhileWaitingForAccountLockLeavesNoTransitionAndPoolContextIsReset()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var before = await Artifacts(db);
        await using (var blocker = new NpgsqlConnection(db.MigrationConnection))
        {
            await blocker.OpenAsync();
            await using var tx = await blocker.BeginTransactionAsync();
            await using var sql = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@subject,0))", blocker);
            sql.Parameters.AddWithValue("subject", db.Subject.ToString());
            await sql.ExecuteNonQueryAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(db, Stop(db), ct: cancellation.Token));
            Assert.Equal(before, await Artifacts(db));
            await tx.RollbackAsync();
        }
        var store = new ControlledModuleLifecycleStore(db.RuntimeConnection, db.Authority, db.Authority,
            db.Authority, true, db.DeactivationAuthority);
        await Execute(db, Stop(db), store);
        await using var reused = new NpgsqlConnection(db.RuntimeConnection);
        await reused.OpenAsync();
        await using var probe = new NpgsqlCommand("SELECT count(*) FROM andril_identity.account_module_lifecycles", reused);
        Assert.Equal(0L, await probe.ExecuteScalarAsync());
        probe.CommandText = "SELECT nullif(current_setting('andril.subject',true),'') IS NULL AND nullif(current_setting('andril.account',true),'') IS NULL AND nullif(current_setting('andril.module',true),'') IS NULL";
        Assert.Equal(true, await probe.ExecuteScalarAsync());
    }

    [LocalPostgresFact]
    public async Task ConnectionTerminationBeforeWriteRollsBackAndFreshConnectionCanRetry()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var before = await Artifacts(db);
        var gate = new GatedAuthority(db.DeactivationAuthority);
        var command = Stop(db);
        var pending = Execute(db, command, Enabled(db, gate));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(db.RuntimeConnection) { MaxPoolSize = 16 };
            await using var peer = new NpgsqlConnection(settings.ConnectionString);
            await peer.OpenAsync();
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user AND pid<>pg_backend_pid() AND state='idle in transaction'", peer);
            Assert.Equal(true, await terminate.ExecuteScalarAsync());
        }
        finally { gate.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => pending);
        Assert.Equal(before, await Artifacts(db));
        Assert.Equal("inactive", (await Execute(db, command)).State);
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
    }

    [LocalPostgresFact]
    public async Task UpgradePreservesHistoricalActiveReceiptsAndDowngradeRefusesDeactivationData()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync(targetMigration: Previous);
        var activation = Start();
        await SeedHistoricalActiveReceipt(db, activation, 1);
        await db.Sql("UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=2");
        var reactivation = Start(2);
        await SeedHistoricalActiveReceipt(db, reactivation, 3);
        var before = await Artifacts(db);
        await using var owner = db.Open();
        await owner.Database.MigrateAsync();
        Assert.Equal(before, await Artifacts(db));
        Assert.False(owner.Database.HasPendingModelChanges());
        foreach (var command in new[] { activation, reactivation })
            Assert.Equal("active", (await Enabled(db).ReadAsync(db.Subject, "learning", command.RequestKey, default)).Receipt!.Result.State);
        Assert.Equal(3, (await Execute(db, reactivation)).Revision);
        await Execute(db, Stop(db, 3));
        before = await Artifacts(db);
        await Assert.ThrowsAsync<PostgresException>(() => owner.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.Equal(before, await Artifacts(db));
        Assert.Contains(Current, await owner.Database.GetAppliedMigrationsAsync());
    }

    private static async Task SeedHistoricalActiveReceipt(ControlledLifecycleDatabase fixture,
        ModuleLifecycleCommand command, long revision)
    {
        // Write only columns present in the historical schema. The current EF receipt
        // mapping includes membership_module, introduced by the later outcomes migration.
        var operation = command.ExpectedRevision is null ? "activate" : "reactivate";
        var intentOperation = command.ExpectedRevision is null ? "Activate" : "Reactivate";
        var expected = command.ExpectedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
        var intent = $"v1\nlearning\n{intentOperation}\n{expected}\nFalse";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(intent)));
        await using var db = fixture.Open();
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        if (command.ExpectedRevision is null)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_module_lifecycles
                    (account_id,module,state,revision,first_activated_at_utc,state_changed_at_utc)
                VALUES ({fixture.AccountId},'learning','active',{revision},{now},{now})
                """);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE andril_identity.account_module_lifecycles
                SET state='active', revision={revision}, state_changed_at_utc={now}
                WHERE account_id={fixture.AccountId} AND module='learning' AND state='inactive'
                    AND revision={command.ExpectedRevision}
                """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_module_lifecycle_receipts
                (account_id,module,request_key,operation,expected_revision,intent_hash,revision,state_changed_at_utc,recorded_at_utc)
            VALUES ({fixture.AccountId},'learning',{command.RequestKey},{operation},{command.ExpectedRevision},{hash},{revision},{now},{now})
            """);
        string? previousState = command.ExpectedRevision is null ? null : "inactive";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_module_lifecycle_audit
                (account_id,module,revision,request_key,operation,previous_state,correlation_id,recorded_at_utc)
            VALUES ({fixture.AccountId},'learning',{revision},{command.RequestKey},{operation},{previousState},{Guid.NewGuid()},{now})
            """);
        await tx.CommitAsync();
    }

    [LocalPostgresFact]
    public async Task EmptyJournalDowngradeAndUpgradePreserveSchemaIsolationAndRls()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await using var owner = db.Open();
        await owner.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.DoesNotContain(Current, await owner.Database.GetAppliedMigrationsAsync());
        await owner.Database.MigrateAsync();
        Assert.False(owner.Database.HasPendingModelChanges());
        Assert.Equal(1, await owner.Accounts.CountAsync());
        Assert.Equal(0, await db.Count("account_module_lifecycles"));
        Assert.Equal(3L, await Scalar<long>(db, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='andril_identity' AND c.relname IN ('account_module_lifecycles','account_module_lifecycle_receipts','account_module_lifecycle_audit') AND c.relrowsecurity AND c.relforcerowsecurity"));
    }

    [LocalPostgresFact]
    public async Task DeactivationConstraintsRejectInvalidOperationsRevisionsAndAuditTransitions()
    {
        await using var db = await ControlledLifecycleDatabase.CreateAsync();
        await Execute(db, Start());
        var before = await Artifacts(db);
        foreach (var values in new[] { "'deactivate',NULL,2", "'deactivate',0,1", "'deactivate',1,3", "'deactivate',9223372036854775807,1", "'unknown',1,2" })
        {
            await using var connection = new NpgsqlConnection(db.MigrationConnection);
            await connection.OpenAsync();
            await using var sql = new NpgsqlCommand($"INSERT INTO andril_identity.account_module_lifecycle_receipts(account_id,module,request_key,operation,expected_revision,revision,intent_hash,state_changed_at_utc) SELECT '{db.AccountId}','learning','{Guid.NewGuid()}',v.operation,v.expected_revision::bigint,v.revision::bigint,repeat('A',64),statement_timestamp() FROM (VALUES ({values})) AS v(operation,expected_revision,revision)", connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => sql.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_lifecycle_receipt_revision", error.ConstraintName);
        }
        var command = Stop(db);
        await Execute(db, command);
        foreach (var previous in new[] { "NULL", "'inactive'" })
        {
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"UPDATE andril_identity.account_module_lifecycle_audit SET previous_state={previous} WHERE operation='deactivate'"))).SqlState);
        }
        Assert.Equal(2, await db.Count("account_module_lifecycle_audit"));
        Assert.NotEqual(before, await Artifacts(db));
    }

    private static async Task<T> Scalar<T>(ControlledLifecycleDatabase db, string statement)
    {
        await using var connection = new NpgsqlConnection(db.MigrationConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task<string> Artifacts(ControlledLifecycleDatabase db) => Scalar<string>(db, """
        SELECT json_build_object(
            'memberships', (SELECT json_agg(t ORDER BY account_id,module) FROM andril_identity.account_module_lifecycles t),
            'receipts', (SELECT json_agg(to_jsonb(t) - 'membership_module' ORDER BY account_id,module,request_key) FROM andril_identity.account_module_lifecycle_receipts t),
            'audits', (SELECT json_agg(t ORDER BY account_id,module,revision) FROM andril_identity.account_module_lifecycle_audit t))::text
        """);

    private sealed class GatedAuthority(IControlledDeactivationAuthority inner) : IControlledDeactivationAuthority
    {
        private int entered;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModuleDeactivationEvidence> ReadAsync(Guid subject, ModuleMembershipScope scope, DateTimeOffset now, CancellationToken ct)
        {
            if (Interlocked.Increment(ref entered) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            return await inner.ReadAsync(subject, scope, now, ct);
        }
    }

    private sealed class CommitFault(IModuleLifecycleStore inner, bool unavailable) : IOfflineModuleDeactivationStore
    {
        internal int Writes, Reads;
        public async Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid key,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken ct)
        {
            Writes++;
            await inner.WriteAsync(subject, module, key, decide, ct);
            throw new ModuleLifecycleCommitUnknownException();
        }
        public Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid key, CancellationToken ct)
        {
            Reads++;
            if (unavailable) throw new IOException("Synthetic reconciliation outage");
            return inner.ReadAsync(subject, module, key, ct);
        }
    }
}
