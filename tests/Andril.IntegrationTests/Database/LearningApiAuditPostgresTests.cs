using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.API.Controllers.Learning;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Basis=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningApiAuditPostgresTests
{
    private sealed class Fixture(Basis basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string Role{get;}="learning_import_"+Guid.NewGuid().ToString("N");private bool created;
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Basis.Create());try
            {
                await using var db=f.Groups.Database.OpenAccounts();await db.Database.ExecuteSqlRawAsync(LearningPortfolioRuntimePermissions.GrantSql(f.Groups.Role));
                using var q=new NpgsqlCommandBuilder();var role=q.QuoteIdentifier(f.Role);var sql=$"CREATE ROLE {role} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {role} TO CURRENT_USER";await db.Database.ExecuteSqlRawAsync(sql);f.created=true;await db.Database.ExecuteSqlRawAsync(LearningProjectImportRuntimePermissions.GrantSql(f.Role));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal PrivateProfileService Profiles(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>new(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),new LearningPrivateCvRuntimePostgresTests.Policies(),TimeProvider.System);
        internal ILearningCvRepository Cv(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>LearningCvPersistence.Create(db,actor,Profiles(db,actor),Groups.Role);
        internal ILearningPortfolioRepository Portfolio(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>LearningPortfolioPersistence.Create(db,actor,Profiles(db,actor),Groups.Role);
        internal ILearningProjectImportRepository Import(AccountsDbContext db,SharedGroupsDatabase.Actor actor)=>LearningProjectImportPersistence.Create(db,actor,Profiles(db,actor),Role);
        internal async Task<(Guid Cv,Guid Portfolio,Guid Project)> Seed(SharedGroupsDatabase.Actor actor)
        {
            await using var db=Groups.Database.OpenAccounts();var cv=await Cv(db,actor).CreateAsync(CvTemplate.Modern);Assert.Equal(CvOperationState.Available,cv.State);
            var portfolio=await Portfolio(db,actor).CreateAsync(PortfolioTemplate.Developer);Assert.Equal(PortfolioOperationState.Available,portfolio.State);var project=Guid.NewGuid();
            Assert.Equal(PortfolioOperationState.Available,(await Portfolio(db,actor).SaveAsync(portfolio.Portfolio!.Id,PortfolioSaveChange.Add(new(project,"PRIVATE_TITLE","PRIVATE_DESCRIPTION",[],[])))).State);return(cv.Cv!.Id,portfolio.Portfolio.Id,project);
        }
        internal SharedGroupsHttpHost Host()=>new(Groups,learningPrivateCv:true,learningPortfolioRole:Groups.Role,learningImportRole:Role);
        public async ValueTask DisposeAsync()
        {try{await basis.DisposeAsync();}finally{if(created){await using var db=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var q=new NpgsqlCommandBuilder();await using var command=new NpgsqlCommand("DROP ROLE "+q.QuoteIdentifier(Role),db);await command.ExecuteNonQueryAsync();}}}
    }
    [LocalPostgresFact]
    public async Task CvReopenAndTemplateSwitchPreserveSevenSectionsReviewedFieldsAndPrivateLegacyText()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");var seed=await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();var cv=(await f.Cv(db,actor).OpenAsync(seed.Cv)).Cv!;var section=cv.GetSection(CvSectionCategory.Projects);var entry=Guid.NewGuid();
        await f.Cv(db,actor).SaveAsync(seed.Cv,CvSaveChange.Section(section.Id,section.Category,[new(entry,section.Id,"UNCLASSIFIED_PRIVATE_MARKER")]));
        using var host=f.Host();using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        using var before=await http.GetAsync($"/api/learning/cvs/{seed.Cv}");Assert.Equal(HttpStatusCode.OK,before.StatusCode);Assert.True(before.Headers.CacheControl!.NoStore);var text=await before.Content.ReadAsStringAsync();using var body=JsonDocument.Parse(text);Assert.Equal(7,body.RootElement.GetProperty("sections").GetArrayLength());Assert.Contains("UNCLASSIFIED_PRIVATE_MARKER",text);Assert.DoesNotContain(actor.Profile.ToString(),text);Assert.DoesNotContain(actor.Account.ToString(),text);
        foreach(var template in Enum.GetValues<CvTemplate>())Assert.Equal(HttpStatusCode.OK,(await http.PutAsJsonAsync($"/api/learning/cvs/{seed.Cv}/template",new{template})).StatusCode);
        var reopened=(await f.Cv(db,actor).OpenAsync(seed.Cv)).Cv!;Assert.Equal(section.Id,reopened.GetSection(section.Category).Id);Assert.Equal(entry,Assert.Single(reopened.GetSection(section.Category).Entries).Id);Assert.Equal("UNCLASSIFIED_PRIVATE_MARKER",Assert.Single(reopened.GetSection(section.Category).Entries).Text);
        var preview=await http.GetStringAsync($"/api/learning/cvs/{seed.Cv}/preview");Assert.DoesNotContain("UNCLASSIFIED_PRIVATE_MARKER",preview);
        foreach(var bad in new object[]{new{},new{template=999},new{template=1,owner=actor.Account}})Assert.Equal(HttpStatusCode.BadRequest,(await http.PutAsJsonAsync($"/api/learning/cvs/{seed.Cv}/template",bad)).StatusCode);
    }
    [LocalPostgresFact]
    public async Task ProductionImportRequiresConfirmedOwnerReviewAndExplicitConfirmation()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");var seed=await f.Seed(actor);using var host=f.Host();using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});var request=new ProjectImportReviewRequest(seed.Portfolio,seed.Cv,[seed.Project]);const string route="/api/v1/learning/cv-imports";
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.PostAsJsonAsync(route+"/reviews",request)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+actor.Subject);var denied=await http.PostAsJsonAsync(route+"/reviews",request);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.True(denied.Headers.CacheControl!.NoStore);
        http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync(route+"/reviews",new{request.PortfolioId,request.CvId,request.ProjectIds,owner=actor.Account})).StatusCode);
        using var reviewed=await http.PostAsJsonAsync(route+"/reviews",request);Assert.Equal(HttpStatusCode.OK,reviewed.StatusCode);Assert.True(reviewed.Headers.CacheControl!.NoStore);using var json=JsonDocument.Parse(await reviewed.Content.ReadAsStringAsync());var review=json.RootElement.GetProperty("reviewId").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest,(await http.PostAsJsonAsync($"{route}/{review}/confirm",new ProjectImportConfirmRequest(false))).StatusCode);Assert.Equal(HttpStatusCode.NoContent,(await http.PostAsJsonAsync($"{route}/{review}/confirm",new ProjectImportConfirmRequest(true))).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await http.PostAsJsonAsync($"{route}/{review}/confirm",new ProjectImportConfirmRequest(true))).StatusCode);
        await using var db=f.Groups.Database.OpenAccounts();var cv=(await f.Cv(db,actor).OpenAsync(seed.Cv)).Cv!;var imported=Assert.Single(cv.GetSection(CvSectionCategory.Projects).Entries);Assert.Empty(imported.Fields);Assert.Contains("PRIVATE_TITLE",imported.Text);Assert.DoesNotContain("PRIVATE_TITLE",(await f.Cv(db,actor).PreviewAsync(seed.Cv)).Preview!.Html);Assert.Single((await f.Portfolio(db,actor).OpenAsync(seed.Portfolio)).Portfolio!.Projects);
    }
    [LocalPostgresFact]
    public async Task ProductionImportRejectsStaleSourcesAndConcurrentDuplicates()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");var seed=await f.Seed(actor);await using var first=f.Groups.Database.OpenAccounts();await using var second=f.Groups.Database.OpenAccounts();var a=f.Import(first,actor);var b=f.Import(second,actor);var review=(await a.ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).Review!;
        await f.Portfolio(first,actor).SaveAsync(seed.Portfolio,PortfolioSaveChange.Edit(seed.Project,"CHANGED","PRIVATE_DESCRIPTION",[],[]));Assert.Equal(ProjectImportState.StaleReview,(await a.ConfirmAsync(review.Id,true)).State);
        var r1=(await a.ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).Review!;var r2=(await b.ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).Review!;var results=await Task.WhenAll(a.ConfirmAsync(r1.Id,true),b.ConfirmAsync(r2.Id,true));Assert.Single(results,x=>x.State==ProjectImportState.Committed);Assert.Single(results,x=>x.State==ProjectImportState.Duplicate);Assert.Single((await f.Cv(first,actor).OpenAsync(seed.Cv)).Cv!.GetSection(CvSectionCategory.Projects).Entries);
    }
    [LocalPostgresFact]
    public async Task ImportAndCvEditingDenyForeignModuleForeignOwnerAndInactiveProfiles()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");var stranger=await f.Groups.SeedAsync("learning");var hobby=await f.Groups.SeedAsync("hobbies");var seed=await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();Assert.Equal(ProjectImportState.NotFound,(await f.Import(db,stranger).ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).State);Assert.Equal(ProjectImportState.Denied,(await f.Import(db,hobby).ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).State);
        using var host=f.Host();using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+stranger.Subject);Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"/api/learning/cvs/{seed.Cv}")).StatusCode);
        var review=(await f.Import(db,actor).ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).Review!;await using(var tx=await db.Database.BeginTransactionAsync()){await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();}
        Assert.Equal(ProjectImportState.Denied,(await f.Import(db,actor).ConfirmAsync(review.Id,true)).State);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync($"/api/learning/cvs/{seed.Cv}")).StatusCode);
    }
    [LocalPostgresFact]
    public async Task ImportCancellationRollsBackCvReviewAndOriginTogether()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");var seed=await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();var review=(await f.Import(db,actor).ReviewAsync(seed.Portfolio,seed.Cv,[seed.Project])).Review!;using var cancel=new CancellationTokenSource();bool reached=false;
        var repository=new IsolatedLearningProjectImportRepository(new LearningOwnerDocumentTransaction(db,actor,f.Profiles(db,actor),f.Role,LearningPrivateDocument.ProjectImports)){TestCheckpoint=(stage,_)=>{if(stage=="prepared"){reached=true;cancel.Cancel();}return Task.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>repository.ConfirmAsync(review.Id,true,cancel.Token));Assert.True(reached);Assert.Empty((await f.Cv(db,actor).OpenAsync(seed.Cv)).Cv!.GetSection(CvSectionCategory.Projects).Entries);Assert.Equal(ProjectImportState.Committed,(await f.Import(db,actor).ConfirmAsync(review.Id,true)).State);
    }
    [LocalPostgresFact]
    public async Task RestrictedImportRoleCannotWriteSourcesOwnershipConsentOrIdentity()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT row_security_active('andril_learning.learning_project_import_reviews') AND row_security_active('andril_learning.learning_project_import_origins') AND NOT (SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname=current_user) AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning.learning_portfolios','UPDATE') OR has_table_privilege(current_user,'andril_learning.learning_portfolios','DELETE') OR has_column_privilege(current_user,'andril_learning.learning_cvs','owner_learning_profile_id','UPDATE') OR has_column_privilege(current_user,'andril_learning.learning_cvs','include_contact_information','UPDATE') OR has_table_privilege(current_user,'andril_learning.learning_project_import_origins','DELETE') AS \"Value\"").SingleAsync());
        await tx.CreateSavepointAsync("denial");var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync("SELECT * FROM andril_identity.accounts"));Assert.Equal("42501",error.SqlState);await tx.RollbackToSavepointAsync("denial");
    }
}
