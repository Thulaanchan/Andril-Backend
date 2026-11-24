using System.Reflection;
using System.Text.Json;
using Andril.Learning.Portfolio;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class PortfolioProjectTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Media = "https://media.example.invalid/private-image";
    private const string Link = "https://project.example.invalid/private-work";

    [Fact]
    public void ValidConstruction_PreservesIdentityAndStructuredManualContent()
    {
        var project = Original();
        AssertOriginal(project);
    }

    [Fact]
    public void EmptyProjectIdentity_IsRejected()
    {
        Assert.Throws<ArgumentException>("id", () => new PortfolioProject(Guid.Empty));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" \t\r\n", " \t\r\n")]
    [InlineData("Title only", null)]
    [InlineData(null, "Description only")]
    [InlineData("  தமிழ் සිංහල e\u0301 👩🏽‍💻  ", "Exact manual text")]
    public void OptionalText_IsPreservedWithoutMandatoryFields(string? title, string? description)
    {
        var project = new PortfolioProject(ProjectId, title, description);
        var edited = project.WithContent(title, description);
        Assert.Equal(title, project.Title);
        Assert.Equal(description, project.Description);
        Assert.Equal(title, edited.Title);
        Assert.Equal(description, edited.Description);
        Assert.Equal(ProjectId, edited.Id);
        Assert.Empty(project.MediaReferences);
        Assert.Empty(project.ExternalLinks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OptionalCollections_AcceptNullOrEmpty(bool useNull)
    {
        var values = useNull ? null : Array.Empty<string>();
        var project = new PortfolioProject(ProjectId, mediaReferences: values, externalLinks: values);
        Assert.Empty(project.MediaReferences);
        Assert.Empty(project.ExternalLinks);
    }

    public static IEnumerable<object[]> SupportedReferences()
    {
        foreach (var value in new[] { "http://example.invalid/project", "https://example.invalid/project", "https://example.invalid/project?view=manual#details" })
        foreach (var media in new[] { true, false })
            yield return new object[] { value, media };
    }

    [Theory]
    [MemberData(nameof(SupportedReferences))]
    public void HttpAndHttpsReferences_AreAcceptedAsInertOfflineData(string reference, bool media)
    {
        // Reserved .invalid names are deliberately not resolved or fetched by these tests.
        var project = new PortfolioProject(ProjectId, mediaReferences: media ? new[] { reference } : null,
            externalLinks: media ? null : new[] { reference });
        Assert.Equal(reference, Assert.Single(media ? project.MediaReferences : project.ExternalLinks));
        Assert.Empty(media ? project.ExternalLinks : project.MediaReferences);
        var edited = project.WithContent(null, null, media ? new[] { reference } : null, media ? null : new[] { reference });
        Assert.Equal(reference, Assert.Single(media ? edited.MediaReferences : edited.ExternalLinks));
        Assert.Equal(ProjectId, edited.Id);
    }

    [Fact]
    public void ManualEdit_ReturnsNewSnapshotWithStableIdentityAndCompleteRequestedContent()
    {
        var original = Original();
        var mediaSnapshot = original.MediaReferences;
        var linkSnapshot = original.ExternalLinks;
        var edited = original.WithContent("Edited title", "Edited description",
            ["http://media.example.invalid/new"], ["https://project.example.invalid/new"]);
        Assert.NotSame(original, edited);
        Assert.Equal(ProjectId, edited.Id);
        Assert.Equal("Edited title", edited.Title);
        Assert.Equal("Edited description", edited.Description);
        Assert.Equal("http://media.example.invalid/new", Assert.Single(edited.MediaReferences));
        Assert.Equal("https://project.example.invalid/new", Assert.Single(edited.ExternalLinks));
        Assert.Same(mediaSnapshot, original.MediaReferences);
        Assert.Same(linkSnapshot, original.ExternalLinks);
        AssertOriginal(original);
    }

    [Fact]
    public void ManualReplacement_CanReturnToAnIncompleteDraftWithoutChangingOriginal()
    {
        var original = Original();
        var edited = original.WithContent(null, null);
        Assert.Equal(ProjectId, edited.Id);
        Assert.Null(edited.Title);
        Assert.Null(edited.Description);
        Assert.Empty(edited.MediaReferences);
        Assert.Empty(edited.ExternalLinks);
        AssertOriginal(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructionAndEditing_CopyCallerOwnedCollections(bool edit)
    {
        var media = new List<string> { Media };
        var links = new[] { Link };
        var original = Original();
        var project = edit ? original.WithContent("Private title", "Private description", media, links)
            : new PortfolioProject(ProjectId, "Private title", "Private description", media, links);
        media[0] = "https://media.example.invalid/changed";
        media.Clear();
        links[0] = "https://project.example.invalid/changed";
        AssertOriginal(project);
        AssertOriginal(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedCollections_CannotModifyConstructionOrEditSnapshots(bool edit)
    {
        var original = Original();
        var project = edit ? original.WithContent("Private title", "Private description", [Media], [Link]) : original;
        foreach (var snapshot in new[] { project.MediaReferences, project.ExternalLinks })
        {
            var list = Assert.IsAssignableFrom<IList<string>>(snapshot);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Add("https://example.invalid/extra"));
            Assert.Throws<NotSupportedException>(() => list[0] = "https://example.invalid/replaced");
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        AssertOriginal(project);
        AssertOriginal(original);
    }

    public static IEnumerable<object?[]> InvalidReferences()
    {
        foreach (var value in new string?[] { null, "", " \t\r\n", "/relative/project", "example.invalid/project",
            "not a URI", "http://", "https://", "javascript:alert(1)", "data:text/plain,private",
            "file:///private", "ftp://example.invalid/project", "mailto:private@example.invalid",
            "tel:+000000", "blob:https://example.invalid/id" })
        foreach (var media in new[] { true, false })
            yield return new object?[] { value, media };
    }

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public void InvalidReference_RejectsConstructionAndEditWithoutPartialMutation(string? reference, bool media)
    {
        // Place the invalid item after a valid one to exercise partial enumeration safety.
        var values = new[] { media ? Media : Link, reference! };
        var parameter = media ? "mediaReferences" : "externalLinks";
        Assert.Throws<ArgumentException>(parameter, () => new PortfolioProject(ProjectId, "Changed", "Changed",
            media ? values : new[] { Media }, media ? new[] { Link } : values));
        var original = Original();
        var mediaSnapshot = original.MediaReferences;
        var linkSnapshot = original.ExternalLinks;
        var error = Assert.Throws<ArgumentException>(parameter, () => original.WithContent("Changed", "Changed",
            media ? values : new[] { Media }, media ? new[] { Link } : values));
        Assert.Same(mediaSnapshot, original.MediaReferences);
        Assert.Same(linkSnapshot, original.ExternalLinks);
        AssertOriginal(original);
        if (!string.IsNullOrWhiteSpace(reference)) Assert.DoesNotContain(reference, error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnumerationFailure_PropagatesAndPreservesTheOriginalCompleteSnapshot(bool media)
    {
        var original = Original();
        var mediaSnapshot = original.MediaReferences;
        var linkSnapshot = original.ExternalLinks;
        var failure = new InvalidOperationException("Synthetic enumeration failure.");
        IEnumerable<string> BrokenReferences()
        {
            yield return media ? Media : Link;
            throw failure;
        }
        var error = Assert.Throws<InvalidOperationException>(() => original.WithContent("Changed", "Changed",
            media ? BrokenReferences() : new[] { Media }, media ? new[] { Link } : BrokenReferences()));
        Assert.Same(failure, error);
        Assert.Same(mediaSnapshot, original.MediaReferences);
        Assert.Same(linkSnapshot, original.ExternalLinks);
        AssertOriginal(original);
    }

    [Fact]
    public void DefaultJsonAndDiagnostics_OmitPrivateContentAndLeaveTheProjectUnchanged()
    {
        var project = Original();
        var mediaSnapshot = project.MediaReferences;
        var linkSnapshot = project.ExternalLinks;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(project));
        var property = Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("Id", property.Name);
        Assert.Equal(ProjectId, property.Value.GetGuid());
        var output = json.RootElement.GetRawText();
        foreach (var secret in new[] { project.Title!, project.Description!, Media, Link })
        {
            Assert.DoesNotContain(secret, output);
            Assert.DoesNotContain(secret, project.ToString());
        }
        Assert.DoesNotContain(ProjectId.ToString(), project.ToString());
        Assert.Same(mediaSnapshot, project.MediaReferences);
        Assert.Same(linkSnapshot, project.ExternalLinks);
        AssertOriginal(project);
    }

    [Fact]
    public void PublicContract_IsImmutableManualContentWithoutPublishingOrModuleServiceHooks()
    {
        var type = typeof(PortfolioProject);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        // Match the existing CV surface guard: additions need deliberate privacy/side-effect review.
        Assert.Equal(new[] { "ToString", "WithContent" }, type
            .GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(new[] { typeof(Guid), typeof(string), typeof(string), typeof(IEnumerable<string>), typeof(IEnumerable<string>) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static PortfolioProject Original() => new(ProjectId, "Private title", "Private description", [Media], [Link]);

    private static void AssertOriginal(PortfolioProject project)
    {
        Assert.Equal(ProjectId, project.Id);
        Assert.Equal("Private title", project.Title);
        Assert.Equal("Private description", project.Description);
        Assert.Equal(Media, Assert.Single(project.MediaReferences));
        Assert.Equal(Link, Assert.Single(project.ExternalLinks));
    }
}
