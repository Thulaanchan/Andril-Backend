using System.Text.Json;
using Andril.Learning.Portfolio;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Actual PostgreSQL and application service; synthetic Common authority only in
/// the isolated host. Production registration and operational permission adapter stay absent.</summary>
public sealed class PortfolioRepositoryPostgresTests
{
    private static LearningPortfolio P(PortfolioReadResult result)
    { Assert.Equal(PortfolioOperationState.Available, result.State); return Assert.IsType<LearningPortfolio>(result.Portfolio); }

    [LocalPostgresFact]
    public async Task StoredRowRoundTripsAllOriginalContentIdentitiesOrderAndTemplates()
    {
        await using var f = await PortfolioPostgresFixture.Create();
        foreach (var template in Enum.GetValues<PortfolioTemplate>())
        {
        var p = P(await f.Service.CreateAsync(template));
        p = P(await f.Service.AddProjectAsync(p.Id, new(Guid.NewGuid(), " தமிழ் e\u0301 <script>SYNTHETIC</script> ", null, ["https://example.test/media"], ["http://example.test/project"])));
        p = P(await f.Service.AddProjectAsync(p.Id, new(Guid.NewGuid())));
        await using var db = f.Shared.OpenOwner();
        await CvPostgresFixture.Bind(db, f.Owner);
        var row = await db.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(x => x.PortfolioId == p.Id);
        var copy = row.ToDomain(); Assert.Equal(p.Id, copy.Id); Assert.Equal(p.OwnerLearningProfileId, copy.OwnerLearningProfileId);
        Assert.Equal(template, copy.Template); Assert.Equal(p.Projects.Select(x => x.Id), copy.Projects.Select(x => x.Id));
        Assert.Equal(p.Projects[0].Title, copy.Projects[0].Title); Assert.Null(copy.Projects[0].Description);
        Assert.Equal(p.Projects[0].MediaReferences, copy.Projects[0].MediaReferences); Assert.Equal(p.Projects[0].ExternalLinks, copy.Projects[0].ExternalLinks);
        Assert.True(copy.IsPrivate); Assert.Equal("learning", copy.Module);
        }
    }

