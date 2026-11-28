using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Learning.Activities;
using Andril.Learning.Discovery;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Basis=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;
using Publications=Andril.IntegrationTests.Database.LearningPublicationsPostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningMentorshipRequestsRuntimePostgresTests
{
    private const LearningActivityKind Kind=LearningActivityKind.MentorshipRequest;
    private sealed class Fixture(Publications publications):IAsyncDisposable
    {
        internal Publications Publications=>publications;
        internal SharedGroupsDatabase Groups=>publications.Groups;
        internal string Role{get;}="learning_mentorship_"+Guid.NewGuid().ToString("N");private bool created;
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Publications.Create());
            try
            {
                await using var db=new LearningPeerActivitiesDbContext(new DbContextOptionsBuilder<LearningPeerActivitiesDbContext>().UseNpgsql(f.Groups.Database.ConnectionString).Options);
                await db.Database.MigrateAsync();using var quoting=new NpgsqlCommandBuilder();var role=quoting.QuoteIdentifier(f.Role);
                var sql=$"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {role} TO CURRENT_USER";
                await db.Database.ExecuteSqlRawAsync(sql);f.created=true;await db.Database.ExecuteSqlRawAsync(LearningPeerRuntimePermissions.GrantSql(f.Role));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal IsolatedLearningActivitiesRepository Store(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)
        {var store=(IsolatedLearningActivitiesRepository)LearningPeerActivityPersistence.Create(db,actor,publications.Profiles(db,actor),new LearningPrivateCvRuntimePostgresTests.Policies(),Role);store.TestCheckpoint=checkpoint;return store;}
        internal async Task<Guid> Offer(SharedGroupsDatabase.Actor mentor,bool publish=true)
        {
            await using var db=Groups.Database.OpenAccounts();var offer=await publications.Activities(db,mentor).CreateAsync(LearningActivityKind.MentorshipOffer,new());Assert.Equal(LearningActivityOutcome.Saved,offer.Outcome);
            if(publish)Assert.Equal(LearningPublicationOutcome.Saved,(await publications.Store(db,mentor).SetOfferAsync(offer.Activity!.Id,new(true))).Outcome);return offer.Activity!.Id;
        }
        public async ValueTask DisposeAsync()
        {try{await publications.DisposeAsync();}finally{if(created){await using var db=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var quoting=new NpgsqlCommandBuilder();await using var drop=new NpgsqlCommand("DROP ROLE "+quoting.QuoteIdentifier(Role),db);await drop.ExecuteNonQueryAsync();}}}
    }
    private static CreateLearningActivityRequest Request(SharedGroupsDatabase.Actor mentor,Guid offer)=>new([mentor.Profile],RelatedOfferId:offer);
    [LocalPostgresFact]
    public async Task PublishedOfferRequestsUseRealParticipantsAndRoleSpecificTransitions()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var stranger=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);await using var db=f.Groups.Database.OpenAccounts();
        var saved=await f.Store(db,a).CreateAsync(Kind,Request(b,offer));Assert.Equal(LearningActivityOutcome.Saved,saved.Outcome);var item=saved.Activity!;
        Assert.Equal(LearningActivityOutcome.Available,(await f.Store(db,b).ReadAsync(Kind,item.Id)).Outcome);Assert.Single((await f.Store(db,b).ListAsync(Kind)).Activities!);
        Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,stranger).ReadAsync(Kind,item.Id)).Outcome);Assert.Empty((await f.Store(db,stranger).ListAsync(Kind)).Activities!);
        Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,a).ChangeAsync(Kind,item.Id,new(LearningActivityAction.Accept,item.Revision))).Outcome);
        var accepted=await f.Store(db,b).ChangeAsync(Kind,item.Id,new(LearningActivityAction.Accept,item.Revision));Assert.Equal(LearningActivityOutcome.Saved,accepted.Outcome);Assert.Equal("Accepted",accepted.Activity!.Status);
        Assert.Equal(LearningActivityOutcome.Conflict,(await f.Store(db,b).ChangeAsync(Kind,item.Id,new(LearningActivityAction.Decline,item.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.Available,(await f.Publications.Activities(db,b).ReadAsync(LearningActivityKind.MentorshipOffer,offer)).Outcome);
    }
    [LocalPostgresFact]
    public async Task ExactPublishedOwnerOfferIsRequiredAndGrowthRemainsPrivate()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var c=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var unpublished=await f.Offer(b,false);var other=await f.Offer(c);
        var growth=(await f.Publications.Activities(db,b).CreateAsync(LearningActivityKind.GrowthSelfReport,new(SkillIds:[Guid.NewGuid()]))).Activity!;
        foreach(var offer in new[]{unpublished,other,growth.Id,Guid.NewGuid()})Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,a).CreateAsync(Kind,Request(b,offer))).Outcome);
        Assert.Empty((await f.Store(db,a).ListAsync(Kind)).Activities!);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Publications.Activities(db,a).ReadAsync(LearningActivityKind.GrowthSelfReport,growth.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task OnePendingUnorderedPairIncludesDifferentOffersAndReverseRequests()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var first=await f.Offer(b);var second=await f.Offer(b);var reverse=await f.Offer(a);await using var x=f.Groups.Database.OpenAccounts();await using var y=f.Groups.Database.OpenAccounts();
        var results=await Task.WhenAll(f.Store(x,a).CreateAsync(Kind,Request(b,first)),f.Store(y,a).CreateAsync(Kind,Request(b,second)));
        var saved=Assert.Single(results,r=>r.Outcome==LearningActivityOutcome.Saved).Activity!;Assert.Single(results,r=>r.Outcome==LearningActivityOutcome.Conflict);
        Assert.Equal(LearningActivityOutcome.Conflict,(await f.Store(x,b).CreateAsync(Kind,Request(a,reverse))).Outcome);
        var declined=await f.Store(x,b).ChangeAsync(Kind,saved.Id,new(LearningActivityAction.Decline,saved.Revision));Assert.Equal(LearningActivityOutcome.Saved,declined.Outcome);
        var next=await f.Store(x,a).CreateAsync(Kind,Request(b,second));Assert.Equal(LearningActivityOutcome.Saved,next.Outcome);
        Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(x,b).ChangeAsync(Kind,next.Activity!.Id,new(LearningActivityAction.Cancel,next.Activity.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(x,a).ChangeAsync(Kind,next.Activity.Id,new(LearningActivityAction.Cancel,next.Activity.Revision))).Outcome);
    }
    [LocalPostgresFact]
    public async Task LifecycleBlockingAndPublicationRevocationDenyBothParticipantsWithoutDeletingRecords()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);await using var db=f.Groups.Database.OpenAccounts();var saved=(await f.Store(db,a).CreateAsync(Kind,Request(b,offer))).Activity!;
        await f.Publications.Block(a,b);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,b).ReadAsync(Kind,saved.Id)).Outcome);await f.Publications.Block(a,b,false);
        await f.Publications.Lifecycle(a,"inactive");Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,b).ReadAsync(Kind,saved.Id)).Outcome);await f.Publications.Lifecycle(a,"active");
        Assert.Equal(LearningActivityOutcome.Available,(await f.Store(db,b).ReadAsync(Kind,saved.Id)).Outcome);
        var publication=(await f.Publications.Store(db,b).ReadAsync(LearningPublicationKind.MentorshipOffer,offer)).Publication!;Assert.Equal(LearningPublicationOutcome.Saved,(await f.Publications.Store(db,b).SetOfferAsync(offer,new(false,publication.Revision))).Outcome);
        Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,a).ReadAsync(Kind,saved.Id)).Outcome);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(db,b).ReadAsync(Kind,saved.Id)).Outcome);
        var revoked=(await f.Publications.Store(db,b).ReadAsync(LearningPublicationKind.MentorshipOffer,offer)).Publication!;await f.Publications.Store(db,b).SetOfferAsync(offer,new(true,revoked.Revision));
        Assert.Equal(saved.Id,(await f.Store(db,a).ReadAsync(Kind,saved.Id)).Activity!.Id);
    }
    [LocalPostgresFact]
    public async Task ForeignModuleInactiveMinorAndSelfRequestsAreDenied()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);await using var db=f.Groups.Database.OpenAccounts();
        foreach(var invalid in new[]{await f.Groups.SeedAsync("hobbies"),await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true)})
        {Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,invalid).CreateAsync(Kind,Request(b,offer))).Outcome);Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,a).CreateAsync(Kind,Request(invalid,offer))).Outcome);}
        Assert.Equal(LearningActivityOutcome.Denied,(await f.Store(db,b).CreateAsync(Kind,Request(b,offer))).Outcome);
    }
    [LocalPostgresFact]
    public async Task CancellationBeforeCommitRollsBackRequestAndLeavesOfferIntact()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);await using var db=f.Groups.Database.OpenAccounts();using var cancel=new CancellationTokenSource();bool reached=false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Store(db,a,(stage,_)=>{if(stage=="before-commit"){reached=true;cancel.Cancel();}return Task.CompletedTask;}).CreateAsync(Kind,Request(b,offer),cancel.Token));Assert.True(reached);Assert.Empty((await f.Store(db,a).ListAsync(Kind)).Activities!);
        Assert.Equal(LearningActivityOutcome.Saved,(await f.Store(db,a).CreateAsync(Kind,Request(b,offer))).Outcome);
        Assert.Equal(LearningActivityOutcome.Available,(await f.Publications.Activities(db,b).ReadAsync(LearningActivityKind.MentorshipOffer,offer)).Outcome);
    }
    [LocalPostgresFact]
    public async Task IndependentPublicationRevocationWaitsForCommitAndThenDeniesFurtherAccess()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);
        await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();
        var publication=(await f.Publications.Store(second,b).ReadAsync(LearningPublicationKind.MentorshipOffer,offer)).Publication!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating=f.Store(first,a,async(stage,ct)=>{if(stage=="before-commit"){entered.TrySetResult();await release.Task.WaitAsync(ct);}}).CreateAsync(Kind,Request(b,offer));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revoking=f.Publications.Store(second,b).SetOfferAsync(offer,new(false,publication.Revision));
        try
        {
            await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%pg_advisory_lock%andril.groups.authority%') AS \"Value\"").SingleAsync(deadline.Token))await Task.Delay(20,deadline.Token);
            Assert.False(revoking.IsCompleted);
        }
        finally{release.TrySetResult();}
        var saved=await creating.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(LearningActivityOutcome.Saved,saved.Outcome);
        Assert.Equal(LearningPublicationOutcome.Saved,(await revoking.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(first,a).ReadAsync(Kind,saved.Activity!.Id)).Outcome);Assert.Equal(LearningActivityOutcome.NotFound,(await f.Store(second,b).ReadAsync(Kind,saved.Activity.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task RestrictedRuntimeHasNoPublicationIdentityOrScopeAccessAndForceRlsRemainsEnabled()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);await Basis.Bind(db,a);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT row_security_active('andril_learning.peer_activities') AND (SELECT relforcerowsecurity FROM pg_class WHERE oid='andril_learning.peer_activities'::regclass) AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        foreach(var sql in new[]{"SELECT * FROM andril_learning.publications","SELECT * FROM andril_learning.peer_authority_scopes","SELECT * FROM andril_learning.publication_scopes","SELECT * FROM andril_identity.accounts","SELECT * FROM andril_learning.private_owner_activities"})
        {await tx.CreateSavepointAsync("denial");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(sql));Assert.Equal("42501",error.SqlState);await tx.RollbackToSavepointAsync("denial");}
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_function_privilege(current_user,'andril_learning.mentorship_target_account(uuid,uuid,text,text)','EXECUTE') OR has_table_privilege(current_user,'andril_learning.peer_activities','DELETE') OR has_column_privilege(current_user,'andril_learning.peer_activities','members','UPDATE') AS \"Value\"").SingleAsync());
    }
    [LocalPostgresFact]
    public async Task ControlledHttpUsesPublishedOfferAndRejectsClientOwnershipAndWrongActorTransitions()
    {
        await using var f=await Fixture.Create();var a=await f.Groups.SeedAsync("learning");var b=await f.Groups.SeedAsync("learning");var offer=await f.Offer(b);using var host=new SharedGroupsHttpHost(f.Groups,learningPeerRole:f.Role);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});const string route="/api/learning/activities/MentorshipRequest";
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.PostAsJsonAsync(route,Request(b,offer))).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+a.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.PostAsJsonAsync(route,Request(b,offer))).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+a.Subject);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync(route,new{targetProfiles=new[]{b.Profile},relatedOfferId=offer,owner=b.Account})).StatusCode);
        using var response=await http.PostAsJsonAsync(route,Request(b,offer));Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl!.NoStore);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var id=body.RootElement.GetProperty("activity").GetProperty("id").GetGuid();
        http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+b.Subject);Assert.Equal(HttpStatusCode.OK,(await http.GetAsync(route+"/"+id)).StatusCode);
        Assert.DoesNotContain("AccountId",await response.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
    }
}
