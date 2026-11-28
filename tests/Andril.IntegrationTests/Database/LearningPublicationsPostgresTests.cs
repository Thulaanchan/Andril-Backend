using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts.Privacy;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Learning.Discovery;
using Andril.Learning.Services;
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

public sealed class LearningPublicationsPostgresTests
{
    private sealed class Fixture(Basis basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string Role{get;}="learning_publications_"+Guid.NewGuid().ToString("N");private bool roleCreated;
        internal LearningPublicationsDbContext Open()=>new(new DbContextOptionsBuilder<LearningPublicationsDbContext>().UseNpgsql(Groups.Database.ConnectionString).Options);
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Basis.Create());
            try
            {
                await using(var activities=new LearningPrivateActivitiesDbContext(new DbContextOptionsBuilder<LearningPrivateActivitiesDbContext>().UseNpgsql(f.Groups.Database.ConnectionString).Options))
                {await activities.Database.MigrateAsync();await activities.Database.ExecuteSqlRawAsync(LearningOwnerActivitiesRuntimePermissions.GrantSql(f.Groups.Role));}
                await using var db=f.Open();await db.Database.MigrateAsync();using var quoting=new NpgsqlCommandBuilder();var role=quoting.QuoteIdentifier(f.Role);var sql=$"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {role} TO CURRENT_USER";
                await db.Database.ExecuteSqlRawAsync(sql);f.roleCreated=true;await db.Database.ExecuteSqlRawAsync(LearningPublicationRuntimePermissions.GrantSql(f.Role));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal PrivateProfileService Profiles(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>new(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),new LearningPrivateCvRuntimePostgresTests.Policies(),TimeProvider.System);
        internal ILearningPublicationRepository Store(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>LearningPublicationPersistence.Create(db,actor,Profiles(db,actor),Role);
        internal LearningPeopleDiscoveryService People(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)=>LearningPublicationPersistence.CreatePeople(db,actor,Profiles(db,actor),new LearningPrivateCvRuntimePostgresTests.Policies(),Role,checkpoint);
        internal ILearningActivitiesRepository Activities(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>LearningOwnerActivityPersistence.Create(db,actor,Profiles(db,actor),Groups.Role);
        internal async Task Prepare(SharedGroupsDatabase.Actor actor,string name="Public Learning Name",string visibility="private")
        {await using var db=Groups.Database.OpenAccounts();await Profiles(db,actor).UpdateAsync("learning",new(name,"PRIVATE_BIO_MARKER",visibility));await Privacy(actor);}
        internal async Task Privacy(SharedGroupsDatabase.Actor actor,string visibility="public",bool ghost=false)
        {
            await using var db=Groups.Database.OpenAccounts();var row=await db.PrivacySettings.SingleOrDefaultAsync(x=>x.AccountId==actor.Account);
            if(row is null){row=UserPrivacySettings.CreateDefault(actor.Account,DateTimeOffset.UtcNow);db.Add(row);}row.Update(visibility,ghost,"disabled",false,false,DateTimeOffset.UtcNow);await db.SaveChangesAsync();
        }
        internal async Task Block(SharedGroupsDatabase.Actor actor,SharedGroupsDatabase.Actor target,bool enabled=true)
        {await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);Assert.True(await db.Database.SqlQuery<bool>($"SELECT andril_groups.write_authority('learning',{target.Profile},'block',{enabled}) AS \"Value\"").SingleAsync());await tx.CommitAsync();}
        internal async Task Lifecycle(SharedGroupsDatabase.Actor actor,string state)
        {await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state={state},revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();}
        internal async Task<Guid?> OfferProof(SharedGroupsDatabase.Actor actor,SharedGroupsDatabase.Actor mentor,Guid offer)
        {
            await using var db=Groups.Database.OpenAccounts();return await Profiles(db,actor).WithSerializableLearningProfileAsync(async profile=>{
                await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{Role},true)");
                return await db.Database.SqlQuery<Guid?>($"SELECT andril_learning.published_target_account({profile.ProfileId},{mentor.Profile},2,{offer},'fixture-terms-v1','fixture-privacy-v1') AS \"Value\"").SingleAsync();},CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {try{await basis.DisposeAsync();}finally{if(roleCreated){await using var db=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var q=new NpgsqlCommandBuilder();await using var drop=new NpgsqlCommand("DROP ROLE "+q.QuoteIdentifier(Role),db);await drop.ExecuteNonQueryAsync();}}}
    }
    [LocalPostgresFact]
    public async Task DiscoveryNeedsExplicitDisclosureAndCurrentSharedProfilePrivacy()
    {
        await using var f=await Fixture.Create();var reader=await f.Groups.SeedAsync("learning");var target=await f.Groups.SeedAsync("learning");await f.Prepare(target);await using var db=f.Groups.Database.OpenAccounts();var store=f.Store(db,target);var people=f.People(db,reader);
        Assert.Empty((await people.SearchAsync("Public")).People!);var visible=await store.SetDisclosureAsync(new(true,false));Assert.Equal(LearningPublicationOutcome.Saved,visible.Outcome);Assert.Empty((await people.SearchAsync("Public")).People!);
        var disclosed=await store.SetDisclosureAsync(new(true,true,visible.Publication!.Revision));Assert.Equal(LearningPublicationOutcome.Saved,disclosed.Outcome);var found=await people.SearchAsync("Public");Assert.Equal(LearningPeopleOutcome.Available,found.Outcome);Assert.Equal(target.Profile,Assert.Single(found.People!).ProfileId);
        Assert.Empty((await people.SearchAsync("PRIVATE_BIO_MARKER")).People!);await f.Privacy(target,"private");Assert.Empty((await people.SearchAsync("Public")).People!);await f.Privacy(target,ghost:true);Assert.Empty((await people.SearchAsync("Public")).People!);
        await f.Privacy(target);var privateProfile=await f.Profiles(db,target).GetAsync("learning");Assert.Equal("private",privateProfile.Visibility);
        Assert.Single((await people.SearchAsync("Public")).People!); // Explicit field disclosure never makes the private profile public.
    }
    [LocalPostgresFact]
    public async Task MentorshipPublicationBindsOnlyTheOwnersExactVoluntaryOffer()
    {
        await using var f=await Fixture.Create();var mentor=await f.Groups.SeedAsync("learning");var reader=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var offer=(await f.Activities(db,mentor).CreateAsync(LearningActivityKind.MentorshipOffer,new())).Activity!;
        var growth=(await f.Activities(db,mentor).CreateAsync(LearningActivityKind.GrowthSelfReport,new(SkillIds:[Guid.NewGuid()]))).Activity!;
        Assert.Null(await f.OfferProof(reader,mentor,offer.Id));var store=f.Store(db,mentor);var initial=await store.ReadAsync(LearningPublicationKind.MentorshipOffer,offer.Id);Assert.False(initial.Publication!.Published);
        Assert.Equal(LearningPublicationOutcome.NotFound,(await f.Store(db,reader).SetOfferAsync(offer.Id,new(true))).Outcome);Assert.Equal(LearningPublicationOutcome.NotFound,(await store.SetOfferAsync(growth.Id,new(true))).Outcome);
        var published=await store.SetOfferAsync(offer.Id,new(true));Assert.Equal(LearningPublicationOutcome.Saved,published.Outcome);Assert.Equal(mentor.Account,await f.OfferProof(reader,mentor,offer.Id));Assert.Null(await f.OfferProof(reader,mentor,Guid.NewGuid()));
        Assert.Equal(LearningPublicationOutcome.Saved,(await store.SetOfferAsync(offer.Id,new(false,published.Publication!.Revision))).Outcome);Assert.Null(await f.OfferProof(reader,mentor,offer.Id));
        Assert.Equal(LearningActivityOutcome.Available,(await f.Activities(db,mentor).ReadAsync(offer.Kind,offer.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task DurablePublicationRevisionsRejectConcurrentAndAbaStaleGrants()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");await f.Prepare(owner);await using var a=f.Groups.Database.OpenAccounts();await using var b=f.Groups.Database.OpenAccounts();
        var results=await Task.WhenAll(f.Store(a,owner).SetDisclosureAsync(new(true,true)),f.Store(b,owner).SetDisclosureAsync(new(true,true)));var saved=Assert.Single(results,x=>x.Outcome==LearningPublicationOutcome.Saved);Assert.Single(results,x=>x.Outcome==LearningPublicationOutcome.Conflict);
        var revoked=await f.Store(a,owner).SetDisclosureAsync(new(false,false,saved.Publication!.Revision));Assert.Equal(LearningPublicationOutcome.Saved,revoked.Outcome);
        Assert.Equal(LearningPublicationOutcome.Conflict,(await f.Store(b,owner).SetDisclosureAsync(new(true,true,saved.Publication.Revision))).Outcome);
        var renewed=await f.Store(b,owner).SetDisclosureAsync(new(true,true,revoked.Publication!.Revision));Assert.Equal(LearningPublicationOutcome.Saved,renewed.Outcome);Assert.Equal(saved.Publication.Revision!.Generation,renewed.Publication!.Revision!.Generation);Assert.True(renewed.Publication.Revision.Version>saved.Publication.Revision.Version);
    }
    [LocalPostgresFact]
    public async Task ForeignModuleInactiveAndMinorProfilesCannotPublishOrSearch()
    {
        await using var f=await Fixture.Create();await using var db=f.Groups.Database.OpenAccounts();
        foreach(var actor in new[]{await f.Groups.SeedAsync("hobbies"),await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true)})
        {Assert.Equal(LearningPublicationOutcome.Denied,(await f.Store(db,actor).SetDisclosureAsync(new(false,false))).Outcome);var result=await f.People(db,actor).SearchAsync("Public");Assert.Equal(LearningPeopleOutcome.Denied,result.Outcome);Assert.Null(result.People);}
    }
    [LocalPostgresFact]
    public async Task BlockingAndChangedNamesWithholdContentUntilTheAppropriateRenewal()
    {
        await using var f=await Fixture.Create();var reader=await f.Groups.SeedAsync("learning");var owner=await f.Groups.SeedAsync("learning");await f.Prepare(owner);await using var db=f.Groups.Database.OpenAccounts();var published=await f.Store(db,owner).SetDisclosureAsync(new(true,true));Assert.Single((await f.People(db,reader).SearchAsync("Public")).People!);
        await f.Block(owner,reader);Assert.Empty((await f.People(db,reader).SearchAsync("Public")).People!);await f.Block(owner,reader,false);
        await f.Prepare(owner,"Changed Professional Name");Assert.Empty((await f.People(db,reader).SearchAsync("Changed")).People!);Assert.Empty((await f.People(db,reader).SearchAsync("Public")).People!);
        Assert.Equal(LearningPublicationOutcome.Saved,(await f.Store(db,owner).SetDisclosureAsync(new(true,true,published.Publication!.Revision))).Outcome);Assert.Single((await f.People(db,reader).SearchAsync("Changed")).People!);
    }
    [LocalPostgresFact]
    public async Task IndependentRevocationWaitsForReadReleaseAndDeniesSubsequentDisclosure()
    {
        await using var f=await Fixture.Create();var reader=await f.Groups.SeedAsync("learning");var owner=await f.Groups.SeedAsync("learning");await f.Prepare(owner);await using var writer=f.Groups.Database.OpenAccounts();var published=await f.Store(writer,owner).SetDisclosureAsync(new(true,true));await using var db=f.Groups.Database.OpenAccounts();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=f.People(db,reader,async(stage,ct)=>{if(stage=="read-release"){entered.SetResult();await release.Task.WaitAsync(ct);}}).SearchAsync("Public");await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var revoking=f.Store(writer,owner).SetDisclosureAsync(new(false,false,published.Publication!.Revision));
        try{await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%pg_advisory_lock%andril.groups.authority%') AS \"Value\"").SingleAsync(deadline.Token))await Task.Delay(20,deadline.Token);Assert.False(revoking.IsCompleted);}
        finally{release.TrySetResult();}
        Assert.Single((await reading).People!);Assert.Equal(LearningPublicationOutcome.Saved,(await revoking.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);Assert.Empty((await f.People(db,reader).SearchAsync("Public")).People!);
    }
    [LocalPostgresFact]
    public async Task CancellationRollsBackAndLifecycleReactivationRequiresFreshPublication()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");var reader=await f.Groups.SeedAsync("learning");await f.Prepare(owner);await using var db=f.Groups.Database.OpenAccounts();using var cancel=new CancellationTokenSource();var store=f.Store(db,owner);LearningPublicationPersistence.SetCheckpoint(store,(stage,_)=>{if(stage=="before-commit")cancel.Cancel();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SetDisclosureAsync(new(true,true),cancel.Token));Assert.Null((await f.Store(db,owner).ReadAsync(LearningPublicationKind.People)).Publication!.Revision);
        var published=await f.Store(db,owner).SetDisclosureAsync(new(true,true));await f.Lifecycle(owner,"inactive");Assert.Empty((await f.People(db,reader).SearchAsync("Public")).People!);await f.Lifecycle(owner,"active");Assert.Empty((await f.People(db,reader).SearchAsync("Public")).People!);
        Assert.Equal(LearningPublicationOutcome.Saved,(await f.Store(db,owner).SetDisclosureAsync(new(true,true,published.Publication!.Revision))).Outcome);Assert.Single((await f.People(db,reader).SearchAsync("Public")).People!);
    }
    [LocalPostgresFact]
    public async Task RestrictedRolesForceRlsAndMigrationRollbackPreserveExistingPrivateOffers()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var offer=(await f.Activities(db,owner).CreateAsync(LearningActivityKind.MentorshipOffer,new())).Activity!;await using var model=f.Open();Assert.False(model.Database.HasPendingModelChanges());
        await using(var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            await Basis.Bind(db,owner);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT row_security_active('andril_learning.publications') AND (SELECT relforcerowsecurity FROM pg_class WHERE oid='andril_learning.publications'::regclass) AS \"Value\"").SingleAsync());
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
            foreach(var statement in new[]{"SELECT * FROM andril_identity.accounts","SELECT * FROM andril_learning.publication_scopes","SELECT * FROM andril_learning.private_owner_activities"})
            {await tx.CreateSavepointAsync("denial");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(statement));Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);await tx.RollbackToSavepointAsync("denial");}
            Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning.publications','DELETE') OR has_column_privilege(current_user,'andril_learning.publications','account_id','UPDATE') AS \"Value\"").SingleAsync());await tx.RollbackAsync();
        }
        await model.GetService<IMigrator>().MigrateAsync("0");await model.Database.MigrateAsync();await model.Database.ExecuteSqlRawAsync(LearningPublicationRuntimePermissions.GrantSql(f.Role));Assert.Equal(LearningActivityOutcome.Available,(await f.Activities(db,owner).ReadAsync(offer.Kind,offer.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task ControlledHttpRequiresConfirmedOwnerExplicitBodiesAndPrivateNoStoreResponses()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");var reader=await f.Groups.SeedAsync("learning");await f.Prepare(owner);const string route="/api/learning/disclosure";
        using(var disabled=new SharedGroupsHttpHost(f.Groups))using(var client=disabled.CreateClient(new(){BaseAddress=new Uri("https://localhost")})){client.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+owner.Subject);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(route)).StatusCode);}
        using var host=new SharedGroupsHttpHost(f.Groups,learningPublicationRole:f.Role);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+owner.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+owner.Subject);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PutAsJsonAsync(route,new{visible=true,displayNameDisclosure=true,owner=reader.Account})).StatusCode);Assert.Equal(HttpStatusCode.BadRequest,(await http.PutAsJsonAsync(route,new{})).StatusCode);
        using var saved=await http.PutAsJsonAsync(route,new LearningDisclosureChange(true,true));Assert.Equal(HttpStatusCode.OK,saved.StatusCode);Assert.True(saved.Headers.CacheControl!.NoStore);
        http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+reader.Subject);using var found=await http.GetAsync("/api/learning/people?query=Public");Assert.Equal(HttpStatusCode.OK,found.StatusCode);var text=await found.Content.ReadAsStringAsync();Assert.Contains(owner.Profile.ToString(),text);Assert.DoesNotContain(owner.Account.ToString(),text);Assert.DoesNotContain("PRIVATE_BIO_MARKER",text);Assert.True(found.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.GetAsync("/api/learning/people?query=Public&limit=bad")).StatusCode);
    }
}
