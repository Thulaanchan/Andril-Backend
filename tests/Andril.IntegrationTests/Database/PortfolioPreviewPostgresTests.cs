using System.Net;
using System.Text.Json;
using Andril.Learning.Portfolio;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PortfolioPreviewPostgresTests
{
    private static LearningPortfolio P(PortfolioReadResult result) { Assert.Equal(PortfolioOperationState.Available, result.State); return Assert.IsType<LearningPortfolio>(result.Portfolio); }
    [LocalPostgresFact]
    public async Task AllTemplatesRenderStoredProjectFieldsAndReferencesWithoutFetchingOrActivation()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        const string text = "<script>PRIVATE</script> தமிழ்"; P(await f.Service.AddProjectAsync(p.Id, new(Guid.NewGuid(), text, "description", ["http://127.0.0.1:1/not-fetched"], ["https://example.invalid/not-fetched"])));
        foreach (var t in Enum.GetValues<PortfolioTemplate>())
        { P(await f.Service.SelectTemplateAsync(p.Id, t)); var r = await f.Service.PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Available, r.State); Assert.Contains(t.ToString(), r.Preview!.Html); Assert.Contains(WebUtility.HtmlEncode(text), r.Preview.Html); Assert.DoesNotContain("<script", r.Preview.Html); Assert.DoesNotContain("href=", r.Preview.Html); Assert.DoesNotContain(p.Id.ToString(), r.Preview.Html); Assert.Equal("{}", JsonSerializer.Serialize(r)); }
        Assert.Equal(text, P(await f.Service.ReopenAsync(p.Id)).Projects[0].Title);
    }
    [LocalPostgresFact]
    public async Task ForeignAccountAndNonLearningProfileCannotReleasePrivatePreview()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        var other = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(f.Shared.Shared.Database, new(other.Account, other.Profiles["learning"]), other.Subject);
        var r = await f.Repository(other).PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.NotFound, r.State); Assert.Null(r.Preview);
        foreach (var profile in f.Owner.Profiles.Where(x => x.Key != "learning").Select(x => x.Value))
        { var forged = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account); forged.Profiles["learning"] = profile; r = await f.Repository(forged).PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Denied, r.State); Assert.Null(r.Preview); }
    }
    [LocalPostgresFact]
    public async Task InactiveProfileDenialPreservesOriginalContentForReactivation()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Creative)); P(await f.Service.AddProjectAsync(p.Id, new(Guid.NewGuid(), "kept")));
        await f.Shared.Shared.Lifecycle("learning", false); var r = await f.Service.PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Denied, r.State); Assert.Null(r.Preview);
        await f.Shared.Shared.Lifecycle("learning", true); Assert.Contains("kept", (await f.Service.PreviewAsync(p.Id)).Preview!.Html);
    }
    [LocalPostgresFact]
    public async Task UnavailableAndUnauthenticatedAuthorityNeverReleasesPreview()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer));
        f.Owner.Unavailable = true; var r = await f.Service.PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Unavailable, r.State); Assert.Null(r.Preview);
        f.Owner.Unavailable = false; f.Owner.Unauthenticated = true; r = await f.Service.PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Denied, r.State); Assert.Null(r.Preview);
    }
    [LocalPostgresFact]
    public async Task AuthorizationRevocationAfterRenderingDiscardsPrivateOutput()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); var reached = false;
        var repo = f.Repository(checkpoint: (phase, _) => { if (phase == "read-release") { reached = true; f.Owner.Denied = true; } return Task.CompletedTask; });
        var r = await repo.PreviewAsync(p.Id); Assert.True(reached); Assert.Equal(PortfolioOperationState.Denied, r.State); Assert.Null(r.Preview); f.Owner.Denied = false;
        Assert.Equal(PortfolioOperationState.Available, (await f.Service.PreviewAsync(p.Id)).State);
    }
    [LocalPostgresFact]
    public async Task CancellationAndReleaseFailureDiscardOutputAndReleaseTransactions()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); using var cts = new CancellationTokenSource(); var reached = false;
        var repo = f.Repository(checkpoint: (phase, ct) => { if (phase == "read-release") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.PreviewAsync(p.Id, cts.Token)); Assert.True(reached);
        repo = f.Repository(checkpoint: (_, _) => Task.FromException(new IOException("SYNTHETIC-PRIVATE"))); var r = await repo.PreviewAsync(p.Id); Assert.Equal(PortfolioOperationState.Unavailable, r.State); Assert.Null(r.Preview); Assert.DoesNotContain("SYNTHETIC", r.ToString());
        P(await f.Service.SelectTemplateAsync(p.Id, PortfolioTemplate.Professional)); Assert.Contains("Professional", (await f.Service.PreviewAsync(p.Id)).Preview!.Html);
    }
    [LocalPostgresFact]
    public async Task PreviewUsesCurrentPersistedProjectsOrderAndTemplateNotCallerHeldChanges()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); var a = new PortfolioProject(Guid.NewGuid(), "FIRST"); var b = new PortfolioProject(Guid.NewGuid(), "SECOND");
        P(await f.Service.AddProjectAsync(p.Id, a)); P(await f.Service.AddProjectAsync(p.Id, b)); P(await f.Service.ReorderProjectsAsync(p.Id, [b.Id, a.Id])); P(await f.Service.SelectTemplateAsync(p.Id, PortfolioTemplate.Creative)); p.AddProject(new(Guid.NewGuid(), "UNSAVED"));
        var html = (await f.Service.PreviewAsync(p.Id)).Preview!.Html; Assert.Contains("Creative", html); Assert.DoesNotContain("UNSAVED", html); Assert.True(html.IndexOf("SECOND", StringComparison.Ordinal) < html.IndexOf("FIRST", StringComparison.Ordinal));
    }
    [LocalPostgresFact]
    public async Task ConcurrentEditCannotMixIntoAnAlreadyCoordinatedPreviewSnapshot()
    {
        await using var f = await PortfolioPostgresFixture.Create(); var p = P(await f.Service.CreateAsync(PortfolioTemplate.Developer)); var project = new PortfolioProject(Guid.NewGuid(), "BEFORE"); P(await f.Service.AddProjectAsync(p.Id, project));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = f.Repository(checkpoint: async (phase, ct) => { if (phase == "read-release") { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); } });
        var preview = repo.PreviewAsync(p.Id); await entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var edit = f.Service.EditProjectAsync(p.Id, project.Id, "AFTER", null); release.SetResult();
        var html = (await preview).Preview!.Html; Assert.Contains("BEFORE", html); Assert.DoesNotContain("AFTER", html); P(await edit); Assert.Contains("AFTER", (await f.Service.PreviewAsync(p.Id)).Preview!.Html);
    }
}
