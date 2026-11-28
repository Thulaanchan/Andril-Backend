using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Basis=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningPeerActivitiesRuntimePostgresTests
{
    private sealed class Fixture(Basis basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string Role{get;}="learning_peer_"+Guid.NewGuid().ToString("N");private bool created;
        internal LearningPeerActivitiesDbContext Open()=>new(new DbContextOptionsBuilder<LearningPeerActivitiesDbContext>().UseNpgsql(Groups.Database.ConnectionString).Options);
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Basis.Create());
            try{await using var db=f.Open();await db.Database.MigrateAsync();using var q=new NpgsqlCommandBuilder();var name=q.QuoteIdentifier(f.Role);var sql=$"CREATE ROLE {name} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {name} TO CURRENT_USER";await db.Database.ExecuteSqlRawAsync(sql);f.created=true;await db.Database.ExecuteSqlRawAsync(LearningPeerRuntimePermissions.GrantSql(f.Role));return f;}
            catch{await f.DisposeAsync();throw;}
        }
        internal IsolatedLearningActivitiesRepository Store(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)
        {
            var catalog=new LearningPrivateCvRuntimePostgresTests.Policies();var profiles=new PrivateProfileService(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),catalog,TimeProvider.System);
            var store=(IsolatedLearningActivitiesRepository)LearningPeerActivityPersistence.Create(db,actor,profiles,catalog,Role);store.TestCheckpoint=checkpoint;return store;
        }
        internal async Task Block(SharedGroupsDatabase.Actor actor,SharedGroupsDatabase.Actor target)
        {
            await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT andril_groups.write_authority('learning',{target.Profile},'block',true) AS \"Value\"").SingleAsync());await tx.CommitAsync();
        }
        internal async Task Revoke(SharedGroupsDatabase.Actor actor)
        {await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();}
        public async ValueTask DisposeAsync()
        {try{await basis.DisposeAsync();}finally{if(created){await using var db=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var q=new NpgsqlCommandBuilder();await using var command=new NpgsqlCommand("DROP ROLE "+q.QuoteIdentifier(Role),db);await command.ExecuteNonQueryAsync();}}}
    }
    private static CreateLearningActivityRequest Request(LearningActivityKind kind,Guid target)=>kind==LearningActivityKind.SkillExchange?new([target],Guid.NewGuid(),Guid.NewGuid()):kind==LearningActivityKind.Pact?new([target],Commitments:["explicit private agreement"]):new([target]);
    [LocalPostgresFact]
    public async Task ExchangesPactsAndConnectionsUseRealM2OwnersAndExistingRecipientTransitions()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();
        foreach(var kind in new[]{LearningActivityKind.SkillExchange,LearningActivityKind.Pact,LearningActivityKind.Connection})
        {
            var saved=await f.Store(first,a).CreateAsync(kind,Request(kind,b.Profile));Assert.Equal(LearningActivityOutcome.Saved,saved.Outcome);var item=saved.Activity!;
            Assert.Equal(LearningActivityOutcome.Available,(await f.Store(second,b).ReadAsync(kind,item.Id)).Outcome);
            Assert.Single((await f.Store(second,b).ListAsync(kind)).Activities!);
            var accepted=await f.Store(second,b).ChangeAsync(kind,item.Id,new(LearningActivityAction.Accept,item.Revision));Assert.Equal(LearningActivityOutcome.Saved,accepted.Outcome);
            Assert.Equal(LearningActivityOutcome.Conflict,(await f.Store(first,a).ChangeAsync(kind,item.Id,new(LearningActivityAction.Cancel,item.Revision))).Outcome);
        }
    }
    [LocalPostgresFact]
    public async Task MultiMemberPactsPreserveAllParticipantsAndRequireUnblockedPairs()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var c=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var kind=LearningActivityKind.Pact;
        var request=new CreateLearningActivityRequest([b.Profile,c.Profile],Commitments:["shared explicit commitment"]);
        var saved=await f.Store(db,a).CreateAsync(kind,request);Assert.Equal(LearningActivityOutcome.Saved,saved.Outcome);
        foreach(var member in new[]{a,b,c})Assert.Equal(LearningActivityOutcome.Available,(await f.Store(db,member).ReadAsync(kind,saved.Activity!.Id)).Outcome);
        var accepted=await f.Store(db,b).ChangeAsync(kind,saved.Activity!.Id,new(LearningActivityAction.Accept,saved.Activity.Revision));Assert.Equal(LearningActivityOutcome.Saved,accepted.Outcome);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(db,c).ChangeAsync(kind,saved.Activity.Id,new(LearningActivityAction.Accept,accepted.Activity!.Revision))).Outcome);
        await f.Block(b,c);Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,a).CreateAsync(kind,request)).Outcome);Assert.Empty((await f.Store(db,a).ListAsync(kind)).Activities!);
    }
    [LocalPostgresFact]
    public async Task PeerEligibilityRejectsSelfForeignModuleInactiveMinorAndOutdatedPolicyTargets()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Store(db,a);
        foreach(var target in new[]{a,await f.Groups.SeedAsync("hobbies"),await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true)})Assert.Equal(LearningActivityOutcome.Denied,(await store.CreateAsync(LearningActivityKind.Connection,Request(LearningActivityKind.Connection,target.Profile))).Outcome);
        var b=await f.Groups.SeedAsync("learning");await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_identity.account_policy_records WHERE account_id={b.Account} AND action='privacy_acknowledged'");Assert.Equal(LearningActivityOutcome.Denied,(await store.CreateAsync(LearningActivityKind.Connection,Request(LearningActivityKind.Connection,b.Profile))).Outcome);
        Assert.Equal(LearningActivityOutcome.Unavailable,(await store.CreateAsync(LearningActivityKind.Circle,new(CircleId:Guid.NewGuid()))).Outcome);
        Assert.Equal(LearningActivityOutcome.Unavailable,(await store.CreateAsync(LearningActivityKind.MentorshipRequest,new([b.Profile],RelatedOfferId:Guid.NewGuid()))).Outcome);
    }
    [LocalPostgresFact]
    public async Task ForeignReadersCannotSeeOrChangeRelationshipsAndBlocksRevokeBothDirections()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var c=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var saved=(await f.Store(db,a).CreateAsync(LearningActivityKind.Connection,Request(LearningActivityKind.Connection,b.Profile))).Activity!;
        Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,c).ReadAsync(saved.Kind,saved.Id)).Outcome);Assert.Empty((await f.Store(db,c).ListAsync(saved.Kind)).Activities!);
        Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,c).ChangeAsync(saved.Kind,saved.Id,new(LearningActivityAction.Accept,saved.Revision))).Outcome);
        await f.Block(b,a);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,a).ReadAsync(saved.Kind,saved.Id)).Outcome);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,b).ReadAsync(saved.Kind,saved.Id)).Outcome);
        Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,a).CreateAsync(saved.Kind,Request(saved.Kind,b.Profile))).Outcome);
    }
    [LocalPostgresFact]
    public async Task IndependentConnectionsRejectReverseDuplicatesAndTerminalConnectionsPermitNewIntent()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();
        var kind=LearningActivityKind.Connection;var results=await Task.WhenAll(f.Store(first,a).CreateAsync(kind,Request(kind,b.Profile)),f.Store(second,b).CreateAsync(kind,Request(kind,a.Profile)));
        var saved=Assert.Single(results,x=>x.Outcome==LearningActivityOutcome.Saved).Activity!;Assert.Single(results,x=>x.Outcome==LearningActivityOutcome.Conflict);
        var proposer=results[0].Outcome==LearningActivityOutcome.Saved?a:b;var recipient=proposer==a?b:a;
        var accepted=(await f.Store(first,recipient).ChangeAsync(kind,saved.Id,new(LearningActivityAction.Accept,saved.Revision))).Activity!;
        Assert.Equal(LearningActivityOutcome.Conflict,(await f.Store(first,proposer).CreateAsync(kind,Request(kind,recipient.Profile))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(first,proposer).ChangeAsync(kind,saved.Id,new(LearningActivityAction.Disconnect,accepted.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(first,recipient).CreateAsync(kind,Request(kind,proposer.Profile))).Outcome);
    }
    [LocalPostgresFact]
    public async Task IndependentBlockWriterWaitsForCommitAndThenPreventsFurtherAccess()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating=f.Store(db,a,async(stage,ct)=>{if(stage=="before-commit"){entered.SetResult();await release.Task.WaitAsync(ct);}}).CreateAsync(LearningActivityKind.Connection,Request(LearningActivityKind.Connection,b.Profile));await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var blocking=f.Block(b,a);
        try{await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%andril_groups.write_authority%') AS \"Value\"").SingleAsync(deadline.Token))await Task.Delay(20,deadline.Token);Assert.False(blocking.IsCompleted);}
        finally{release.TrySetResult();}
        var saved=await creating;Assert.Equal(LearningActivityOutcome.Saved,saved.Outcome);await blocking.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,a).ReadAsync(saved.Activity!.Kind,saved.Activity.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task CancellationRollbackAndTargetDeactivationPreserveCommittedRecords()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();using var cancel=new CancellationTokenSource();var kind=LearningActivityKind.SkillExchange;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Store(db,a,(stage,_)=>{if(stage=="before-commit")cancel.Cancel();return Task.CompletedTask;}).CreateAsync(kind,Request(kind,b.Profile),cancel.Token));Assert.Empty((await f.Store(db,a).ListAsync(kind)).Activities!);
        var saved=(await f.Store(db,a).CreateAsync(kind,Request(kind,b.Profile))).Activity!;await f.Revoke(b);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,a).ReadAsync(kind,saved.Id)).Outcome);
        await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,b);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={b.Account} AND module='learning'");
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('andril.peer_terms','fixture-terms-v1',true),set_config('andril.peer_privacy','fixture-privacy-v1',true)");
        Assert.Equal(1L,await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.peer_activities").SingleAsync());await tx.RollbackAsync();
    }
    [LocalPostgresFact]
    public async Task MigrationMatchesModelAndRestrictedRoleCannotReadIdentityOrAuthorityScopes()
    {
        await using var f=await Fixture.Create();await using var model=f.Open();Assert.False(model.Database.HasPendingModelChanges());var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT row_security_active('andril_learning.peer_activities') AND (SELECT relforcerowsecurity FROM pg_class WHERE oid='andril_learning.peer_activities'::regclass) AS \"Value\"").SingleAsync());Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        foreach(var statement in new[]{"SELECT * FROM andril_learning.peer_authority_scopes","SELECT * FROM andril_identity.accounts"})
        {await tx.CreateSavepointAsync("denial");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(statement));Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);await tx.RollbackToSavepointAsync("denial");}
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning.peer_activities','DELETE') OR has_column_privilege(current_user,'andril_learning.peer_activities','members','UPDATE') AS \"Value\"").SingleAsync());await tx.RollbackAsync();
        await model.GetService<IMigrator>().MigrateAsync("0");await model.Database.MigrateAsync();await model.Database.ExecuteSqlRawAsync(LearningPeerRuntimePermissions.GrantSql(f.Role));
    }
    [LocalPostgresFact]
    public async Task OptInHttpRequiresConfirmedOwnerAndPreservesRoleSpecificTransitions()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");using var host=new SharedGroupsHttpHost(f.Groups,learningPeerRole:f.Role);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});const string route="/api/learning/activities/Connection";
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+a.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+a.Subject);
        using var created=await http.PostAsJsonAsync(route,Request(LearningActivityKind.Connection,b.Profile));Assert.Equal(HttpStatusCode.OK,created.StatusCode);Assert.True(created.Headers.CacheControl!.NoStore);
        using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=json.RootElement.GetProperty("activity").GetProperty("id").GetGuid();Assert.NotEqual(Guid.Empty,id);Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync(route,new{targetProfiles=new[]{b.Profile},owner=b.Account})).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await http.PostAsJsonAsync("/api/learning/activities/MentorshipRequest",new CreateLearningActivityRequest([b.Profile],RelatedOfferId:Guid.NewGuid()))).StatusCode);
        http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+b.Subject);Assert.Equal(HttpStatusCode.OK,(await http.GetAsync(route+"/"+id)).StatusCode);
    }
}
