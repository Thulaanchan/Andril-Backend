using System.Data.Common;
using System.Transactions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;
namespace Andril.IntegrationTests.Database;

public sealed partial class MapAttractionLifecyclePersistenceTests
{
    [Fact]
    public void MappingsAreUnregisteredAndProductionSnapshotRemainsUnchanged()
    {
        using var production=new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql("Host=127.0.0.1;Database=model_only;Enlist=false").Options);
        Assert.Null(production.Model.FindEntityType(typeof(MapAttractionLifecycleRow)));
        Assert.Null(production.Model.FindEntityType(typeof(MapAttractionLifecycleReceiptRow)));
        Assert.Null(production.Model.FindEntityType(typeof(MapAttractionLifecycleAuditRow)));
        Assert.False(production.Database.HasPendingModelChanges());
        using var test=new MappingContext(new DbContextOptionsBuilder<MappingContext>().UseNpgsql("Host=127.0.0.1;Database=model_only;Enlist=false").Options);
        var state=test.Model.FindEntityType(typeof(MapAttractionLifecycleRow))!;
        Assert.Equal("andril_hobbies",state.GetSchema());
        Assert.True(state.FindProperty("Version")!.IsConcurrencyToken);
        Assert.True(state.FindProperty("OperationSequence")!.IsConcurrencyToken);
        Assert.True(state.FindProperty("LastReceiptId")!.IsNullable);
        Assert.Equal(14,test.Model.FindEntityType(typeof(MapAttractionLifecycleAuditRow))!.GetProperties().Count());
        Assert.All(test.Model.GetEntityTypes().SelectMany(x=>x.GetForeignKeys()),fk=>Assert.Equal(DeleteBehavior.NoAction,fk.DeleteBehavior));
    }

    [Fact]
    public async Task AmbientTransactionsAndPreCancellationNeverCreateAContext()
    {
        var factory=new Factory("Host=127.0.0.1;Database=model_only;Enlist=false");var user=new Subject();var auth=new Authorization(user);
        var writer=new MapAttractionLifecycleWriter(factory,auth,user);var command=new MapAttractionLifecycleChange(Guid.NewGuid(),Guid.NewGuid(),true,0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>writer.ChangeAsync(command,new(true)));
        using(var scope=new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            await Assert.ThrowsAsync<InvalidOperationException>(()=>writer.ChangeAsync(command));
        Assert.Equal(0,factory.Calls);Assert.Equal(0,auth.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(()=>writer.ChangeAsync(null!));
    }

    [Fact]
    public async Task UnsafeConnectionOptionsAndUnauthenticatedIdentityFailClosedOffline()
    {
        var user=new Subject();var auth=new Authorization(user);
        foreach(var option in new[]{"Enlist=true","Enlist=false;Multiplexing=true","Enlist=false;No Reset On Close=true"})
        {
            var writer=new MapAttractionLifecycleWriter(new Factory("Host=127.0.0.1;Database=model_only;"+option),auth,user);
            Safe(await writer.ChangeAsync(new(Guid.NewGuid(),Guid.NewGuid(),true,0)),MapAttractionLifecycleOutcome.Unavailable);
        }
        user.IsAuthenticated=false;var factory=new Factory("Host=127.0.0.1;Database=model_only;Enlist=false");
        Safe(await new MapAttractionLifecycleWriter(factory,auth,user).ChangeAsync(new(Guid.NewGuid(),Guid.NewGuid(),true,0)),MapAttractionLifecycleOutcome.Denied);
        Assert.Equal(0,factory.Calls);Assert.Equal(0,auth.Calls);
    }

    [MapCapturePostgresFact]
    public Task ChangesNoOpsAndExactRetriesPreserveVersionsSequencesAndOriginalEvidence()=>WithFixture(async f=>
    {
        var command=f.Request();var first=await f.Writer.ChangeAsync(command);
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,first.Outcome);await f.Stored(false,1,1,1);
        var original=await f.Scalar("SELECT extract(epoch FROM accepted_at_utc)::bigint FROM andril_hobbies.map_attraction_lifecycle_receipts");
        Assert.Equal(first with{Outcome=MapAttractionLifecycleOutcome.ExistingAuthorized},await f.Writer.ChangeAsync(command));
        Assert.Equal(MapAttractionLifecycleOutcome.Unchanged,(await f.Writer.ChangeAsync(f.Request(false,1))).Outcome);
        await f.Stored(false,1,2,2);
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request(true,1))).Outcome);
        Assert.Equal(first with{Outcome=MapAttractionLifecycleOutcome.ExistingAuthorized},await f.Writer.ChangeAsync(command));
        Assert.Equal(original,await f.Scalar("SELECT min(extract(epoch FROM accepted_at_utc))::bigint FROM andril_hobbies.map_attraction_lifecycle_receipts"));
        await f.Stored(true,2,3,3);
    });

    [MapCapturePostgresFact]
    public Task ChangedRetryPayloadAndStaleVersionsCannotMutateState()=>WithFixture(async f=>
    {
        var command=f.Request();await f.Writer.ChangeAsync(command);
        foreach(var retry in new[]{new MapAttractionLifecycleChange(command.RequestId,f.Attraction,true,0),new(command.RequestId,f.Attraction,false,1),new(command.RequestId,f.OtherAttraction,false,0),f.Request(false,0),f.Request(false,2)})
            Safe(await f.Writer.ChangeAsync(retry),MapAttractionLifecycleOutcome.Conflict);
        await f.Stored(false,1,1,1);
    });

    [MapCapturePostgresFact]
    public Task MissingStateAndMissingSchemaAreNeverProvisionedByTheWriter()=>WithFixture(async f=>
    {
        await f.Execute("DELETE FROM andril_hobbies.map_attraction_lifecycle");
        Safe(await f.Writer.ChangeAsync(f.Request(true)),MapAttractionLifecycleOutcome.Denied);
        Assert.Equal(0L,await f.Scalar("SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle"));
        await f.Execute("DROP TABLE andril_hobbies.map_attraction_lifecycle");
        Safe(await f.Writer.ChangeAsync(f.Request(true)),MapAttractionLifecycleOutcome.Unavailable);
        Assert.Equal(0L,await f.Scalar("SELECT count(*) FROM pg_class WHERE relnamespace='andril_hobbies'::regnamespace AND relname='map_attraction_lifecycle'"));
    });

    [MapCapturePostgresFact]
    public Task RevokedAndMalformedAuthorizationCannotCreateOrRevealReceipts()=>WithFixture(async f=>
    {
        var command=f.Request();await f.Writer.ChangeAsync(command);
        await f.Execute("UPDATE m3_lifecycle_adapter_authorization SET permitted=false");
        Safe(await f.Writer.ChangeAsync(command),MapAttractionLifecycleOutcome.Denied);
        await f.Execute("UPDATE m3_lifecycle_adapter_authorization SET permitted=true");
        foreach(var fault in new[]{"subject","attraction","action","missing","unknown"})
        {
            f.Auth.Transform=result=>fault switch {
                "subject"=>result with{Grant=result.Grant! with{ReviewerSubjectId=Guid.NewGuid()}},
                "attraction"=>result with{Grant=result.Grant! with{AttractionId=Guid.NewGuid()}},
                "action"=>result with{Grant=result.Grant! with{RequestedIsActive=!result.Grant!.RequestedIsActive}},
                "missing"=>result with{Grant=null},_=>result with{Outcome=(MapAttractionManagementAuthorizationOutcome)99}};
            Safe(await f.Writer.ChangeAsync(command),MapAttractionLifecycleOutcome.Unavailable);
        }
        await f.Stored(false,1,1,1);
    });

    [MapCapturePostgresFact]
    public Task ActorChangeDuringAuthorizationDeniesTheWholeTransaction()=>WithFixture(async f=>
    {
        f.Auth.AfterFence=()=>f.User.UserId=Guid.NewGuid();
        Safe(await f.Writer.ChangeAsync(f.Request()),MapAttractionLifecycleOutcome.Denied);
        await f.Stored(true,0,0,0);
    });

    [MapCapturePostgresFact]
    public Task ConcurrentChangesHaveOneWinnerAndConcurrentExactRetriesReconcile()=>WithFixture(async f=>
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var command=f.Request();
        var results=await Task.WhenAll(f.Writer.ChangeAsync(command,deadline.Token),f.Writer.ChangeAsync(command,deadline.Token));
        Assert.Single(results,r=>r.Outcome==MapAttractionLifecycleOutcome.Changed);
        Assert.Single(results,r=>r.Outcome==MapAttractionLifecycleOutcome.ExistingAuthorized);
        var changed=await Task.WhenAll(f.Writer.ChangeAsync(f.Request(true,1),deadline.Token),f.Writer.ChangeAsync(f.Request(true,1),deadline.Token));
        Assert.Single(changed,r=>r.Outcome==MapAttractionLifecycleOutcome.Changed);
        Assert.Single(changed,r=>r.Outcome==MapAttractionLifecycleOutcome.Conflict);
        await f.Stored(true,2,2,2);
    });

    [MapCapturePostgresFact]
    public Task IdentityFenceWaitCancellationRollsBackAndAllowsTheNextRequest()=>WithFixture(async f=>
    {
        await using var holder=await f.Open();await using var tx=await holder.BeginTransactionAsync();
        await using(var cmd=Command(holder,tx,"SELECT subject_id FROM m3_lifecycle_adapter_authorization FOR UPDATE"))await cmd.ExecuteScalarAsync();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending=f.Writer.ChangeAsync(f.Request(),cancel.Token);
        var pid=await f.Auth.Entered.Task.WaitAsync(cancel.Token);
        await f.Blocked(holder.ProcessID,pid,pending,cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        await tx.RollbackAsync();await f.Stored(true,0,0,0);
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request())).Outcome);
    });

    [MapCapturePostgresFact]
    public Task CancellationAfterReceiptAndAuditInsertsRollsBackEverything()=>WithFixture(async f=>
    {
        await f.Execute("""
            CREATE FUNCTION public.pause_lifecycle_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(962641); RETURN NEW; END $$;
            CREATE TRIGGER pause_lifecycle_update BEFORE UPDATE ON andril_hobbies.map_attraction_lifecycle FOR EACH ROW EXECUTE FUNCTION public.pause_lifecycle_update();
            """);
        await using var holder=await f.Open();
        await using(var cmd=new NpgsqlCommand("SELECT pg_advisory_lock(962641)",holder))await cmd.ExecuteScalarAsync();
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending=f.Writer.ChangeAsync(f.Request(),cancel.Token);
        var pid=await f.Auth.Entered.Task.WaitAsync(cancel.Token);
        try {await f.Blocked(holder.ProcessID,pid,pending,cancel.Token);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);}
        finally {await using var cmd=new NpgsqlCommand("SELECT pg_advisory_unlock(962641)",holder);await cmd.ExecuteScalarAsync();}
        await f.Stored(true,0,0,0);
        await f.Execute("DROP TRIGGER pause_lifecycle_update ON andril_hobbies.map_attraction_lifecycle");
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request())).Outcome);
    });

    [MapCapturePostgresFact]
    public Task DatabaseFailureBeforeCommitRollsBackStateReceiptAndAudit()=>WithFixture(async f=>
    {
        await f.Execute("""
            CREATE FUNCTION public.fail_lifecycle_update() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Synthetic private failure'; END $$;
            CREATE TRIGGER fail_lifecycle_update BEFORE UPDATE ON andril_hobbies.map_attraction_lifecycle FOR EACH ROW EXECUTE FUNCTION public.fail_lifecycle_update();
            """);
        Safe(await f.Writer.ChangeAsync(f.Request()),MapAttractionLifecycleOutcome.Unavailable);await f.Stored(true,0,0,0);
        await f.Execute("DROP TRIGGER fail_lifecycle_update ON andril_hobbies.map_attraction_lifecycle");
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request())).Outcome);
    });

    [MapCapturePostgresFact]
    public Task LostCommitAcknowledgementUsesOriginalCommittedEvidenceOnExplicitRetry()=>WithFixture(async f=>
    {
        f.Factory.Interceptors=[new CompletionInterceptor(()=>throw new NpgsqlException("Synthetic private lost acknowledgement"))];
        var command=f.Request();Safe(await f.Writer.ChangeAsync(command),MapAttractionLifecycleOutcome.OutcomeUnknown);
        await f.Stored(false,1,1,1);f.Factory.Interceptors=[];
        var replay=await f.Writer.ChangeAsync(command);Assert.Equal(MapAttractionLifecycleOutcome.ExistingAuthorized,replay.Outcome);Assert.Equal(1,replay.Version);
        await f.Stored(false,1,1,1);
    });

    [MapCapturePostgresFact]
    public Task LateCancellationCannotReplaceAnAcknowledgedCommit()=>WithFixture(async f=>
    {
        using var cancel=new CancellationTokenSource();f.Factory.Interceptors=[new CompletionInterceptor(cancel.Cancel)];
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request(),cancel.Token)).Outcome);
        await f.Stored(false,1,1,1);
    });

    [MapCapturePostgresFact]
    public Task StateCannotChangeWithoutMatchingAcceptedReceipt()=>WithFixture(async f=>
    {
        var error=await Assert.ThrowsAsync<PostgresException>(()=>f.Execute("UPDATE andril_hobbies.map_attraction_lifecycle SET is_active=false,version=1,operation_sequence=1"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);Assert.Equal("ck_map_lifecycle_update_link",error.ConstraintName);await f.Stored(true,0,0,0);
    });

    [MapCapturePostgresFact]
    public Task ReceiptAndAuditCannotCommitWithoutApplyingTheState()=>WithFixture(async f=>
    {
        var error=await Assert.ThrowsAsync<PostgresException>(()=>f.ManualReceipt("unapplied"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);Assert.Equal("ck_map_lifecycle_receipt_applied",error.ConstraintName);await f.Stored(true,0,0,0);
    });

    [MapCapturePostgresFact]
    public Task MissingOrContradictoryAuditEvidenceFailsTheNamedDeferredConstraint()=>WithFixture(async f=>
    {
        foreach(var fault in new[]{"missing","timestamp","reviewer"})
        {var error=await Assert.ThrowsAsync<PostgresException>(()=>f.ManualReceipt(fault));Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);Assert.Equal("fk_map_lifecycle_receipt_audit",error.ConstraintName);await f.Stored(true,0,0,0);}
    });

    [MapCapturePostgresFact]
    public Task AcceptedEvidenceIsImmutableAndHistoricalNoOpCannotBeReapplied()=>WithFixture(async f=>
    {
        await f.Writer.ChangeAsync(f.Request(true));await f.Writer.ChangeAsync(f.Request(true));
        foreach(var sql in new[]{"UPDATE andril_hobbies.map_attraction_lifecycle_receipts SET result_version=1","DELETE FROM andril_hobbies.map_attraction_lifecycle_receipts","UPDATE andril_hobbies.map_attraction_lifecycle_audit SET accepted_at_utc=clock_timestamp()","DELETE FROM andril_hobbies.map_attraction_lifecycle_audit"})
        {var error=await Assert.ThrowsAsync<PostgresException>(()=>f.Execute(sql));Assert.Equal("ck_map_lifecycle_evidence_immutable",error.ConstraintName);}
        var replay=await Assert.ThrowsAsync<PostgresException>(()=>f.Execute("UPDATE andril_hobbies.map_attraction_lifecycle s SET last_receipt_id=r.receipt_id,operation_sequence=r.result_operation_sequence FROM andril_hobbies.map_attraction_lifecycle_receipts r WHERE s.attraction_id=r.attraction_id AND r.result_operation_sequence=1"));
        Assert.Equal("ck_map_lifecycle_update_link",replay.ConstraintName);await f.Stored(true,0,2,2);
    });

    [MapCapturePostgresFact]
    public Task CatalogAndUnregisteredMappingsMatchTheAcceptedStorageContract()=>WithFixture(async f=>
    {
        Assert.Equal(3L,await f.Scalar("SELECT count(*) FROM pg_constraint WHERE connamespace='andril_hobbies'::regnamespace AND conname IN ('fk_map_lifecycle_current_receipt','fk_map_lifecycle_receipt_audit','fk_map_lifecycle_audit_receipt') AND convalidated AND condeferrable AND condeferred AND confdeltype='a'"));
        Assert.Equal(2L,await f.Scalar("SELECT count(*) FROM pg_constraint WHERE connamespace='andril_hobbies'::regnamespace AND conname IN ('ak_map_lifecycle_audit_evidence','ak_map_lifecycle_receipt_result') AND convalidated AND NOT condeferrable"));
        using var model=new MappingContext(new DbContextOptionsBuilder<MappingContext>().UseNpgsql("Host=127.0.0.1;Database=model_only").Options);
        await using var c=await f.Open();
        foreach(var type in new[]{typeof(MapAttractionLifecycleRow),typeof(MapAttractionLifecycleReceiptRow),typeof(MapAttractionLifecycleAuditRow)})
        {
            var entity=model.Model.FindEntityType(type)!;var table=StoreObjectIdentifier.Table(entity.GetTableName()!,entity.GetSchema());
            await using var cmd=Command(c,null,"SELECT column_name,is_nullable FROM information_schema.columns WHERE table_schema=$1 AND table_name=$2",entity.GetSchema()!,entity.GetTableName()!);
            await using var reader=await cmd.ExecuteReaderAsync();var columns=new Dictionary<string,bool>();while(await reader.ReadAsync())columns.Add(reader.GetString(0),reader.GetString(1)=="YES");
            Assert.Equal(entity.GetProperties().Count(),columns.Count);foreach(var prop in entity.GetProperties())Assert.Equal(prop.IsNullable,columns[prop.GetColumnName(table)!]);
        }
    });

    [MapCapturePostgresFact]
    public Task AuditInsertFailureRollsBackTheReceiptBeforeStateMutation()=>WithFixture(async f=>
    {
        await f.Execute("ALTER TABLE andril_hobbies.map_attraction_lifecycle_audit RENAME TO temporarily_unavailable_audit");
        Safe(await f.Writer.ChangeAsync(f.Request()),MapAttractionLifecycleOutcome.Unavailable);
        Assert.Equal(0L,await f.Scalar("SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts"));
        await f.Execute("ALTER TABLE andril_hobbies.temporarily_unavailable_audit RENAME TO map_attraction_lifecycle_audit");
        await f.Stored(true,0,0,0);
        Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await f.Writer.ChangeAsync(f.Request())).Outcome);
    });

    [MapCapturePostgresFact]
    public Task EfStagedSavesPreserveDeferredReceiptAuditAndStateRelationships()=>WithFixture(async f=>
    {
        await using var connection=await f.Open();await using var transaction=await connection.BeginTransactionAsync();
        await using var context=new MappingContext(new DbContextOptionsBuilder<MappingContext>().UseNpgsql(connection).Options);
        await context.Database.UseTransactionAsync(transaction);
        DateTimeOffset accepted;
        await using(var clock=new NpgsqlCommand("SELECT clock_timestamp()",connection,transaction))
            accepted=(DateTimeOffset)(await clock.ExecuteScalarAsync() is DateTime time?new DateTimeOffset(time):throw new InvalidOperationException("Database timestamp missing"));
        var receipt=new MapAttractionLifecycleReceiptRow{ReceiptId=Guid.NewGuid(),ReviewerSubjectId=f.User.UserId!.Value,RequestId=Guid.NewGuid(),AttractionId=f.Attraction,
            RequestedIsActive=false,ExpectedVersion=0,PreviousIsActive=true,PreviousVersion=0,ResultIsActive=false,ResultVersion=1,
            PreviousOperationSequence=0,ResultOperationSequence=1,Outcome=1,AcceptedAtUtc=accepted};
        context.Add(receipt);await context.SaveChangesAsync();
        var audit=new MapAttractionLifecycleAuditRow{ReceiptId=receipt.ReceiptId,ReviewerSubjectId=receipt.ReviewerSubjectId,RequestId=receipt.RequestId,AttractionId=receipt.AttractionId,
            RequestedIsActive=receipt.RequestedIsActive,ExpectedVersion=receipt.ExpectedVersion,PreviousIsActive=receipt.PreviousIsActive,PreviousVersion=receipt.PreviousVersion,
            ResultIsActive=receipt.ResultIsActive,ResultVersion=receipt.ResultVersion,PreviousOperationSequence=receipt.PreviousOperationSequence,
            ResultOperationSequence=receipt.ResultOperationSequence,Outcome=receipt.Outcome,AcceptedAtUtc=receipt.AcceptedAtUtc};
        context.Add(audit);await context.SaveChangesAsync();
        await using(var update=Command(connection,transaction,"UPDATE andril_hobbies.map_attraction_lifecycle SET is_active=false,version=1,operation_sequence=1,last_receipt_id=$1 WHERE attraction_id=$2",receipt.ReceiptId,f.Attraction))
            Assert.Equal(1,await update.ExecuteNonQueryAsync());
        await transaction.CommitAsync();await f.Stored(false,1,1,1);
    });

    [MapCapturePostgresFact]
    public Task RestrictedDisposableLoginCanUseAdapterButCannotBypassEvidenceOrRunDdl()=>WithFixture(async f=>
    {
        // The isolated maintenance role must have CREATEROLE. No production role is used.
        var role="m3_lifecycle_runtime_"+Guid.NewGuid().ToString("N");
        var password=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var created=false;
        try
        {
            try {await f.Execute("CREATE ROLE \""+role+"\" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT PASSWORD '"+password+"'");created=true;}
            catch {throw new InvalidOperationException("Disposable restricted-role setup requires a role-capable isolated maintenance connection.");}
            await f.Execute("GRANT USAGE ON SCHEMA andril_hobbies TO \""+role+"\"; GRANT SELECT ON andril_hobbies.map_attractions TO \""+role+"\"; GRANT SELECT,UPDATE ON andril_hobbies.map_attraction_lifecycle,public.m3_lifecycle_adapter_authorization TO \""+role+"\"; GRANT SELECT,INSERT ON andril_hobbies.map_attraction_lifecycle_receipts,andril_hobbies.map_attraction_lifecycle_audit TO \""+role+"\"");
            var restricted=f.RestrictedConnection(role,password);
            var writer=new MapAttractionLifecycleWriter(new Factory(restricted),f.Auth,f.User);
            Assert.Equal(MapAttractionLifecycleOutcome.Changed,(await writer.ChangeAsync(f.Request())).Outcome);
            await using(var c=new NpgsqlConnection(restricted))
            {
                await c.OpenAsync();
                foreach(var sql in new[]{"TRUNCATE andril_hobbies.map_attraction_lifecycle_receipts CASCADE","ALTER TABLE andril_hobbies.map_attraction_lifecycle ADD COLUMN forbidden integer","ALTER TABLE andril_hobbies.map_attraction_lifecycle DISABLE TRIGGER ALL","CREATE TABLE andril_hobbies.forbidden(id integer)","UPDATE andril_hobbies.map_attraction_lifecycle_receipts SET result_version=9","SET session_replication_role=replica"})
                {
                    await using var cmd=new NpgsqlCommand(sql,c);
                    var error=await Assert.ThrowsAsync<PostgresException>(()=>cmd.ExecuteNonQueryAsync());Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);
                }
            }
            await f.Stored(false,1,1,1);
        }
        finally {if(created){await f.Execute("REVOKE ALL PRIVILEGES ON andril_hobbies.map_attractions,andril_hobbies.map_attraction_lifecycle,andril_hobbies.map_attraction_lifecycle_receipts,andril_hobbies.map_attraction_lifecycle_audit,public.m3_lifecycle_adapter_authorization FROM \""+role+"\"; REVOKE ALL PRIVILEGES ON SCHEMA andril_hobbies FROM \""+role+"\"");await f.Execute("DROP ROLE \""+role+"\"");}}
    });

    [MapCapturePostgresFact]
    public Task DifferentReviewersBlockOnTheSameStateRowAndHaveOneVersionWinner()=>WithFixture(async f=>
    {
        var other=new Subject();var otherAuth=new Authorization(other);
        await using(var c=await f.Open())await using(var cmd=Command(c,null,"INSERT INTO m3_lifecycle_adapter_authorization VALUES($1,true)",other.UserId!.Value))await cmd.ExecuteNonQueryAsync();
        var otherWriter=new MapAttractionLifecycleWriter(f.Factory,otherAuth,other);
        await using var holder=await f.Open();await using var tx=await holder.BeginTransactionAsync();
        await using(var cmd=Command(holder,tx,"SELECT attraction_id FROM andril_hobbies.map_attraction_lifecycle WHERE attraction_id=$1 FOR UPDATE",f.Attraction))await cmd.ExecuteScalarAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first=f.Writer.ChangeAsync(f.Request(),deadline.Token);var second=otherWriter.ChangeAsync(f.Request(),deadline.Token);
        try
        {
            var a=await f.Auth.Entered.Task.WaitAsync(deadline.Token);var b=await otherAuth.Entered.Task.WaitAsync(deadline.Token);Assert.NotEqual(a,b);
            await f.Blocked(holder.ProcessID,a,first,deadline.Token);await f.Blocked(holder.ProcessID,b,second,deadline.Token);
            await tx.CommitAsync();var results=await Task.WhenAll(first,second);
            Assert.Single(results,r=>r.Outcome==MapAttractionLifecycleOutcome.Changed);Assert.Single(results,r=>r.Outcome==MapAttractionLifecycleOutcome.Conflict);
            await f.Stored(false,1,1,1);
        }
        finally {await tx.DisposeAsync();try{await Task.WhenAll(first,second);}catch(OperationCanceledException) when(deadline.IsCancellationRequested){/* Preserve the original wait/assertion failure during cleanup. */}}
    });

    [MapCapturePostgresFact]
    public Task RevocationFenceDeniesAWaitingCommandAndPreviouslyAcceptedRetry()=>WithFixture(async f=>
    {
        var accepted=f.Request(true);Assert.Equal(MapAttractionLifecycleOutcome.Unchanged,(await f.Writer.ChangeAsync(accepted)).Outcome);
        f.Auth.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var holder=await f.Open();await using var tx=await holder.BeginTransactionAsync();
        await using(var cmd=Command(holder,tx,"UPDATE m3_lifecycle_adapter_authorization SET permitted=false"))await cmd.ExecuteNonQueryAsync();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending=f.Writer.ChangeAsync(f.Request(),deadline.Token);
        try
        {
            var pid=await f.Auth.Entered.Task.WaitAsync(deadline.Token);await f.Blocked(holder.ProcessID,pid,pending,deadline.Token);
            await tx.CommitAsync();Safe(await pending,MapAttractionLifecycleOutcome.Denied);
            Safe(await f.Writer.ChangeAsync(accepted),MapAttractionLifecycleOutcome.Denied);await f.Stored(true,0,1,1);
        }
        finally {await tx.DisposeAsync();await pending;}
    });

    private static void Safe(MapAttractionLifecycleResult result,MapAttractionLifecycleOutcome outcome)
    {Assert.Equal(new MapAttractionLifecycleResult(outcome),result);}

    private sealed class MappingContext(DbContextOptions<MappingContext> options):DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {model.ApplyConfiguration(new MapAttractionConfiguration());model.ApplyConfiguration(new MapAttractionLifecycleConfiguration());model.ApplyConfiguration(new MapAttractionLifecycleReceiptConfiguration());model.ApplyConfiguration(new MapAttractionLifecycleAuditConfiguration());}
    }
}
