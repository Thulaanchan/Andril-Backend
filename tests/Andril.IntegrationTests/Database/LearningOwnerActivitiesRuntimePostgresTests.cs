using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Basis=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningOwnerActivitiesRuntimePostgresTests
{
    private sealed class Fixture(Basis basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string Role {get;}="owner_activity_runtime_"+Guid.NewGuid().ToString("N");
        private bool roleCreated;
        internal LearningPrivateActivitiesDbContext Open()=>new(new DbContextOptionsBuilder<LearningPrivateActivitiesDbContext>().UseNpgsql(Groups.Database.ConnectionString).Options);
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Basis.Create());
            try
            {
                await using var db=f.Open();await db.Database.MigrateAsync();using var quoting=new NpgsqlCommandBuilder();var role=quoting.QuoteIdentifier(f.Role);
                var create=$"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {role} TO CURRENT_USER";
                await db.Database.ExecuteSqlRawAsync(create);f.roleCreated=true;await db.Database.ExecuteSqlRawAsync(LearningOwnerActivitiesRuntimePermissions.GrantSql(f.Role));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal IsolatedLearningActivitiesRepository Store(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)
        {
            var profiles=new PrivateProfileService(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),new LearningPrivateCvRuntimePostgresTests.Policies(),TimeProvider.System);
            var store=(IsolatedLearningActivitiesRepository)LearningOwnerActivityPersistence.Create(db,actor,profiles,Role);store.TestCheckpoint=checkpoint;return store;
        }
        internal async Task<long> Count(SharedGroupsDatabase.Actor actor)
        {
            await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning' AND state='inactive'");
            var count=await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.private_owner_activities").SingleAsync();await tx.RollbackAsync();return count;
        }
        public async ValueTask DisposeAsync()
        {
            try{await basis.DisposeAsync();}
            finally{if(roleCreated){await using var maintenance=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var quoting=new NpgsqlCommandBuilder();await using var drop=new NpgsqlCommand("DROP ROLE "+quoting.QuoteIdentifier(Role),maintenance);await drop.ExecuteNonQueryAsync();}}
        }
    }
    private static CreateLearningActivityRequest Request()=>new(SkillIds:[Guid.NewGuid()]);
    private static async Task Revoke(Fixture f,SharedGroupsDatabase.Actor actor)
    {
        await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();
    }
    [LocalPostgresFact]
    public async Task PrivateSelfReportsAndVoluntaryOffersRoundTripThroughRealM2OwnerPurpose()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Store(db,actor);var request=Request();
        var report=await store.CreateAsync(LearningActivityKind.GrowthSelfReport,request);Assert.Equal(LearningActivityOutcome.Saved,report.Outcome);Assert.NotNull(report.Activity);Assert.Equal(request.SkillIds,report.Activity.SkillIds);
        var offer=await store.CreateAsync(LearningActivityKind.MentorshipOffer,new());Assert.Equal(LearningActivityOutcome.Saved,offer.Outcome);
        await using var independent=f.Groups.Database.OpenAccounts();var read=await f.Store(independent,actor).ReadAsync(report.Activity.Kind,report.Activity.Id);Assert.Equal(LearningActivityOutcome.Available,read.Outcome);Assert.Equal(JsonSerializer.Serialize(report.Activity),JsonSerializer.Serialize(read.Activity));
        Assert.Single((await store.ListAsync(LearningActivityKind.GrowthSelfReport)).Activities!);Assert.Single((await store.ListAsync(LearningActivityKind.MentorshipOffer)).Activities!);Assert.Equal(2,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task ForeignInactiveMinorWrongModuleAndRevokedPoliciesNeverReleasePrivateActivities()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var saved=(await f.Store(db,owner).CreateAsync(LearningActivityKind.MentorshipOffer,new())).Activity!;
        var other=await f.Groups.SeedAsync("learning");await using var foreign=f.Groups.Database.OpenAccounts();Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(foreign,other).ReadAsync(saved.Kind,saved.Id)).Outcome);Assert.Empty((await f.Store(foreign,other).ListAsync(saved.Kind)).Activities!);
        foreach(var actor in new[]{await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true),await f.Groups.SeedAsync("hobbies")})
        {await using var denied=f.Groups.Database.OpenAccounts();var result=await f.Store(denied,actor).ReadAsync(saved.Kind,saved.Id);Assert.Equal(LearningActivityOutcome.Denied,result.Outcome);Assert.Null(result.Activity);}
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_identity.account_policy_records WHERE account_id={owner.Account} AND action='privacy_acknowledged'");Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,owner).ReadAsync(saved.Kind,saved.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task PeerPurposesTargetsAndCommandsCannotBorrowPrivateOwnerPermission()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Store(db,actor);
        foreach(var kind in new[]{LearningActivityKind.SkillExchange,LearningActivityKind.Pact,LearningActivityKind.MentorshipRequest,LearningActivityKind.Circle,LearningActivityKind.Connection})
        {Assert.Equal(LearningActivityOutcome.Unavailable,(await store.CreateAsync(kind,new(TargetProfiles:[Guid.NewGuid()]))).Outcome);Assert.Equal(LearningActivityOutcome.Unavailable,(await store.ListAsync(kind)).Outcome);}
        Assert.Equal(LearningActivityOutcome.Denied,(await store.CreateAsync(LearningActivityKind.MentorshipOffer,new(TargetProfiles:[Guid.NewGuid()],SkillIds:[Guid.NewGuid()]))).Outcome);
        var own=(await store.CreateAsync(LearningActivityKind.MentorshipOffer,new())).Activity!;Assert.Equal(LearningActivityOutcome.Unavailable,(await store.ChangeAsync(own.Kind,own.Id,new(LearningActivityAction.Accept,own.Revision))).Outcome);Assert.Equal(1,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task IndependentLifecycleRevocationOrdersWithPrivateReadReleaseAndPreservesRows()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var saved=(await f.Store(db,actor).CreateAsync(LearningActivityKind.MentorshipOffer,new())).Activity!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=f.Store(db,actor,async(stage,ct)=>{if(stage=="read-release"){entered.SetResult();await release.Task.WaitAsync(ct);}}).ReadAsync(saved.Kind,saved.Id);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var revoking=Revoke(f,actor);
        try{await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE andril_identity.account_module_lifecycles%') AS \"Value\"").SingleAsync(deadline.Token))await Task.Delay(20,deadline.Token);Assert.False(revoking.IsCompleted);}
        finally{release.TrySetResult();}
        Assert.Equal(LearningActivityOutcome.Available,(await reading).Outcome);await revoking.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,actor).ReadAsync(saved.Kind,saved.Id)).Outcome);Assert.Equal(1,await f.Count(actor));
    }
    private sealed class LostAck:DbTransactionInterceptor
    {public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)=>Task.FromException(new IOException("synthetic-lost-owner-activity-ack"));}
    [LocalPostgresFact]
    public async Task CancellationAndUnknownCommitsNeverCachePurposeOrReplayPrivateWrites()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();using var cancellation=new CancellationTokenSource();bool reached=false;
        var cancelled=f.Store(db,actor,(stage,ct)=>{if(stage=="before-commit"){reached=true;cancellation.Cancel();ct.ThrowIfCancellationRequested();}return Task.CompletedTask;});await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled.CreateAsync(LearningActivityKind.GrowthSelfReport,Request(),cancellation.Token));Assert.True(reached);Assert.Equal(0,await f.Count(actor));
        await using var lost=f.Groups.Database.OpenAccounts(new LostAck());var result=await f.Store(lost,actor).CreateAsync(LearningActivityKind.MentorshipOffer,new());Assert.Equal(LearningActivityOutcome.CommitUnknown,result.Outcome);Assert.Null(result.Activity);Assert.Equal(1,await f.Count(actor));await Revoke(f,actor);Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,actor).ListAsync(LearningActivityKind.MentorshipOffer)).Outcome);
    }
    [LocalPostgresFact]
    public async Task RuntimeRolesCannotModifyHistoryAccessOtherStoresOrOperateWithoutForceRls()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(db,actor).CreateAsync(LearningActivityKind.GrowthSelfReport,Request())).Outcome);
        foreach(var sql in new[]{"UPDATE andril_learning.private_owner_activities SET version=version+1","DELETE FROM andril_learning.private_owner_activities","SELECT * FROM andril_identity.accounts","SELECT * FROM andril_learning.learning_cvs"})
        {await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(sql));Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);await tx.RollbackAsync();}
        var foreign=await f.Groups.SeedAsync("learning");
        await using(var tx=await db.Database.BeginTransactionAsync())
        {
            await Basis.Bind(db,actor);var content=await db.Database.SqlQueryRaw<string>("SELECT content::text AS \"Value\" FROM andril_learning.private_owner_activities").SingleAsync();
            var id=Guid.NewGuid();var generation=Guid.NewGuid();var member=new LearningActivityMember(foreign.Account,foreign.Profile);var members=JsonSerializer.Serialize(new[]{member});
            var forged=JsonNode.Parse(content)!;forged["Id"]=JsonValue.Create(id);forged["Members"]=JsonSerializer.SerializeToNode(new[]{member});forged["Creator"]=JsonSerializer.SerializeToNode(member);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");
            var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.private_owner_activities(id,module,kind,generation,version,members,content) VALUES({id},'learning',3,{generation},1,CAST({members} AS jsonb),CAST({forged.ToJsonString()} AS jsonb))"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);await tx.RollbackAsync();
        }
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE andril_learning.private_owner_activities NO FORCE ROW LEVEL SECURITY");Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,actor).ListAsync(LearningActivityKind.GrowthSelfReport)).Outcome);await db.Database.ExecuteSqlRawAsync("ALTER TABLE andril_learning.private_owner_activities FORCE ROW LEVEL SECURITY");Assert.Equal(1,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task OwnedMigrationAndSnapshotPreserveOtherLearningAndM2StateOnRollbackAndReapply()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Open();Assert.False(db.Database.HasPendingModelChanges());
        var before=await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.\"__EFMigrationsHistory_Learning\"").SingleAsync();await db.GetService<IMigrator>().MigrateAsync("0");
        Assert.Equal(before,await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.\"__EFMigrationsHistory_Learning\"").SingleAsync());
        await db.Database.MigrateAsync();await db.Database.ExecuteSqlRawAsync(LearningOwnerActivitiesRuntimePermissions.GrantSql(f.Role));await using var accounts=f.Groups.Database.OpenAccounts();Assert.Equal(actor.Subject,(await accounts.Accounts.SingleAsync(a=>a.Id==actor.Account)).SupabaseUserId);Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(accounts,actor).CreateAsync(LearningActivityKind.MentorshipOffer,new())).Outcome);
    }
    [LocalPostgresFact]
    public async Task HttpOptInRequiresConfirmedOwnerAndNeverPublishesOffersOrAuthorizesPeerTools()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        using(var disabled=new SharedGroupsHttpHost(f.Groups))using(var client=disabled.CreateClient(new(){BaseAddress=new Uri("https://localhost")})){client.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/learning/activities/GrowthSelfReport")).StatusCode);}
        using var host=new SharedGroupsHttpHost(f.Groups,learningOwnerActivitiesRole:f.Role);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});const string route="/api/learning/activities/MentorshipOffer";
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync(route,new{owner=actor.Profile,skillIds=new[]{Guid.NewGuid()}})).StatusCode);using var created=await http.PostAsJsonAsync(route,new CreateLearningActivityRequest());Assert.Equal(HttpStatusCode.OK,created.StatusCode);Assert.True(created.Headers.CacheControl!.NoStore);using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=json.RootElement.GetProperty("activity").GetProperty("id").GetGuid();
        var other=await f.Groups.SeedAsync("learning");http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+other.Subject);Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync(route+"/"+id)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await http.PostAsJsonAsync("/api/learning/activities/Connection",new{targetProfiles=new[]{other.Profile}})).StatusCode);await Revoke(f,actor);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route+"/"+id)).StatusCode);Assert.Equal(1,await f.Count(actor));
    }
}
