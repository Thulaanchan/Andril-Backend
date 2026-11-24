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
    private const string SchemaSql = MapAttractionLifecycleTestSchema.Sql;

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
