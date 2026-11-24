using System.Data;
using System.Data.Common;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
namespace Andril.IntegrationTests.Database;

public sealed partial class MapAttractionLifecyclePersistenceTests
{
    // Accepted MAP-36D-01 candidate DDL, exercised only inside authorized disposable databases.
    // It is not a migration, production provisioning mechanism or runtime permission provider.
    private const string SchemaSql = """
        CREATE TABLE andril_hobbies.map_attraction_lifecycle (
         attraction_id uuid CONSTRAINT pk_map_attraction_lifecycle PRIMARY KEY CONSTRAINT fk_map_lifecycle_attraction REFERENCES andril_hobbies.map_attractions(attraction_id) ON DELETE NO ACTION,
         is_active boolean NOT NULL,version bigint NOT NULL,operation_sequence bigint NOT NULL,last_receipt_id uuid,
         CONSTRAINT ck_map_lifecycle_state CHECK (version>=0 AND operation_sequence>=0 AND
         ((operation_sequence=0 AND version=0 AND last_receipt_id IS NULL) OR (operation_sequence>0 AND last_receipt_id IS NOT NULL))));
        CREATE TABLE andril_hobbies.map_attraction_lifecycle_receipts (
         receipt_id uuid CONSTRAINT pk_map_lifecycle_receipts PRIMARY KEY,
         reviewer_subject_id uuid NOT NULL,request_id uuid NOT NULL,attraction_id uuid NOT NULL CONSTRAINT fk_map_lifecycle_receipt_attraction REFERENCES andril_hobbies.map_attractions(attraction_id) ON DELETE NO ACTION,
         requested_is_active boolean NOT NULL,expected_version bigint NOT NULL,previous_is_active boolean NOT NULL,previous_version bigint NOT NULL,
         result_is_active boolean NOT NULL,result_version bigint NOT NULL,previous_operation_sequence bigint NOT NULL,result_operation_sequence bigint NOT NULL,
         outcome smallint NOT NULL,accepted_at_utc timestamp(6) with time zone NOT NULL,
         CONSTRAINT ck_map_lifecycle_receipt_ids CHECK(receipt_id<>'00000000-0000-0000-0000-000000000000'::uuid AND reviewer_subject_id<>'00000000-0000-0000-0000-000000000000'::uuid AND request_id<>'00000000-0000-0000-0000-000000000000'::uuid),
         CONSTRAINT ck_map_lifecycle_receipt_time CHECK(isfinite(accepted_at_utc)),
         CONSTRAINT ck_map_lifecycle_receipt_transition CHECK(expected_version=previous_version AND requested_is_active=result_is_active AND previous_version>=0 AND result_version>=0 AND previous_operation_sequence>=0 AND result_operation_sequence::numeric=previous_operation_sequence::numeric+1 AND
         ((outcome=1 AND previous_is_active<>result_is_active AND result_version::numeric=previous_version::numeric+1) OR (outcome=2 AND previous_is_active=result_is_active AND result_version=previous_version))),
         CONSTRAINT ux_map_lifecycle_reviewer_request UNIQUE(reviewer_subject_id,request_id),
         CONSTRAINT ux_map_lifecycle_operation UNIQUE(attraction_id,result_operation_sequence),
         CONSTRAINT ak_map_lifecycle_receipt_result UNIQUE(receipt_id,attraction_id,result_is_active,result_version,result_operation_sequence));
        CREATE TABLE andril_hobbies.map_attraction_lifecycle_audit (
         receipt_id uuid CONSTRAINT pk_map_lifecycle_audit PRIMARY KEY,reviewer_subject_id uuid NOT NULL,request_id uuid NOT NULL,attraction_id uuid NOT NULL,
         requested_is_active boolean NOT NULL,expected_version bigint NOT NULL,previous_is_active boolean NOT NULL,previous_version bigint NOT NULL,
         result_is_active boolean NOT NULL,result_version bigint NOT NULL,previous_operation_sequence bigint NOT NULL,result_operation_sequence bigint NOT NULL,
         outcome smallint NOT NULL,accepted_at_utc timestamp(6) with time zone NOT NULL,
         CONSTRAINT ak_map_lifecycle_audit_evidence UNIQUE(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc),
         CONSTRAINT fk_map_lifecycle_audit_receipt FOREIGN KEY(receipt_id) REFERENCES andril_hobbies.map_attraction_lifecycle_receipts(receipt_id) ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED);
        ALTER TABLE andril_hobbies.map_attraction_lifecycle_receipts ADD CONSTRAINT fk_map_lifecycle_receipt_audit FOREIGN KEY(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc)
         REFERENCES andril_hobbies.map_attraction_lifecycle_audit(receipt_id,reviewer_subject_id,request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,accepted_at_utc) ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE andril_hobbies.map_attraction_lifecycle ADD CONSTRAINT fk_map_lifecycle_current_receipt
         FOREIGN KEY(last_receipt_id,attraction_id,is_active,version,operation_sequence)
         REFERENCES andril_hobbies.map_attraction_lifecycle_receipts(receipt_id,attraction_id,result_is_active,result_version,result_operation_sequence)
         ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        CREATE FUNCTION andril_hobbies.map_lifecycle_check_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
         IF NEW.attraction_id<>OLD.attraction_id OR NOT EXISTS (
         SELECT 1 FROM andril_hobbies.map_attraction_lifecycle_receipts r
         WHERE r.receipt_id=NEW.last_receipt_id AND r.attraction_id=OLD.attraction_id
         AND ROW(r.previous_is_active,r.previous_version,r.previous_operation_sequence)=ROW(OLD.is_active,OLD.version,OLD.operation_sequence)
         AND ROW(r.result_is_active,r.result_version,r.result_operation_sequence)=ROW(NEW.is_active,NEW.version,NEW.operation_sequence)) THEN
         RAISE EXCEPTION 'Lifecycle update requires matching evidence' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_update_link'; END IF;
         RETURN NEW;
        END $$;
        CREATE TRIGGER map_lifecycle_update_link BEFORE UPDATE ON andril_hobbies.map_attraction_lifecycle FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_update();
        CREATE FUNCTION andril_hobbies.map_lifecycle_check_applied() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
         IF NOT EXISTS(SELECT 1 FROM andril_hobbies.map_attraction_lifecycle s WHERE s.attraction_id=NEW.attraction_id AND s.last_receipt_id=NEW.receipt_id
         AND ROW(s.is_active,s.version,s.operation_sequence)=ROW(NEW.result_is_active,NEW.result_version,NEW.result_operation_sequence)) THEN
         RAISE EXCEPTION 'Lifecycle receipt must be applied' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_receipt_applied'; END IF;
         RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER map_lifecycle_receipt_applied AFTER INSERT ON andril_hobbies.map_attraction_lifecycle_receipts DEFERRABLE INITIALLY DEFERRED
         FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_check_applied();
        CREATE FUNCTION andril_hobbies.map_lifecycle_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Lifecycle evidence is immutable' USING ERRCODE='23514',CONSTRAINT='ck_map_lifecycle_evidence_immutable'; END $$;
        CREATE TRIGGER map_lifecycle_receipt_immutable BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_receipts FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
        CREATE TRIGGER map_lifecycle_audit_immutable BEFORE UPDATE OR DELETE ON andril_hobbies.map_attraction_lifecycle_audit FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_lifecycle_immutable();
        """;

