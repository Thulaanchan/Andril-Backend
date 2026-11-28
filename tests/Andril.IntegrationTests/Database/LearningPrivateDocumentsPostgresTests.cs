using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Diary;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using BaseFixture=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningPrivateDocumentsPostgresTests
{
    private static readonly DateOnly Day=new(2024,2,29);
    private sealed class Fixture(BaseFixture basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string PortfolioRole {get;}="portfolio_runtime_"+Guid.NewGuid().ToString("N");
        internal string DiaryRole {get;}="diary_runtime_"+Guid.NewGuid().ToString("N");
        private readonly List<string> roles=[];
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await BaseFixture.Create());
            try
            {
                await using var db=new DiaryDbContext(new DbContextOptionsBuilder<DiaryDbContext>().UseNpgsql(f.Groups.Database.ConnectionString).Options);await db.Database.MigrateAsync();
                foreach(var role in new[]{f.PortfolioRole,f.DiaryRole})
                {
                    using var quoting=new NpgsqlCommandBuilder();var quoted=quoting.QuoteIdentifier(role);
                    var sql=$"CREATE ROLE {quoted} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {quoted} TO CURRENT_USER";
                    await db.Database.ExecuteSqlRawAsync(sql);f.roles.Add(role);
                    await db.Database.ExecuteSqlRawAsync(role==f.PortfolioRole?LearningPortfolioRuntimePermissions.GrantSql(role):LearningDiaryRuntimePermissions.GrantSql(role));
                }
                return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        private static PrivateProfileService Profiles(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>new(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),new LearningPrivateCvRuntimePostgresTests.Policies(),TimeProvider.System);
        internal IsolatedLearningPortfolioRepository Portfolio(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)=>
            new(new LearningOwnerDocumentTransaction(db,actor,Profiles(db,actor),PortfolioRole,LearningPrivateDocument.Portfolio)){TestCheckpoint=checkpoint};
        internal IsolatedModuleDiaryRepository Diary(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<string,CancellationToken,Task>? checkpoint=null)=>
            new(new LearningDiaryTransaction(db,actor,Profiles(db,actor),DiaryRole)){TestCheckpoint=checkpoint};
        internal async Task<long> Count(SharedGroupsDatabase.Actor actor,bool diary)
        {
            await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await BaseFixture.Bind(db,actor);
            // Fixture-only rolled-back reactivation can observe retained rows without bypassing FORCE RLS.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning' AND state='inactive'");
            var sql=diary?"SELECT count(*) AS \"Value\" FROM andril_diary.module_diary_entries":"SELECT count(*) AS \"Value\" FROM andril_learning.learning_portfolios";
            var count=await db.Database.SqlQueryRaw<long>(sql).SingleAsync();await tx.RollbackAsync();return count;
        }
        public async ValueTask DisposeAsync()
        {
            try{await basis.DisposeAsync();}
            finally
            {
                if(roles.Count>0)
                {
                    await using var maintenance=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var quoting=new NpgsqlCommandBuilder();
                    await using var command=new NpgsqlCommand("DROP ROLE "+string.Join(',',roles.Select(quoting.QuoteIdentifier)),maintenance);await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
    private static ModuleDiaryEntry Entry(SharedGroupsDatabase.Actor actor,DateOnly? day=null)
    {var entry=new ModuleDiaryEntry(actor.Profile,"learning",day??Day);entry.SetReflection("Private","Owner reflection");entry.AddPlan(new(Guid.NewGuid(),"Plan","Owner plan"));return entry;}
    private static async Task Revoke(Fixture f,SharedGroupsDatabase.Actor actor)
    {
        await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await BaseFixture.Bind(db,actor);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();
    }
    [LocalPostgresFact]
    public async Task RealM2PortfolioCrudPreservesProjectIdentitiesOrderAndTemplates()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Portfolio(db,actor);
        var a=await store.CreateAsync(PortfolioTemplate.Developer);var b=await store.CreateAsync(PortfolioTemplate.Creative);Assert.NotNull(a.Portfolio);Assert.NotNull(b.Portfolio);
        var first=new PortfolioProject(Guid.NewGuid(),"First","Original",["https://example.invalid/private-media"],["https://example.invalid"]);var second=new PortfolioProject(Guid.NewGuid(),"Second","Original");
        Assert.Equal(PortfolioOperationState.Available,(await store.SaveAsync(a.Portfolio.Id,PortfolioSaveChange.Add(first))).State);
        await store.SaveAsync(a.Portfolio.Id,PortfolioSaveChange.Add(second));await store.SaveAsync(a.Portfolio.Id,PortfolioSaveChange.Reorder([second.Id,first.Id]));await store.SaveAsync(a.Portfolio.Id,PortfolioSaveChange.Template(PortfolioTemplate.Professional));
        await using var independent=f.Groups.Database.OpenAccounts();var reopened=await f.Portfolio(independent,actor).OpenAsync(a.Portfolio.Id);Assert.NotNull(reopened.Portfolio);
        Assert.Equal(new[]{second.Id,first.Id},reopened.Portfolio.Projects.Select(p=>p.Id));Assert.Equal(PortfolioTemplate.Professional,reopened.Portfolio.Template);Assert.Equal("Original",reopened.Portfolio.Projects[1].Description);
        Assert.Equal(PortfolioOperationState.Available,(await store.PreviewAsync(a.Portfolio.Id)).State);Assert.Equal(2,(await store.ListAsync()).Ids!.Count);
        Assert.Equal(PortfolioOperationState.Available,(await store.DeleteAsync(a.Portfolio.Id)).State);Assert.Equal(PortfolioOperationState.NotFound,(await store.OpenAsync(a.Portfolio.Id)).State);Assert.Equal(PortfolioOperationState.Available,(await store.OpenAsync(b.Portfolio.Id)).State);
    }
    [LocalPostgresFact]
    public async Task LearningDiaryCrudPreservesLiteralDateAndExplicitDeletionAllowsRecreation()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Diary(db,actor);var entry=Entry(actor);
        Assert.Equal(DiaryWriteOutcome.Committed,await store.CreateAsync(entry));Assert.Equal(DiaryWriteOutcome.Conflict,await store.CreateAsync(entry));
        var read=await store.ReadAsync("learning",Day);Assert.NotNull(read.Entry);Assert.Equal(Day,read.Entry.Date);Assert.Equal(entry.Plans[0].Id,Assert.Single(read.Entry.Plans).Id);
        entry.EditReflection("Edited","Preserved date");Assert.Equal(DiaryWriteOutcome.Committed,await store.EditAsync(entry));Assert.Equal(new[]{Day},(await store.CalendarAsync("learning",Day,Day)).Dates);
        Assert.Equal(DiaryWriteOutcome.Committed,await store.DeleteAsync("learning",Day));Assert.Equal(DiaryReadState.NotFound,(await store.ReadAsync("learning",Day)).State);Assert.Equal(DiaryWriteOutcome.Committed,await store.CreateAsync(entry));
        Assert.Equal(DiaryReadState.Denied,(await store.ReadAsync("hobbies",Day)).State);Assert.Equal(DiaryReadState.Denied,(await store.ReadAsync("social",Day)).State);
    }
    [LocalPostgresFact]
    public async Task ForeignInactiveMinorAndWrongModuleActorsCannotAccessEitherPrivateStore()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var portfolio=(await f.Portfolio(db,owner).CreateAsync(PortfolioTemplate.Developer)).Portfolio!;await f.Diary(db,owner).CreateAsync(Entry(owner));
        var other=await f.Groups.SeedAsync("learning");await using var foreign=f.Groups.Database.OpenAccounts();Assert.Equal(PortfolioOperationState.NotFound,(await f.Portfolio(foreign,other).OpenAsync(portfolio.Id)).State);Assert.Equal(DiaryReadState.NotFound,(await f.Diary(foreign,other).ReadAsync("learning",Day)).State);Assert.Equal(DiaryWriteOutcome.Denied,await f.Diary(foreign,other).EditAsync(Entry(owner)));
        foreach(var actor in new[]{await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true),await f.Groups.SeedAsync("hobbies")})
        {await using var denied=f.Groups.Database.OpenAccounts();Assert.Equal(PortfolioOperationState.Denied,(await f.Portfolio(denied,actor).CreateAsync(PortfolioTemplate.Developer)).State);Assert.Equal(DiaryWriteOutcome.Denied,await f.Diary(denied,actor).CreateAsync(Entry(actor)));}
        await Revoke(f,owner);Assert.Equal(PortfolioOperationState.Denied,(await f.Portfolio(db,owner).OpenAsync(portfolio.Id)).State);Assert.Equal(DiaryReadState.Denied,(await f.Diary(db,owner).ReadAsync("learning",Day)).State);Assert.Equal(1,await f.Count(owner,false));Assert.Equal(1,await f.Count(owner,true));
        Assert.Equal(DiaryReadState.Denied,(await f.Diary(db,owner).ReadAsync("learning",Day)).State);
    }
    [LocalPostgresFact]
    public async Task IndependentLifecycleRevocationWaitsForPrivateDiaryReleaseThenDeniesBothStores()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();await f.Diary(db,actor).CreateAsync(Entry(actor));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read=f.Diary(db,actor,async(stage,ct)=>{if(stage=="release"){entered.SetResult();await release.Task.WaitAsync(ct);}}).ReadAsync("learning",Day);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var revoke=Revoke(f,actor);
        try
        {await using var observer=f.Groups.Database.OpenAccounts();using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE andril_identity.account_module_lifecycles%') AS \"Value\"").SingleAsync(limit.Token))await Task.Delay(20,limit.Token);Assert.False(revoke.IsCompleted);}
        finally{release.TrySetResult();}
        Assert.Equal(DiaryReadState.Available,(await read).State);await revoke.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(DiaryReadState.Denied,(await f.Diary(db,actor).ReadAsync("learning",Day)).State);Assert.Equal(PortfolioOperationState.Denied,(await f.Portfolio(db,actor).ListAsync()).State);
    }
    [LocalPostgresFact]
    public async Task CancellationAndInjectedFailuresRollBackBothStoresAndReleaseTheirFences()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();using var cancelled=new CancellationTokenSource();bool reached=false;
        var portfolio=f.Portfolio(db,actor,(stage,ct)=>{if(stage=="prepared"){reached=true;cancelled.Cancel();ct.ThrowIfCancellationRequested();}return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>portfolio.CreateAsync(PortfolioTemplate.Developer,cancelled.Token));Assert.True(reached);Assert.Equal(0,await f.Count(actor,false));
        var diary=f.Diary(db,actor,(stage,_)=>stage=="before-commit"?Task.FromException(new IOException("synthetic-private-content")):Task.CompletedTask);Assert.Equal(DiaryWriteOutcome.Unavailable,await diary.CreateAsync(Entry(actor)));Assert.Equal(0,await f.Count(actor,true));
        using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(10));await using var independent=f.Groups.Database.OpenAccounts();Assert.Equal(DiaryWriteOutcome.Committed,await f.Diary(independent,actor).CreateAsync(Entry(actor),bounded.Token));
    }
    [LocalPostgresFact]
    public async Task ConcurrentPortfolioUpdatesAndDiaryDuplicateCreationAreSerializedWithoutLostData()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();var id=(await f.Portfolio(first,actor).CreateAsync(PortfolioTemplate.Developer)).Portfolio!.Id;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a=f.Portfolio(first,actor,async(stage,ct)=>{if(stage=="before-commit"){entered.SetResult();await release.Task.WaitAsync(ct);}}).SaveAsync(id,PortfolioSaveChange.Add(new(Guid.NewGuid(),"A","First")));await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var b=f.Portfolio(second,actor).SaveAsync(id,PortfolioSaveChange.Add(new(Guid.NewGuid(),"B","Second")));release.SetResult();Assert.Equal(PortfolioOperationState.Available,(await a).State);Assert.Equal(PortfolioOperationState.Available,(await b.WaitAsync(TimeSpan.FromSeconds(10))).State);Assert.Equal(2,(await f.Portfolio(first,actor).OpenAsync(id)).Portfolio!.Projects.Count);
        var writes=await Task.WhenAll(f.Diary(first,actor).CreateAsync(Entry(actor)),f.Diary(second,actor).CreateAsync(Entry(actor)));Assert.Equal(new[]{DiaryWriteOutcome.Committed,DiaryWriteOutcome.Conflict},writes.Order());Assert.Equal(1,await f.Count(actor,true));
    }
    private sealed class LostAck:DbTransactionInterceptor
    {public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)=>Task.FromException(new IOException("synthetic-lost-ack"));}
    [LocalPostgresFact]
    public async Task AmbiguousCommitsDoNotReleaseContentOrReplayEitherDocumentMutation()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var a=f.Groups.Database.OpenAccounts(new LostAck());await using var b=f.Groups.Database.OpenAccounts(new LostAck());
        var result=await f.Portfolio(a,actor).CreateAsync(PortfolioTemplate.Creative);Assert.Equal(PortfolioOperationState.CommitUnknown,result.State);Assert.Null(result.Portfolio);Assert.Equal(DiaryWriteOutcome.CommitUnknown,await f.Diary(b,actor).CreateAsync(Entry(actor)));Assert.Equal(1,await f.Count(actor,false));Assert.Equal(1,await f.Count(actor,true));
    }
    [LocalPostgresFact]
    public async Task RuntimeRolesCannotCrossDocumentIdentityOrOwnershipBoundariesAndForceRlsRemainsRequired()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();
        foreach(var item in new[]{(f.PortfolioRole,"SELECT * FROM andril_diary.module_diary_entries"),(f.DiaryRole,"SELECT * FROM andril_learning.learning_portfolios"),(f.PortfolioRole,"SELECT * FROM andril_identity.accounts"),(f.DiaryRole,"UPDATE andril_diary.module_diary_entries SET owner_module_profile_id=gen_random_uuid()")})
        {await using var tx=await db.Database.BeginTransactionAsync();await BaseFixture.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{item.Item1},true)");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(item.Item2));Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);await tx.RollbackAsync();}
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE andril_diary.module_diary_entries NO FORCE ROW LEVEL SECURITY");Assert.Equal(DiaryWriteOutcome.Denied,await f.Diary(db,actor).CreateAsync(Entry(actor)));await db.Database.ExecuteSqlRawAsync("ALTER TABLE andril_diary.module_diary_entries FORCE ROW LEVEL SECURITY");Assert.Equal(DiaryWriteOutcome.Committed,await f.Diary(db,actor).CreateAsync(Entry(actor)));
    }
    [LocalPostgresFact]
    public async Task ControlledHttpUsesCurrentServerIdentityAndPreservesPrivateCrudAndLiteralDates()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        using(var disabled=new SharedGroupsHttpHost(f.Groups))using(var client=disabled.CreateClient(new(){BaseAddress=new Uri("https://localhost")}))
        {client.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/learning/portfolios")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/learning/diary/2024-02-29")).StatusCode);}
        using var host=new SharedGroupsHttpHost(f.Groups,learningPortfolioRole:f.PortfolioRole,learningDiaryRole:f.DiaryRole);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/api/learning/portfolios")).StatusCode);
        http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync("/api/learning/portfolios")).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync("/api/learning/portfolios",new{template=1,owner=actor.Profile})).StatusCode);using var created=await http.PostAsJsonAsync("/api/learning/portfolios",new{template=1});Assert.Equal(HttpStatusCode.OK,created.StatusCode);Assert.True(created.Headers.CacheControl!.NoStore);using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=json.RootElement.GetProperty("id").GetGuid();Assert.Equal(HttpStatusCode.OK,(await http.PostAsJsonAsync($"/api/learning/portfolios/{id}/projects",new{title="Project",description="Private",mediaReferences=Array.Empty<string>(),externalLinks=Array.Empty<string>()})).StatusCode);Assert.Equal(HttpStatusCode.OK,(await http.GetAsync($"/api/learning/portfolios/{id}/preview")).StatusCode);
        var content=new{reflection=new{title="Owner",body="Private diary"},plans=Array.Empty<object>()};Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync("/api/learning/diary/not-a-date",content)).StatusCode);Assert.Equal(HttpStatusCode.OK,(await http.PostAsJsonAsync("/api/learning/diary/2024-02-29",content)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await http.PostAsJsonAsync("/api/learning/diary/2024-02-29",content)).StatusCode);using var read=await http.GetAsync("/api/learning/diary/2024-02-29");Assert.Equal(HttpStatusCode.OK,read.StatusCode);Assert.True(read.Headers.CacheControl!.NoStore);Assert.Contains("Private diary",await read.Content.ReadAsStringAsync());
        var foreign=await f.Groups.SeedAsync("learning");http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+foreign.Subject);Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"/api/learning/portfolios/{id}")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync("/api/learning/diary/2024-02-29")).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        await Revoke(f,actor);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync($"/api/learning/portfolios/{id}")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync("/api/learning/diary/2024-02-29")).StatusCode);Assert.Equal(1,await f.Count(actor,false));Assert.Equal(1,await f.Count(actor,true));
    }
}
