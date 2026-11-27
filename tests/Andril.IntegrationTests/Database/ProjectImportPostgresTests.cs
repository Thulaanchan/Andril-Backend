using System.Text;
using System.Text.Json;
using Andril.Learning.CV;
using Andril.Learning.Portfolio;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class ProjectImportPostgresTests
{
    internal sealed record Seed(LearningPortfolio Portfolio, LearningCv Cv, PortfolioProject A, PortfolioProject B);
    internal static async Task<Seed> Data(ProjectImportPostgresFixture f)
    {
        var p = (await f.Shared.Service.CreateAsync(PortfolioTemplate.Developer)).Portfolio!;
        var cv = (await f.Shared.Shared.Service.CreateAsync(CvTemplate.Creative)).Cv!;
        Assert.NotNull(p); Assert.NotNull(cv);
        var a = new PortfolioProject(Guid.NewGuid(), "SOURCE-A", "DESCRIPTION-A", ["https://media.invalid/NEVER-IMPORT"], ["https://link.invalid/NEVER-IMPORT"]);
        var b = new PortfolioProject(Guid.NewGuid(), "SOURCE-B", null);
        Assert.Equal(PortfolioOperationState.Available, (await f.Shared.Service.AddProjectAsync(p.Id, a)).State);
        Assert.Equal(PortfolioOperationState.Available, (await f.Shared.Service.AddProjectAsync(p.Id, b)).State);
        return new(p, cv, a, b);
    }
    private static ProjectImportReview Review(ProjectImportReviewResult r) { Assert.Equal(ProjectImportState.Available, r.State); return Assert.IsType<ProjectImportReview>(r.Review); }
    private static async Task<LearningCv> Cv(ProjectImportPostgresFixture f, Guid id) { var r = await f.Shared.Shared.Service.ReopenAsync(id); Assert.Equal(CvOperationState.Available, r.State); return r.Cv!; }
    private static IReadOnlyList<CvSection.Entry> Entries(LearningCv cv) => cv.Sections.Single(x => x.Category == CvSectionCategory.Projects).Entries;

    [LocalPostgresFact]
    public async Task SingleSelectionRequiresReviewedTokenAndExplicitConfirmationBeforeAppending()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f);
        Assert.Equal(ProjectImportState.NotFound, (await f.Service.ConfirmAsync(Guid.NewGuid(), true)).State);
        var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(s.A.Title, Assert.Single(r.Projects).Title); Assert.Equal(s.A.Description, r.Projects[0].Description);
        Assert.Empty(Entries(await Cv(f, s.Cv.Id))); Assert.Equal(ProjectImportState.ConfirmationRequired, (await f.Service.ConfirmAsync(r.Id, false)).State); Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
        Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State);
        var entry = Assert.Single(Entries(await Cv(f, s.Cv.Id))); Assert.Equal("Title: SOURCE-A\nDescription: DESCRIPTION-A", entry.Text); Assert.DoesNotContain("NEVER-IMPORT", entry.Text);
    }
    [LocalPostgresFact]
    public async Task MultipleSelectionsAppendInReviewedOrderAndPreserveAllExistingContentAndSource()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var section = s.Cv.Sections.Single(x => x.Category == CvSectionCategory.Projects); var manual = new CvSection.Entry(Guid.NewGuid(), section.Id, "EXISTING");
        await f.Shared.Shared.Service.SaveSectionAsync(s.Cv.Id, section.Id, section.Category, [manual]);
        var other = s.Cv.Sections.Single(x => x.Category == CvSectionCategory.Education); await f.Shared.Shared.Service.SaveSectionAsync(s.Cv.Id, other.Id, other.Category, [new(Guid.NewGuid(), other.Id, "KEEP-EDUCATION")]);
        await using var beforeDb = await f.BoundOwner(); var before = await beforeDb.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(x => x.PortfolioId == s.Portfolio.Id);
        var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.B.Id, s.A.Id])); Assert.Equal(new[] { s.B.Id, s.A.Id }, r.Projects.Select(x => x.Id));
        Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State); var cv = await Cv(f, s.Cv.Id); Assert.Equal(manual.Id, Entries(cv)[0].Id); Assert.Equal("EXISTING", Entries(cv)[0].Text); Assert.Contains("SOURCE-B", Entries(cv)[1].Text); Assert.Contains("SOURCE-A", Entries(cv)[2].Text);
        Assert.Equal(CvTemplate.Creative, cv.Template); Assert.False(cv.IncludeContactInformation); Assert.Equal("KEEP-EDUCATION", cv.Sections.Single(x => x.Category == CvSectionCategory.Education).Entries.Single().Text);
        await using var afterDb = await f.BoundOwner(); var after = await afterDb.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(x => x.PortfolioId == s.Portfolio.Id); Assert.Equal(before.Projects, after.Projects); Assert.Equal(before.ContentVersion, after.ContentVersion);
    }
    [LocalPostgresFact]
    public async Task DurableDuplicateIdentityRejectsNewReviewsWithoutUsingTextEquality()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var a = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(a.Id, true)).State);
        var again = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(ProjectImportState.Duplicate, (await f.Service.ConfirmAsync(again.Id, true)).State); Assert.Single(Entries(await Cv(f, s.Cv.Id)));
        await f.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.B.Id, s.A.Title, s.A.Description); var different = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.B.Id])); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(different.Id, true)).State); Assert.Equal(2, Entries(await Cv(f, s.Cv.Id)).Count);
    }
    [LocalPostgresFact]
    public async Task ConcurrentConfirmationsOnSeparateConnectionsCommitExactlyOneOrigin()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var a = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); var b = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]));
        var results = await Task.WhenAll(f.Repository().ConfirmAsync(a.Id, true), f.Repository().ConfirmAsync(b.Id, true)); Assert.Single(results, x => x.State == ProjectImportState.Committed); Assert.Single(results, x => x.State == ProjectImportState.Duplicate); Assert.Single(Entries(await Cv(f, s.Cv.Id)));
        await using var db = await f.BoundOwner(); Assert.Single(await db.Set<LearningProjectImportOriginRow>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task SameSourceProjectCanBeImportedIntoAnotherOwnedCv()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var other = (await f.Shared.Shared.Service.CreateAsync(CvTemplate.Modern)).Cv!;
        foreach (var id in new[] { s.Cv.Id, other.Id }) { var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, id, [s.A.Id])); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State); Assert.Single(Entries(await Cv(f, id))); }
    }
    [LocalPostgresFact]
    public async Task InvalidOrDuplicateSelectionsCreateNoReviewAndNoMutation()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f);
        foreach (var ids in new Guid[][] { [], [Guid.Empty], [s.A.Id, s.A.Id], [Guid.NewGuid()], [s.A.Id, Guid.NewGuid()] })
        { var r = await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, ids); Assert.Equal(ProjectImportState.InvalidSelection, r.State); Assert.Null(r.Review); }
        await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<LearningProjectImportReviewRow>().ToListAsync()); Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
    }
    [LocalPostgresFact]
    public async Task ForeignSourceDestinationAndReviewCannotBeUsedAcrossAccounts()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var foreign = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Shared.Shared.Shared.Database, new(foreign.Account, foreign.Profiles["learning"]), foreign.Subject);
        var p = (await f.Shared.Repository(foreign).CreateAsync(PortfolioTemplate.Developer)).Portfolio!; var cv = (await f.Shared.Shared.Repository(foreign).CreateAsync(CvTemplate.Modern)).Cv!; Assert.NotNull(p); Assert.NotNull(cv);
        Assert.Equal(ProjectImportState.NotFound, (await f.Service.ReviewAsync(p.Id, s.Cv.Id, [s.A.Id])).State); Assert.Equal(ProjectImportState.NotFound, (await f.Service.ReviewAsync(s.Portfolio.Id, cv.Id, [s.A.Id])).State);
        var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(ProjectImportState.NotFound, (await f.Repository(foreign).ConfirmAsync(r.Id, true)).State); Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
    }
    [LocalPostgresFact]
    public async Task ForgedNonLearningProfileEvidenceFailsClosed()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f);
        foreach (var id in f.Owner.Profiles.Where(x => x.Key != "learning").Select(x => x.Value))
        { var fake = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account); fake.Profiles["learning"] = id; var r = await f.Repository(fake).ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]); Assert.Equal(ProjectImportState.Denied, r.State); Assert.Null(r.Review); }
    }
    [LocalPostgresFact]
    public async Task InactiveUnavailableAndUnauthenticatedAuthorityNeverImports()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]));
        await f.Shared.Shared.Shared.Lifecycle("learning", false); Assert.Equal(ProjectImportState.Denied, (await f.Service.ConfirmAsync(r.Id, true)).State); await f.Shared.Shared.Shared.Lifecycle("learning", true);
        f.Owner.Unavailable = true; Assert.Equal(ProjectImportState.Unavailable, (await f.Service.ConfirmAsync(r.Id, true)).State); f.Owner.Unavailable = false;
        f.Owner.Unauthenticated = true; Assert.Equal(ProjectImportState.Denied, (await f.Service.ConfirmAsync(r.Id, true)).State); f.Owner.Unauthenticated = false; Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
    }
    [LocalPostgresFact]
    public async Task AuthorizationRevocationBeforeCommitRollsBackCvLedgerAndConfirmation()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]));
        var repo = f.Repository(checkpoint: (phase, _) => { if (phase == "before-commit") f.Owner.Denied = true; return Task.CompletedTask; });
        Assert.Equal(ProjectImportState.Denied, (await repo.ConfirmAsync(r.Id, true)).State); f.Owner.Denied = false; Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
        await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<LearningProjectImportOriginRow>().ToListAsync()); Assert.False((await db.Set<LearningProjectImportReviewRow>().SingleAsync()).Confirmed);
    }
    [LocalPostgresFact]
    public async Task SourceEditsAndChangeThenRevertInvalidateReviewedVersion()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]));
        await f.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.A.Id, "CHANGED", s.A.Description); Assert.Equal(ProjectImportState.StaleReview, (await f.Service.ConfirmAsync(r.Id, true)).State);
        await f.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.A.Id, s.A.Title, s.A.Description, s.A.MediaReferences, s.A.ExternalLinks); Assert.Equal(ProjectImportState.StaleReview, (await f.Service.ConfirmAsync(r.Id, true)).State); Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
    }
    [LocalPostgresFact]
    public async Task IdenticalExplicitSaveDoesNotInvalidateContentVersion()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id]));
        await using var beforeDb = await f.BoundOwner(); var before = await beforeDb.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(x => x.PortfolioId == s.Portfolio.Id);
        Assert.Equal(PortfolioOperationState.Available, (await f.Shared.Service.SelectTemplateAsync(s.Portfolio.Id, PortfolioTemplate.Developer)).State);
        await using var afterDb = await f.BoundOwner(); var after = await afterDb.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(x => x.PortfolioId == s.Portfolio.Id);
        Assert.Equal(before.ContentVersion, after.ContentVersion); Assert.Equal(before.Projects, after.Projects);
        Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State);
    }
    [LocalPostgresFact]
    public async Task CancellationAfterPreparedImportRollsBackAllWritesAndReleasesLocks()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); using var cts = new CancellationTokenSource(); var reached = false;
        var repo = f.Repository(checkpoint: (phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ConfirmAsync(r.Id, true, cts.Token)); Assert.True(reached); Assert.Empty(Entries(await Cv(f, s.Cv.Id))); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State);
    }
    [LocalPostgresFact]
    public async Task InfrastructureFailureAfterPreparedImportRollsBackAllWrites()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id, s.B.Id]));
        var repo = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new IOException("SYNTHETIC-PRIVATE-ERROR")) : Task.CompletedTask);
        Assert.Equal(ProjectImportState.Unavailable, (await repo.ConfirmAsync(r.Id, true)).State); Assert.Empty(Entries(await Cv(f, s.Cv.Id))); await using var db = await f.BoundOwner(); Assert.Empty(await db.Set<LearningProjectImportOriginRow>().ToListAsync()); Assert.False((await db.Set<LearningProjectImportReviewRow>().SingleAsync()).Confirmed);
    }
    [LocalPostgresFact]
    public async Task ImportedUnclassifiedContentRemainsWithheldFromCvPreviewAndPdfEvenWithConsent()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); await f.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.A.Id, "SYNTHETIC-PRIVATE me@example.test <script>secret</script>", "PRIVATE-DESCRIPTION");
        var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal("{}", JsonSerializer.Serialize(r)); Assert.DoesNotContain("SYNTHETIC", r.ToString()); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State);
        foreach (var consent in new[] { false, true })
        { await f.Shared.Shared.Service.SetContactConsentAsync(s.Cv.Id, consent); var html = (await f.Shared.Shared.Service.PreviewAsync(s.Cv.Id)).Preview!.Html; var pdf = Encoding.ASCII.GetString((await f.Shared.Shared.Service.ExportPdfAsync(s.Cv.Id)).Document!.Bytes); foreach (var marker in new[] { "SYNTHETIC", "example.test", "secret", "PRIVATE-DESCRIPTION" }) { Assert.DoesNotContain(marker, html); Assert.DoesNotContain(marker, pdf); } }
    }
    [LocalPostgresFact]
    public async Task RestrictedRuntimeEnforcesRlsReadOnlySourceAndDurableUniqueLedger()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); await f.Service.ConfirmAsync(r.Id, true);
        await using var db = f.OpenRuntime(); await CvPostgresFixture.Bind(db, f.Owner);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT NOT pg_has_role(current_user,'pg_signal_backend','MEMBER') AS \"Value\"").SingleAsync());
        foreach (var sql in new[] { "UPDATE andril_learning.learning_portfolios SET projects='[]'::jsonb", "UPDATE andril_learning.learning_portfolios SET content_version=gen_random_uuid()", "UPDATE andril_learning.learning_cvs SET include_contact_information=true", "DELETE FROM andril_learning.learning_project_import_origins", "UPDATE andril_learning.learning_project_import_reviews SET projects='[]'::jsonb", "SELECT * FROM andril_identity.accounts" })
        { var e = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql)); Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState); }
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO andril_learning.learning_project_import_origins SELECT * FROM andril_learning.learning_project_import_origins")); Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState); Assert.Equal("pk_learning_project_import_origins", duplicate.ConstraintName);
        foreach (var table in new[] { "learning_project_import_reviews", "learning_project_import_origins" }) Assert.True(await db.Database.SqlQuery<bool>($"SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid={"andril_learning." + table}::regclass").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('andril.subject','',false),set_config('andril.account','',false)"); Assert.Empty(await db.Set<LearningProjectImportReviewRow>().ToListAsync()); Assert.Empty(await db.Set<LearningProjectImportOriginRow>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task MigrationRollbackReapplyBackfillsVersionAndPreservesExistingPortfolioCvAndM2History()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); await using var db = await f.BoundOwner(); var before = (await db.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync()).Projects;
        await using var accounts = f.Shared.Shared.Shared.Database.Open(); var history = (await accounts.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.GetService<IMigrator>().MigrateAsync("20261126110000_LearningPortfolioPersistence"); Assert.Equal(before, await db.Database.SqlQueryRaw<string>("SELECT projects::text AS \"Value\" FROM andril_learning.learning_portfolios").SingleAsync());
        await db.Database.MigrateAsync(); var restored = await db.Set<LearningPortfolioRow>().AsNoTracking().SingleAsync(); Assert.Equal(before, restored.Projects); Assert.NotEqual(Guid.Empty, restored.ContentVersion); Assert.Equal(history, (await accounts.Database.GetAppliedMigrationsAsync()).ToArray()); Assert.Equal(s.Cv.Id, (await Cv(f, s.Cv.Id)).Id);
    }
    [LocalPostgresFact]
    public async Task SourceMutationCannotOvertakeConfirmationWithinTheExistingOwnerFence()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = f.Repository(checkpoint: async (phase, ct) => { if (phase == "before-commit") { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); } });
        var confirm = repo.ConfirmAsync(r.Id, true); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var edit = f.Shared.Service.EditProjectAsync(s.Portfolio.Id, s.A.Id, "AFTER", "after"); release.SetResult(); Assert.Equal(ProjectImportState.Committed, (await confirm).State); Assert.Equal(PortfolioOperationState.Available, (await edit).State); Assert.Contains("SOURCE-A", Entries(await Cv(f, s.Cv.Id))[0].Text);
    }
    [LocalPostgresFact]
    public async Task LostCommitAcknowledgementIsUnknownWithoutReplayAndDurableStateReconciles()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); Assert.Equal(ProjectImportState.CommitUnknown, (await f.Repository(loseAck: true).ConfirmAsync(r.Id, true)).State); Assert.Single(Entries(await Cv(f, s.Cv.Id))); Assert.Equal(ProjectImportState.Duplicate, (await f.Service.ConfirmAsync(r.Id, true)).State);
    }
    [LocalPostgresFact]
    public async Task SourceFingerprintAlsoDetectsPrivilegedFixtureChangesThatBypassVersionWriter()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); await using var db = await f.BoundOwner(); Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_portfolios SET template=2 WHERE portfolio_id={s.Portfolio.Id}")); Assert.Equal(ProjectImportState.StaleReview, (await f.Service.ConfirmAsync(r.Id, true)).State); Assert.Empty(Entries(await Cv(f, s.Cv.Id)));
    }
    [LocalPostgresFact]
    public async Task DestinationEditsAfterReviewArePreservedByAppendingToCurrentLockedCv()
    {
        await using var f = await ProjectImportPostgresFixture.Create(); var s = await Data(f); var r = Review(await f.Service.ReviewAsync(s.Portfolio.Id, s.Cv.Id, [s.A.Id])); var section = s.Cv.Sections.Single(x => x.Category == CvSectionCategory.Projects); var e = new CvSection.Entry(Guid.NewGuid(), section.Id, "NEWER-CV-EDIT"); await f.Shared.Shared.Service.SaveSectionAsync(s.Cv.Id, section.Id, section.Category, [e]); Assert.Equal(ProjectImportState.Committed, (await f.Service.ConfirmAsync(r.Id, true)).State); var entries = Entries(await Cv(f, s.Cv.Id)); Assert.Equal(e.Id, entries[0].Id); Assert.Equal("NEWER-CV-EDIT", entries[0].Text); Assert.Equal(2, entries.Count);
    }
}
