using System.Text;
using System.Text.Json;
using Andril.Learning.CV;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Actual restricted PostgreSQL export boundaries; synthetic Common authority only
/// in the existing isolated fixture. No approved detailed fields or production export claimed.</summary>
public sealed class CvPdfPostgresTests
{
    private static LearningCv Cv(CvReadResult result)
    { Assert.Equal(CvOperationState.Available, result.State); return Assert.IsType<LearningCv>(result.Cv); }

    [LocalPostgresFact]
    public async Task AllTemplatesAndPersistedConsentChoicesExportOnlyTheSharedSafeProjection()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        foreach (var s in cv.Sections) Cv(await f.Service.SaveSectionAsync(cv.Id, s.Id, s.Category, [new(Guid.NewGuid(), s.Id, "SYNTHETIC me@example.test <script>secret</script>")]));
        foreach (var template in Enum.GetValues<CvTemplate>())
        foreach (var consent in new[] { false, true, false })
        {
            Cv(await f.Service.SelectTemplateAsync(cv.Id, template)); Cv(await f.Service.SetContactConsentAsync(cv.Id, consent));
            var result = await f.Service.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Available, result.State);
            var pdf = Encoding.ASCII.GetString(result.Document!.Bytes); var html = (await f.Service.PreviewAsync(cv.Id)).Preview!.Html;
            Assert.Contains("/Count 2", pdf); Assert.Contains(template.ToString(), pdf);
            foreach (var category in Enum.GetValues<CvSectionCategory>()) { Assert.Contains(category.ToString(), pdf); Assert.Contains(category.ToString(), html); }
            foreach (var secret in new[] { "SYNTHETIC", "example.test", "script", "secret", cv.Id.ToString(), cv.OwnerLearningProfileId.ToString() }) Assert.DoesNotContain(secret, pdf);
            Assert.Equal("{}", JsonSerializer.Serialize(result));
        }
        Assert.All(Cv(await f.Service.ReopenAsync(cv.Id)).Sections, s => Assert.Single(s.Entries));
    }

    [LocalPostgresFact]
    public async Task ForeignAccountAndForgedCrossModuleEvidenceCannotExport()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        var foreign = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Shared.Database, new(foreign.Account, foreign.Profiles["learning"]), foreign.Subject);
        var denied = await f.Repository(foreign).ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.NotFound, denied.State); Assert.Null(denied.Document);
        var forged = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account); forged.Profiles["learning"] = f.Owner.Profiles["hobbies"];
        denied = await f.Repository(forged).ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Denied, denied.State); Assert.Null(denied.Document);
    }

    [LocalPostgresFact]
    public async Task InactiveUnavailableAndUnauthenticatedOwnersCannotReleasePdf()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        await f.Shared.Lifecycle("learning", false); var result = await f.Service.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Denied, result.State); Assert.Null(result.Document);
        await f.Shared.Lifecycle("learning", true);
        f.Owner.Unavailable = true; result = await f.Service.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Unavailable, result.State); Assert.Null(result.Document);
        f.Owner.Unavailable = false; f.Owner.Unauthenticated = true; result = await f.Service.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Denied, result.State); Assert.Null(result.Document);
        f.Owner.Unauthenticated = false; Assert.Equal(CvOperationState.Available, (await f.Service.ExportPdfAsync(cv.Id)).State);
    }

    [LocalPostgresFact]
    public async Task AuthorizationRevocationAtReleaseDiscardsGeneratedBytes()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var reached = false;
        var repo = f.Repository(checkpoint: (phase, _) => { if (phase == "read-release") { reached = true; f.Owner.Denied = true; } return Task.CompletedTask; });
        var result = await repo.ExportPdfAsync(cv.Id); Assert.True(reached); Assert.Equal(CvOperationState.Denied, result.State); Assert.Null(result.Document);
        f.Owner.Denied = false; Assert.Equal(CvOperationState.Available, (await f.Service.ReopenAsync(cv.Id)).State);
    }

    [LocalPostgresFact]
    public async Task CancellationAndReleaseFailuresDiscardBytesReleaseLocksAndPreserveStoredState()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); using var cts = new CancellationTokenSource(); var reached = false;
        var repo = f.Repository(checkpoint: (phase, ct) => { if (phase == "read-release") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ExportPdfAsync(cv.Id, cts.Token)); Assert.True(reached);
        repo = f.Repository(checkpoint: (_, _) => Task.FromException(new IOException("SYNTHETIC-PRIVATE-ERROR")));
        var result = await repo.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Unavailable, result.State); Assert.Null(result.Document); Assert.DoesNotContain("SYNTHETIC", result.ToString());
        Assert.Equal(CvOperationState.Available, (await f.Service.SetContactConsentAsync(cv.Id, true)).State);
        Assert.Equal(CvOperationState.Available, (await f.Service.ExportPdfAsync(cv.Id)).State);
    }

    [LocalPostgresFact]
    public async Task ExportUsesLatestPersistedTemplateAndRevokedConsentNotCallerHeldSnapshots()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        var old = Cv(await f.Service.SetContactConsentAsync(cv.Id, true));
        Cv(await f.Service.SetContactConsentAsync(cv.Id, false)); Cv(await f.Service.SelectTemplateAsync(cv.Id, CvTemplate.Creative));
        Assert.True(old.IncludeContactInformation); Assert.Equal(CvTemplate.Modern, old.Template);
        var result = await f.Service.ExportPdfAsync(cv.Id); Assert.Equal(CvOperationState.Available, result.State); var pdf = Encoding.ASCII.GetString(result.Document!.Bytes);
        Assert.Contains("Creative", pdf); Assert.DoesNotContain("Modern", pdf); Assert.False(Cv(await f.Service.ReopenAsync(cv.Id)).IncludeContactInformation);
        Assert.DoesNotContain("/Info", pdf); Assert.DoesNotContain("/Annot", pdf);
    }
}
