using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningPrivateCvRuntimePostgresTests
{
    private sealed class Policies : IPolicyCatalogProvider
    {
        public PolicyCatalog GetCurrent() => new(new("fixture-terms-v1","https://example.invalid/terms","en"),
            new("fixture-privacy-v1","https://example.invalid/privacy","en"),new("fixture-community-v1","https://example.invalid/community","en"));
    }
    private sealed class Fixture(LearningEligibilityDatabase isolated) : IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups => isolated.Groups;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(await LearningEligibilityDatabase.CreateAsync());
            try
            {
                await using var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(f.Groups.Database.ConnectionString).Options);
                await db.Database.MigrateAsync();
                await db.Database.ExecuteSqlRawAsync(LearningCvRuntimePermissions.GrantSql(f.Groups.Role));
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal IsolatedLearningCvRepository Store(AccountsDbContext accounts, SharedGroupsDatabase.Actor actor,
            Func<string,CancellationToken,Task>? checkpoint = null, string? role = null)
        {
            var profiles = new PrivateProfileService(actor,actor,new AccountRepository(accounts),new PrivateProfileRepository(accounts),new Policies(),TimeProvider.System);
            return new(new LearningOwnerDocumentTransaction(accounts,actor,profiles,role??Groups.Role)) { TestCheckpoint=checkpoint };
        }
        internal async Task<long> Count(SharedGroupsDatabase.Actor actor)
        {
            await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();
            await Bind(db,actor);
            // Fixture-only reversible reactivation verifies the physical row while preserving
            // FORCE RLS. The inactive owner's runtime visibility must remain denied afterward.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning' AND state='inactive'");
            var count=await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.learning_cvs").SingleAsync();
            await tx.RollbackAsync();return count;
        }
        internal static Task Bind(AccountsDbContext db,SharedGroupsDatabase.Actor actor) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{actor.Subject.ToString()},true),set_config('andril.account',{actor.Account.ToString()},true),set_config('andril.module','learning',true)");
        public ValueTask DisposeAsync()=>isolated.DisposeAsync();
    }
    private static async Task Revoke(Fixture f,SharedGroupsDatabase.Actor actor)
    {
        await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Fixture.Bind(db,actor);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");
        await tx.CommitAsync();
    }
    [LocalPostgresFact]
    public async Task RealM2SerializableScopePersistsReopensAndPreservesPrivateCvContent()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");Guid id;
        await using(var accounts=f.Groups.Database.OpenAccounts())
        {
            var store=f.Store(accounts,actor);var created=await store.CreateAsync(CvTemplate.Modern);Assert.Equal(CvOperationState.Available,created.State);id=created.Cv!.Id;
            var section=created.Cv.GetSection(CvSectionCategory.Projects);
            var entry=CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle","Private project"}});
            Assert.Equal(CvOperationState.Available,(await store.SaveAsync(id,CvSaveChange.ReviewedFields(section.Category,[entry]))).State);
            Assert.Equal(CvOperationState.Available,(await store.SaveAsync(id,CvSaveChange.Template(CvTemplate.Creative))).State);
        }
        await using var reopened=f.Groups.Database.OpenAccounts();var result=await f.Store(reopened,actor).OpenAsync(id);
        Assert.Equal(CvOperationState.Available,result.State);Assert.Equal(CvTemplate.Creative,result.Cv!.Template);
        Assert.Single(result.Cv.GetSection(CvSectionCategory.Projects).Entries);Assert.False(result.Cv.IncludeContactInformation);
        Assert.Equal(1,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task OtherAccountsOtherModulesInactiveAndOutdatedPolicyCannotAccessPrivateCvs()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        await using var owner=f.Groups.Database.OpenAccounts();var created=await f.Store(owner,actor).CreateAsync(CvTemplate.Modern);Assert.NotNull(created.Cv);
        var other=await f.Groups.SeedAsync("learning");await using var otherDb=f.Groups.Database.OpenAccounts();
        Assert.Equal(CvOperationState.NotFound,(await f.Store(otherDb,other).OpenAsync(created.Cv.Id)).State);
        foreach(var denied in new[]{await f.Groups.SeedAsync("hobbies"),await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true)})
        {await using var db=f.Groups.Database.OpenAccounts();Assert.Equal(CvOperationState.Denied,(await f.Store(db,denied).CreateAsync(CvTemplate.Modern)).State);}
        await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_identity.account_policy_records WHERE account_id={actor.Account} AND action='privacy_acknowledged'");
        Assert.Equal(CvOperationState.Denied,(await f.Store(owner,actor).OpenAsync(created.Cv.Id)).State);
    }
    [LocalPostgresFact]
    public async Task CancellationAndPreparationFailureRollBackAndReleaseTheExistingSubjectFence()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        await using var accounts=f.Groups.Database.OpenAccounts();using var cancellation=new CancellationTokenSource();bool reached=false;
        var store=f.Store(accounts,actor,(stage,ct)=>{if(stage=="prepared"){reached=true;cancellation.Cancel();ct.ThrowIfCancellationRequested();}return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.CreateAsync(CvTemplate.Modern,cancellation.Token));Assert.True(reached);Assert.Equal(0,await f.Count(actor));
        var failed=f.Store(accounts,actor,(stage,_)=>stage=="prepared"?Task.FromException(new IOException("synthetic-private-payload")):Task.CompletedTask);
        Assert.Equal(CvOperationState.Unavailable,(await failed.CreateAsync(CvTemplate.Modern)).State);Assert.Equal(0,await f.Count(actor));
        using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var independent=f.Groups.Database.OpenAccounts();Assert.Equal(CvOperationState.Available,(await f.Store(independent,actor).CreateAsync(CvTemplate.Modern,bounded.Token)).State);
    }
    [LocalPostgresFact]
    public async Task IndependentRevocationWaitsForTheAuthorizedTransactionAndThenDeniesWithoutDeletingContent()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        await using var accounts=f.Groups.Database.OpenAccounts();var store=f.Store(accounts,actor);var cv=(await store.CreateAsync(CvTemplate.Modern)).Cv!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=f.Store(accounts,actor,async(stage,ct)=>{if(stage=="read-release"){entered.SetResult();await release.Task.WaitAsync(ct);}}).PreviewAsync(cv.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revoking=Revoke(f,actor);
        try
        {
            // Observe PostgreSQL's actual lock wait, not an assumed Task scheduling delay.
            await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE andril_identity.account_module_lifecycles%') AS \"Value\"").SingleAsync(deadline.Token))
                await Task.Delay(20,deadline.Token);
            Assert.False(revoking.IsCompleted);
        }
        finally{release.TrySetResult();}
        Assert.Equal(CvOperationState.Available,(await reading).State);await revoking.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CvOperationState.Denied,(await store.PreviewAsync(cv.Id)).State);Assert.Equal(1,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task IndependentConcurrentUpdatesSerializeAndPreserveUnrelatedChanges()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();var cv=(await f.Store(first,actor).CreateAsync(CvTemplate.Modern)).Cv!;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a=f.Store(first,actor,async(stage,ct)=>{if(stage=="before-commit"){entered.SetResult();await release.Task.WaitAsync(ct);}}).SaveAsync(cv.Id,CvSaveChange.Template(CvTemplate.Professional));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var b=f.Store(second,actor).SaveAsync(cv.Id,CvSaveChange.ContactConsent(true));release.SetResult();
        Assert.Equal(CvOperationState.Available,(await a).State);Assert.Equal(CvOperationState.Available,(await b.WaitAsync(TimeSpan.FromSeconds(10))).State);
        var read=(await f.Store(first,actor).OpenAsync(cv.Id)).Cv!;Assert.Equal(CvTemplate.Professional,read.Template);Assert.True(read.IncludeContactInformation);
    }
    [LocalPostgresFact]
    public async Task RuntimeCannotReadIdentityTablesMutateOwnershipOrDeleteDocumentsAndRestoresRole()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var accounts=f.Groups.Database.OpenAccounts();
        var original=await accounts.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        Assert.Equal(CvOperationState.Available,(await f.Store(accounts,actor).CreateAsync(CvTemplate.Modern)).State);
        Assert.Equal(original,await accounts.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.Equal("",await accounts.Database.SqlQueryRaw<string>("SELECT coalesce(current_setting('andril.subject',true),'') AS \"Value\"").SingleAsync());
        foreach(var forbidden in new[]{"SELECT * FROM andril_identity.accounts","DELETE FROM andril_learning.learning_cvs","UPDATE andril_learning.learning_cvs SET owner_learning_profile_id=gen_random_uuid()"})
        {
            await using var tx=await accounts.Database.BeginTransactionAsync();await Fixture.Bind(accounts,actor);await accounts.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Groups.Role},true)");
            var error=await Assert.ThrowsAsync<PostgresException>(()=>accounts.Database.ExecuteSqlRawAsync(forbidden));Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);
            await tx.RollbackAsync();
        }
        // A migration owner cannot be substituted as the runtime role.
        Assert.Equal(CvOperationState.Denied,(await f.Store(accounts,actor,role:original).CreateAsync(CvTemplate.Modern)).State);
    }
    private sealed class LostCommitAcknowledgement:DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData eventData,CancellationToken cancellationToken=default)=>Task.FromException(new IOException("synthetic-lost-ack"));
    }
    [LocalPostgresFact]
    public async Task AmbiguousCommitDoesNotReturnPrivateContentOrAutomaticallyReplayTheWrite()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var accounts=f.Groups.Database.OpenAccounts(new LostCommitAcknowledgement());
        var result=await f.Store(accounts,actor).CreateAsync(CvTemplate.Modern);Assert.Equal(CvOperationState.CommitUnknown,result.State);Assert.Null(result.Cv);Assert.Equal(1,await f.Count(actor));
    }
    [LocalPostgresFact]
    public async Task ProductionHttpRequiresConfirmedOwnerUsesPrivateProjectionAndRechecksLifecycle()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");
        using var server=new SharedGroupsHttpHost(f.Groups,learningPrivateCv:true);
        using var client=server.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/learning/cvs")).StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","unconfirmed-"+actor.Subject);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/learning/cvs")).StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","confirmed-"+actor.Subject);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/learning/cvs",new{template=1,owner=actor.Profile})).StatusCode);
        using var created=await client.PostAsJsonAsync("/api/learning/cvs",new{template=1});Assert.Equal(HttpStatusCode.OK,created.StatusCode);Assert.True(created.Headers.CacheControl!.NoStore);
        using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());var id=json.RootElement.GetProperty("id").GetGuid();
        using var preview=await client.GetAsync($"/api/learning/cvs/{id}/preview");Assert.Equal(HttpStatusCode.OK,preview.StatusCode);Assert.True(preview.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync($"/api/learning/cvs/{id}/pdf")).StatusCode);
        await Revoke(f,actor);Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/learning/cvs/{id}/preview")).StatusCode);
        Assert.Equal(1,await f.Count(actor));
    }
}
