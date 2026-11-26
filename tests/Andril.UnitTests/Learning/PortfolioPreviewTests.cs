using System.Net;
using System.Text.Json;
using Andril.Learning.Portfolio;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class PortfolioPreviewTests
{
    [Theory]
    [InlineData(PortfolioTemplate.Developer)]
    [InlineData(PortfolioTemplate.Creative)]
    [InlineData(PortfolioTemplate.Professional)]
    public void AllTemplatesRenderExistingFieldsInOrderAsPrivateInertEscapedText(PortfolioTemplate template)
    {
        const string title = "<script>PRIVATE</script> & \"name\"";
        const string description = "line1\n<img src=\"https://private.invalid\" onerror=\"secret()\"> தமிழ்";
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), template); var a = new PortfolioProject(Guid.NewGuid(), title, description, ["https://media.invalid/image?q=%22"], ["http://links.invalid/?a=1&b=2"]); var b = new PortfolioProject(Guid.NewGuid(), "SECOND"); p.AddProject(a); p.AddProject(b); var before = p.Projects;
        var preview = PortfolioPreview.CreateOffline(p); var html = preview.Html;
        Assert.Contains(template.ToString(), html); Assert.Contains(WebUtility.HtmlEncode(title), html); Assert.Contains(WebUtility.HtmlEncode(description), html);
        Assert.Contains(WebUtility.HtmlEncode(a.MediaReferences[0]), html); Assert.Contains(WebUtility.HtmlEncode(a.ExternalLinks[0]), html);
        Assert.True(html.IndexOf(WebUtility.HtmlEncode(title), StringComparison.Ordinal) < html.IndexOf("SECOND", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "<script", "<img", "<iframe", "<a ", "href=", "src=\"https", "<svg", "<object", "<link", "<meta", p.Id.ToString(), p.OwnerLearningProfileId.ToString(), a.Id.ToString() }) Assert.DoesNotContain(forbidden, html);
        Assert.Equal(2, html.Split("<section>").Length - 1); Assert.Same(before, p.Projects); Assert.Equal(template, p.Template); Assert.Equal(title, p.Projects[0].Title);
        Assert.Equal("{}", JsonSerializer.Serialize(preview)); Assert.DoesNotContain("PRIVATE", preview.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(PortfolioPreviewResult.Available(preview)));
    }
    [Fact]
    public void TemplateSwitchingChangesPresentationWithoutMutatingProjectsOrPriorPreview()
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Developer); p.AddProject(new(Guid.NewGuid(), "SYNTHETIC", "description")); var projects = p.Projects;
        var old = PortfolioPreview.CreateOffline(p); var oldHtml = old.Html; var variants = new List<string>();
        foreach (var template in Enum.GetValues<PortfolioTemplate>()) { p.SelectTemplate(template); variants.Add(PortfolioPreview.CreateOffline(p).Html); Assert.Same(projects, p.Projects); }
        Assert.Equal(3, variants.Distinct().Count()); Assert.Equal(oldHtml, old.Html); Assert.Equal("description", p.Projects[0].Description);
    }
    [Fact]
    public void EmptyDraftFieldsStayEmptyAndCancellationCannotReturnAResult()
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Professional); p.AddProject(new(Guid.NewGuid()));
        Assert.Contains("<h2></h2>", PortfolioPreview.CreateOffline(p).Html); Assert.Null(p.Projects[0].Title);
        using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => PortfolioPreview.CreateOffline(p, cts.Token));
        Assert.Throws<ArgumentNullException>(() => PortfolioPreview.CreateOffline(null!));
        foreach (var s in Enum.GetValues<PortfolioOperationState>().Where(x => x != PortfolioOperationState.Available)) { var result = PortfolioPreviewResult.Failure(s); Assert.Null(result.Preview); Assert.Equal(s, result.State); Assert.Equal("{}", JsonSerializer.Serialize(result)); }
    }
}