    [LocalPostgresFact]
    public async Task MultipleOwnerPortfoliosCreateReopenListUpdateAndDeleteIndependently()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var ids = new List<Guid>();
        foreach (var t in Enum.GetValues<PortfolioTemplate>()) { var p = P(await f.Service.CreateAsync(t)); ids.Add(p.Id); Assert.Equal(t, P(await f.Service.ReopenAsync(p.Id)).Template); }
        Assert.Equal(3, (await f.Service.ListOwnAsync()).Ids!.Count);
        Assert.Equal(PortfolioOperationState.Available, (await f.Service.DeleteAsync(ids[0])).State); Assert.Equal(PortfolioOperationState.NotFound, (await f.Service.ReopenAsync(ids[0])).State);
        Assert.Equal(PortfolioOperationState.NotFound, (await f.Service.DeleteAsync(ids[0])).State); Assert.Equal(2, (await f.Service.ListOwnAsync()).Ids!.Count);
        Assert.Equal(ids.Skip(1).OrderBy(x => x), (await f.Service.ListOwnAsync()).Ids!.OrderBy(x => x));
    }
    [LocalPostgresFact]
    public async Task ProjectContentOrderAndAllTemplatesRoundTripWithoutLossOrFetching()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        var a = new PortfolioProject(Guid.NewGuid(), " தமிழ் e\u0301 <script>secret</script> ", null, ["https://example.test/not-fetched"], ["http://127.0.0.1:1/not-fetched"]); var b = new PortfolioProject(Guid.NewGuid());
        P(await f.Service.AddProjectAsync(p.Id, a)); P(await f.Service.AddProjectAsync(p.Id, b));
        P(await f.Service.EditProjectAsync(p.Id, b.Id, "edited", "description")); P(await f.Service.ReorderProjectsAsync(p.Id, [b.Id, a.Id]));
        foreach (var t in Enum.GetValues<PortfolioTemplate>())
        { P(await f.Service.SelectTemplateAsync(p.Id, t)); var stored = P(await f.Service.ReopenAsync(p.Id)); Assert.Equal(t, stored.Template); Assert.Equal(new[] { b.Id, a.Id }, stored.Projects.Select(x => x.Id)); Assert.Equal(a.Title, stored.Projects[1].Title); Assert.Equal(a.MediaReferences, stored.Projects[1].MediaReferences); Assert.Equal(a.ExternalLinks, stored.Projects[1].ExternalLinks); Assert.Equal("description", stored.Projects[0].Description); }
    }
    [LocalPostgresFact]
    public async Task CallerHeldSnapshotsDoNotAutosaveOrReplaceConcurrentStoredContent()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); p.AddProject(new(Guid.NewGuid(), "unsaved"));
        Assert.Empty(P(await f.Service.ReopenAsync(p.Id)).Projects); var id = Guid.NewGuid(); P(await f.Service.AddProjectAsync(p.Id, new(id, "saved"))); p.SelectTemplate(PortfolioTemplate.Creative);
        var current = P(await f.Service.ReopenAsync(p.Id)); Assert.Equal(PortfolioTemplate.Developer, current.Template); Assert.Equal(id, Assert.Single(current.Projects).Id);
    }
    [LocalPostgresFact]
    public async Task ForeignAccountCannotReadEditDeleteOrEnumerateAnotherOwnersPortfolio()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        var foreign = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(f.Shared.Shared.Database, new(foreign.Account, foreign.Profiles["learning"]), foreign.Subject);
        var repo = f.Repository(foreign); Assert.Equal(PortfolioOperationState.NotFound, (await repo.OpenAsync(p.Id)).State); Assert.Empty((await repo.ListAsync()).Ids!);
        Assert.Equal(PortfolioOperationState.NotFound, (await repo.SaveAsync(p.Id, PortfolioSaveChange.Template(PortfolioTemplate.Creative))).State); Assert.Equal(PortfolioOperationState.NotFound, (await repo.DeleteAsync(p.Id)).State);
        Assert.Equal(PortfolioTemplate.Developer, P(await f.Service.ReopenAsync(p.Id)).Template);
    }
    [LocalPostgresFact]
    public async Task ForgedNonLearningAndForeignProfileEvidenceNeverGrantsLearningOwnership()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        foreach (var profile in f.Owner.Profiles.Where(p => p.Key != "learning").Select(p => p.Value).Append(Guid.NewGuid()))
        { var fake = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account); fake.Profiles["learning"] = profile; var repo = f.Repository(fake); Assert.Equal(PortfolioOperationState.Denied, (await repo.OpenAsync(p.Id)).State); Assert.Equal(PortfolioOperationState.Denied, (await repo.DeleteAsync(p.Id)).State); Assert.Null((await repo.ListAsync()).Ids); }
    }
    [LocalPostgresFact]
    public async Task DeactivationDeniesOperationsAndReactivationPreservesDocumentsAndProjects()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Creative)); var project = new PortfolioProject(Guid.NewGuid(), "keep"); P(await f.Service.AddProjectAsync(p.Id, project));
        await f.Shared.Shared.Lifecycle("learning", false);
        Assert.Equal(PortfolioOperationState.Denied, (await f.Service.ReopenAsync(p.Id)).State); Assert.Equal(PortfolioOperationState.Denied, (await f.Service.DeleteAsync(p.Id)).State); Assert.Equal(PortfolioOperationState.Denied, (await f.Service.CreateAsync(PortfolioTemplate.Developer)).State);
        await f.Shared.Shared.Lifecycle("learning", true); Assert.Equal(project.Id, Assert.Single(P(await f.Service.ReopenAsync(p.Id)).Projects).Id);
    }
    [LocalPostgresFact]
    public async Task UnavailableRestrictedAndUnauthenticatedAuthorityFailClosedWithoutMutation()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        f.Owner.Unavailable = true; var r = await f.Service.ReopenAsync(p.Id); Assert.Equal(PortfolioOperationState.Unavailable, r.State); Assert.Null(r.Portfolio); Assert.Equal(PortfolioOperationState.Unavailable, (await f.Service.DeleteAsync(p.Id)).State);
        f.Owner.Unavailable = false; f.Owner.Denied = true; Assert.Equal(PortfolioOperationState.Denied, (await f.Service.ListOwnAsync()).State);
        f.Owner.Denied = false; f.Owner.Unauthenticated = true; Assert.Equal(PortfolioOperationState.Denied, (await f.Service.CreateAsync(PortfolioTemplate.Developer)).State);
        f.Owner.Unauthenticated = false; Assert.Equal(p.Id, P(await f.Service.ReopenAsync(p.Id)).Id);
    }
    [LocalPostgresFact]
    public async Task InvalidProjectContentDuplicateIdsAndOrderRejectAtomically()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); var a = new PortfolioProject(Guid.NewGuid(), "keep"); P(await f.Service.AddProjectAsync(p.Id, a));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.AddProjectAsync(p.Id, a));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ReorderProjectsAsync(p.Id, [Guid.NewGuid()]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SelectTemplateAsync(p.Id, (PortfolioTemplate)99));
        Assert.Throws<ArgumentException>(() => PortfolioSaveChange.Edit(a.Id, "bad", null, ["file:///private"]));
        var kept = P(await f.Service.ReopenAsync(p.Id)); Assert.Equal("keep", Assert.Single(kept.Projects).Title); Assert.Equal(PortfolioTemplate.Developer, kept.Template);
    }
    [LocalPostgresFact]
    public async Task CancellationAndInjectedFailuresRollBackCreateSaveAndDelete()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); using var cts = new CancellationTokenSource(); var reached = false;
        var cancel = f.Repository(checkpoint: (phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancel.SaveAsync(p.Id, PortfolioSaveChange.Template(PortfolioTemplate.Creative), cts.Token)); Assert.True(reached);
        var fail = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new IOException("SYNTHETIC-PRIVATE")) : Task.CompletedTask);
        Assert.Equal(PortfolioOperationState.Unavailable, (await fail.CreateAsync(PortfolioTemplate.Developer)).State); Assert.Equal(PortfolioOperationState.Unavailable, (await fail.DeleteAsync(p.Id)).State);
        Assert.Equal(PortfolioTemplate.Developer, P(await f.Service.ReopenAsync(p.Id)).Template); Assert.Single((await f.Service.ListOwnAsync()).Ids!);
    }
    [LocalPostgresFact]
    public async Task ReadReleaseAndMutationCommitRevalidateCurrentAuthorization()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        foreach (var write in new[] { false, true })
        {
            var reached = false; var repo = f.Repository(checkpoint: (phase, _) => { if (phase == (write ? "before-commit" : "read-release")) { reached = true; f.Owner.Denied = true; } return Task.CompletedTask; });
            var result = write ? await repo.SaveAsync(p.Id, PortfolioSaveChange.Template(PortfolioTemplate.Creative)) : await repo.OpenAsync(p.Id);
            Assert.True(reached); Assert.Equal(PortfolioOperationState.Denied, result.State); Assert.Null(result.Portfolio); f.Owner.Denied = false;
            Assert.Equal(PortfolioTemplate.Developer, P(await f.Service.ReopenAsync(p.Id)).Template);
        }
    }
    [LocalPostgresFact]
    public async Task SeparateConnectionsSerializeConcurrentAddsWithoutLostProjects()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Repository(checkpoint: async (phase, ct) => { if (phase == "prepared") { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); } });
        var a = new PortfolioProject(Guid.NewGuid(), "first"); var b = new PortfolioProject(Guid.NewGuid(), "second"); var task = first.SaveAsync(p.Id, PortfolioSaveChange.Add(a));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var other = f.Repository().SaveAsync(p.Id, PortfolioSaveChange.Add(b)); release.SetResult();
        P(await task); P(await other); Assert.Equal(new[] { a.Id, b.Id }, P(await f.Service.ReopenAsync(p.Id)).Projects.Select(x => x.Id));
    }
    [LocalPostgresFact]
    public async Task RestrictedRuntimeUsesForcedRlsAndCannotChangeIdentityOrAccessOtherTables()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        await using var db = f.OpenRuntime(); await CvPostgresFixture.Bind(db, f.Owner); Assert.Single(await db.Set<LearningPortfolioRow>().ToListAsync());
        foreach (var sql in new[] { "UPDATE andril_learning.learning_portfolios SET owner_learning_profile_id=gen_random_uuid()", "UPDATE andril_learning.learning_portfolios SET module='hobbies'", "UPDATE andril_learning.learning_portfolios SET portfolio_id=gen_random_uuid()", "SELECT * FROM andril_learning.learning_cvs", "SELECT * FROM andril_identity.accounts", "CREATE TABLE andril_learning.portfolio_forbidden(id integer)" })
        { var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState); }
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_learning.learning_portfolios'::regclass").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('andril.account','',false),set_config('andril.subject','',false)"); Assert.Empty(await db.Set<LearningPortfolioRow>().ToListAsync());
        Assert.Equal(p.Id, P(await f.Service.ReopenAsync(p.Id)).Id);
    }
    [LocalPostgresFact]
    public async Task CorruptStoredProjectsFailClosedWithoutPrivateErrorPayloads()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        await using var owner = f.Shared.OpenOwner(); await CvPostgresFixture.Bind(owner, f.Owner);
        var affected = await owner.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_portfolios SET projects='[null]'::jsonb WHERE portfolio_id={p.Id}");
        Assert.Equal(1, affected);
        var r = await f.Service.ReopenAsync(p.Id); Assert.Equal(PortfolioOperationState.Unavailable, r.State); Assert.Null(r.Portfolio); Assert.Equal("{}", JsonSerializer.Serialize(r)); Assert.DoesNotContain(p.Id.ToString(), r.ToString());
    }
    [LocalPostgresFact]
    public async Task LostCommitAcknowledgementReturnsUnknownAndReopeningReconcilesCommittedState()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var r = await f.Repository(loseAck: true).CreateAsync(PortfolioTemplate.Creative);
        Assert.Equal(PortfolioOperationState.CommitUnknown, r.State); Assert.Null(r.Portfolio); var id = Assert.Single((await f.Service.ListOwnAsync()).Ids!); Assert.Equal(PortfolioTemplate.Creative, P(await f.Service.ReopenAsync(id)).Template);
    }
    [LocalPostgresFact]
    public async Task MigrationRollbackAndReapplicationPreserveExistingCvAndM2Schema()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var cv = await f.Shared.Service.CreateAsync(Andril.Learning.CV.CvTemplate.Creative);
        await using var db = f.Shared.OpenOwner(); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261126090000_LearningCvPersistence"); Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('andril_learning.learning_portfolios') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(Andril.Learning.CV.CvOperationState.Available, (await f.Shared.Service.ReopenAsync(cv.Cv!.Id)).State);
        await db.Database.MigrateAsync(); Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('andril_learning.learning_portfolios') IS NOT NULL AS \"Value\"").SingleAsync());
        // Reapplication intentionally does not restore fixture-only runtime grants.
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('andril_identity.accounts') IS NOT NULL AS \"Value\"").SingleAsync());
    }
}