    private static Task WithFixture(Func<Fixture,Task> test,bool schema=true) => MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
    { await using var f=new Fixture(connection); await f.Initialize(schema); await test(f); });

    private sealed class Subject : ICurrentUser
    { public Guid? UserId { get; set; }=Guid.NewGuid(); public bool IsAuthenticated { get; set; }=true; }

    private sealed class Factory(string connection) : IDbContextFactory<HobbiesMapDbContext>
    {
        internal IInterceptor[] Interceptors { get; set; }=[];
        internal int Calls { get; private set; }
        public HobbiesMapDbContext CreateDbContext()
        { Calls++; return new(new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(connection,HobbiesDatabaseOptions.ConfigureMapHistory).AddInterceptors(Interceptors).Options); }
    }

    // No M2 permission implementation: this row is a synthetic test-only transaction fence.
    private sealed class Authorization(Subject user) : IMapAttractionManagementAuthorization
    {
        internal TaskCompletionSource<int> Entered { get; set; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<MapAttractionManagementAuthorizationResult,MapAttractionManagementAuthorizationResult>? Transform { get; set; }
        internal Action? AfterFence { get; set; }
        internal int Calls { get; private set; }
        public async Task<MapAttractionManagementAuthorizationResult> AuthorizeAsync(DbConnection connection,DbTransaction transaction,
            Guid attractionId,bool requestedIsActive,CancellationToken token=default)
        {
            Calls++;
            var c=Assert.IsType<NpgsqlConnection>(connection);
            var t=Assert.IsType<NpgsqlTransaction>(transaction);
            Assert.Same(c,t.Connection);
            Assert.Equal(IsolationLevel.ReadCommitted,t.IsolationLevel);
            Entered.TrySetResult(c.ProcessID);
            await using var cmd=Command(c,t,"SELECT permitted FROM m3_lifecycle_adapter_authorization WHERE subject_id=$1 FOR UPDATE",user.UserId!.Value);
            var allowed=await cmd.ExecuteScalarAsync(token) is true;
            var result=new MapAttractionManagementAuthorizationResult(allowed?MapAttractionManagementAuthorizationOutcome.Satisfied:MapAttractionManagementAuthorizationOutcome.Denied,
                allowed?new(user.UserId.Value,attractionId,requestedIsActive):null);
            AfterFence?.Invoke();
            return Transform is null?result:Transform(result);
        }
    }

    private sealed class Fixture(string connection) : IAsyncDisposable
    {
        internal Subject User { get; }=new();
        internal Guid Attraction { get; }=Guid.NewGuid();
        internal Guid OtherAttraction { get; }=Guid.NewGuid();
        internal Factory Factory { get; }=new(connection);
        internal Authorization Auth { get; private set; }=null!;
        internal MapAttractionLifecycleWriter Writer=>new(Factory,Auth,User);
        internal MapAttractionLifecycleChange Request(bool active=false,long expected=0)=>new(Guid.NewGuid(),Attraction,active,expected);
        internal async Task Initialize(bool schema)
        {
            Auth=new(User);
            await using(var context=Factory.CreateDbContext())
            { context.MapAttractions.AddRange(MapAttraction.Create(Attraction,"Synthetic lifecycle",0,0),MapAttraction.Create(OtherAttraction,"Synthetic second",0,0)); await context.SaveChangesAsync(); }
            await Execute("CREATE TABLE m3_lifecycle_adapter_authorization(subject_id uuid PRIMARY KEY,permitted boolean NOT NULL)");
            await using(var c=await Open()) await using(var cmd=Command(c,null,"INSERT INTO m3_lifecycle_adapter_authorization VALUES($1,true)",User.UserId!.Value)) await cmd.ExecuteNonQueryAsync();
            if(schema)
            {
                await Execute(SchemaSql);
                await using var c=await Open();
                await using var cmd=Command(c,null,"INSERT INTO andril_hobbies.map_attraction_lifecycle VALUES($1,true,0,0,NULL),($2,true,0,0,NULL)",Attraction,OtherAttraction);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        internal string RestrictedConnection(string role,string password)=>new NpgsqlConnectionStringBuilder(connection){Username=role,Password=password,Pooling=false}.ConnectionString;
        internal async Task<NpgsqlConnection> Open()
        { var c=new NpgsqlConnection(connection); try{await c.OpenAsync();return c;}catch{await c.DisposeAsync();throw;} }
        internal async Task ManualReceipt(string fault)
        {
            await using var c=await Open();await using var t=await c.BeginTransactionAsync();
            var receipt=Guid.NewGuid();var request=Guid.NewGuid();
            await using(var cmd=Command(c,t,"""
                INSERT INTO andril_hobbies.map_attraction_lifecycle_receipts VALUES($1,$2,$3,$4,false,0,true,0,false,1,0,1,1,clock_timestamp())
                """,receipt,User.UserId!.Value,request,Attraction)) await cmd.ExecuteNonQueryAsync();
            if(fault!="missing")
            {
                // Every expression/identifier is a fixed test-owned allowlist entry.
                var time=fault=="timestamp"?"accepted_at_utc+interval '1 microsecond'":"accepted_at_utc";
                var reviewer=fault=="reviewer"?"'00000000-0000-4000-8000-000000000099'::uuid":"reviewer_subject_id";
                var sql="INSERT INTO andril_hobbies.map_attraction_lifecycle_audit SELECT receipt_id,"+reviewer+",request_id,attraction_id,requested_is_active,expected_version,previous_is_active,previous_version,result_is_active,result_version,previous_operation_sequence,result_operation_sequence,outcome,"+time+" FROM andril_hobbies.map_attraction_lifecycle_receipts WHERE receipt_id=$1";
                await using var cmd=Command(c,t,sql,receipt);await cmd.ExecuteNonQueryAsync();
            }
            if(fault!="unapplied")
            {await using var cmd=Command(c,t,"UPDATE andril_hobbies.map_attraction_lifecycle SET is_active=false,version=1,operation_sequence=1,last_receipt_id=$1 WHERE attraction_id=$2",receipt,Attraction);await cmd.ExecuteNonQueryAsync();}
            await t.CommitAsync();
        }
        internal async Task Execute(string sql)
        { await using var c=await Open(); await using var cmd=new NpgsqlCommand(sql,c); await cmd.ExecuteNonQueryAsync(); }
        internal async Task<long> Scalar(string sql)
        { await using var c=await Open(); await using var cmd=new NpgsqlCommand(sql,c); return (long)(await cmd.ExecuteScalarAsync())!; }
        internal async Task Stored(bool active,long version,long sequence,int count)
        {
            await using var c=await Open();
            await using(var cmd=Command(c,null,"SELECT is_active,version,operation_sequence FROM andril_hobbies.map_attraction_lifecycle WHERE attraction_id=$1",Attraction))
            await using(var reader=await cmd.ExecuteReaderAsync())
            {Assert.True(await reader.ReadAsync());Assert.Equal(active,reader.GetBoolean(0));Assert.Equal(version,reader.GetInt64(1));Assert.Equal(sequence,reader.GetInt64(2));}
            Assert.Equal((long)count,await Scalar("SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_receipts"));
            Assert.Equal((long)count,await Scalar("SELECT count(*) FROM andril_hobbies.map_attraction_lifecycle_audit"));
            Assert.Equal(0L,await Scalar("SELECT count(*) FROM andril_hobbies.map_contributions"));
            Assert.Equal(0L,await Scalar("SELECT count(*) FROM andril_hobbies.flight_point_ledger"));
        }
        internal async Task Blocked(int holder,int waiter,Task pending,CancellationToken token)
        {
            await using var c=await Open();
            await using var cmd=Command(c,null,"WITH RECURSIVE blockers(pid) AS (SELECT unnest(pg_blocking_pids($2)) UNION SELECT unnest(pg_blocking_pids(b.pid)) FROM blockers b) SELECT EXISTS(SELECT 1 FROM blockers WHERE pid=$1)",holder,waiter);
            while(await cmd.ExecuteScalarAsync(token) is not true){Assert.False(pending.IsCompleted);await Task.Delay(20,token);}
            Assert.False(pending.IsCompleted);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class CompletionInterceptor(Action action) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)
        {action();return Task.CompletedTask;}
    }
    private static NpgsqlCommand Command(NpgsqlConnection c,NpgsqlTransaction? t,string sql,params object[] values)
    {var cmd=new NpgsqlCommand(sql,c,t);foreach(var value in values)cmd.Parameters.AddWithValue(value);return cmd;}
}
