using System.Reflection;
using System.Text.Json;
using Andril.Learning.Portfolio;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPortfolioTests
{
    private static readonly Guid PortfolioId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData(PortfolioTemplate.Developer)]
    [InlineData(PortfolioTemplate.Creative)]
    [InlineData(PortfolioTemplate.Professional)]
    public void Creation_HasStableIdentityLearningOwnershipPrivateScopeAndSelectedTemplate(PortfolioTemplate template)
    {
        var portfolio = new LearningPortfolio(PortfolioId, OwnerId, template);
        Assert.Equal(PortfolioId, portfolio.Id);
        Assert.Equal(OwnerId, portfolio.OwnerLearningProfileId);
        Assert.Equal("learning", portfolio.Module);
        Assert.True(portfolio.IsPrivate);
        Assert.Equal(template, portfolio.Template);
        Assert.Empty(portfolio.Projects);
    }

    [Fact]
    public void EmptyPortfolioIdentity_IsRejected() =>
        Assert.Throws<ArgumentException>("id", () => new LearningPortfolio(Guid.Empty, OwnerId, PortfolioTemplate.Developer));

    [Fact]
    public void EmptyOwnerIdentity_IsRejected() =>
        Assert.Throws<ArgumentException>("ownerLearningProfileId", () => new LearningPortfolio(PortfolioId, Guid.Empty, PortfolioTemplate.Developer));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedTemplate_IsRejectedByConstructionAndSelectionWithoutMutation(int value)
    {
        var template = (PortfolioTemplate)value;
        Assert.False(Enum.IsDefined(template));
        Assert.Throws<ArgumentException>("template", () => new LearningPortfolio(PortfolioId, OwnerId, template));
        var portfolio = Populated();
        var before = Capture(portfolio);
        Assert.Throws<ArgumentException>("template", () => portfolio.SelectTemplate(template));
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void IdentityAndOwnerReference_HaveNoMutationSurface()
    {
        Assert.Null(typeof(LearningPortfolio).GetProperty(nameof(LearningPortfolio.Id))!.SetMethod);
        Assert.Null(typeof(LearningPortfolio).GetProperty(nameof(LearningPortfolio.OwnerLearningProfileId))!.SetMethod);
        Assert.Null(typeof(LearningPortfolio).GetProperty(nameof(LearningPortfolio.Module))!.SetMethod);
        Assert.Null(typeof(LearningPortfolio).GetProperty(nameof(LearningPortfolio.IsPrivate))!.SetMethod);
    }

    [Fact]
    public void RepeatedTemplateSwitching_PreservesIdentityOwnershipOrderAndAllProjectContent()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        foreach (var template in new[] { PortfolioTemplate.Creative, PortfolioTemplate.Professional, PortfolioTemplate.Developer,
            PortfolioTemplate.Developer, PortfolioTemplate.Creative, PortfolioTemplate.Professional })
        {
            portfolio.SelectTemplate(template);
            Assert.Equal(template, portfolio.Template);
            Assert.Equal(before.Id, portfolio.Id);
            Assert.Equal(before.Owner, portfolio.OwnerLearningProfileId);
            Assert.Same(before.Projects, portfolio.Projects);
            AssertProjectContents(portfolio.Projects);
        }
    }

    [Fact]
    public void IndependentPortfolios_DoNotShareMutableCollectionsOrEditingState()
    {
        var first = Populated();
        var second = new LearningPortfolio(OtherId, OwnerId, PortfolioTemplate.Developer);
        second.AddProject(first.Projects[0]); // Sharing an immutable project value is safe.
        var secondBefore = Capture(second);
        first.EditProject(first.Projects[0].Id, "Changed", null);
        first.ReorderProjects(first.Projects.Reverse().Select(project => project.Id));
        first.SelectTemplate(PortfolioTemplate.Creative);
        first.AddProject(Project(4));
        Assert.NotSame(first.Projects, second.Projects);
        AssertUnchanged(second, secondBefore);
        Assert.Equal("Private title 1", Assert.Single(second.Projects).Title);
    }

    [Fact]
    public void AddingDistinctProjects_PreservesExistingProjectsAndEarlierSnapshots()
    {
        var portfolio = Empty();
        var empty = portfolio.Projects;
        var first = Project(1); var second = Project(2); var third = Project(3);
        portfolio.AddProject(first);
        var one = portfolio.Projects;
        portfolio.AddProject(second);
        var two = portfolio.Projects;
        portfolio.AddProject(third);
        Assert.Empty(empty);
        Assert.Same(first, Assert.Single(one));
        Assert.Equal(new[] { first, second }, two);
        Assert.Equal(new[] { first, second, third }, portfolio.Projects);
        Assert.Equal(3, portfolio.Projects.Select(project => project.Id).Distinct().Count());
        AssertProjectContents(portfolio.Projects);
        Assert.Equal(PortfolioId, portfolio.Id);
        Assert.Equal(OwnerId, portfolio.OwnerLearningProfileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateIdentity_IsRejectedEvenWithDifferentContent(bool differentObject)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var duplicate = differentObject ? new PortfolioProject(portfolio.Projects[0].Id, "Different") : portfolio.Projects[0];
        Assert.Throws<ArgumentException>("project", () => portfolio.AddProject(duplicate));
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void NullProject_IsRejectedWithoutMutation()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        Assert.Throws<ArgumentNullException>("project", () => portfolio.AddProject(null!));
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void InvalidProjectIdentity_CannotBeConstructedOrAdded()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        Assert.Throws<ArgumentException>("id", () => portfolio.AddProject(new PortfolioProject(Guid.Empty)));
        AssertUnchanged(portfolio, before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ManualEdit_ChangesOnlyTargetAndCopiesInputsWhilePreservingIdentityOrderAndOldSnapshot(int index)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var target = before.Projects[index];
        var media = new List<string> { "http://media.example.invalid/edited" };
        var links = new[] { "https://project.example.invalid/edited" };
        portfolio.EditProject(target.Id, "Edited title", "Edited description", media, links);
        media.Clear(); links[0] = "https://project.example.invalid/caller-change";
        Assert.NotSame(before.Projects, portfolio.Projects);
        Assert.Equal(before.Projects.Select(project => project.Id), portfolio.Projects.Select(project => project.Id));
        var edited = portfolio.Projects[index];
        Assert.NotSame(target, edited);
        Assert.Equal(target.Id, edited.Id);
        Assert.Equal("Edited title", edited.Title);
        Assert.Equal("Edited description", edited.Description);
        Assert.Equal("http://media.example.invalid/edited", Assert.Single(edited.MediaReferences));
        Assert.Equal("https://project.example.invalid/edited", Assert.Single(edited.ExternalLinks));
        for (var i = 0; i < before.Projects.Count; i++)
            if (i != index) Assert.Same(before.Projects[i], portfolio.Projects[i]);
        AssertProjectContents(before.Projects);
        Assert.Equal(before.Id, portfolio.Id);
        Assert.Equal(before.Owner, portfolio.OwnerLearningProfileId);
        Assert.Equal(before.Template, portfolio.Template);
    }

    [Fact]
    public void ManualEdit_AllowsOptionalDraftFieldsWithoutMutatingPreviousProject()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        portfolio.EditProject(before.Projects[0].Id, null, null);
        var edited = portfolio.Projects[0];
        Assert.Equal(before.Projects[0].Id, edited.Id);
        Assert.Null(edited.Title); Assert.Null(edited.Description);
        Assert.Empty(edited.MediaReferences); Assert.Empty(edited.ExternalLinks);
        AssertProjectContents(before.Projects);
        Assert.Same(before.Projects[1], portfolio.Projects[1]);
        Assert.Same(before.Projects[2], portfolio.Projects[2]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("foreign")]
    [InlineData("media")]
    [InlineData("link")]
    public void RejectedEdit_PreservesCompletePortfolio(string defect)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var id = defect == "empty" ? Guid.Empty : defect == "foreign" ? OtherId : portfolio.Projects[1].Id;
        var parameter = defect is "empty" or "foreign" ? "projectId" : defect == "media" ? "mediaReferences" : "externalLinks";
        Assert.Throws<ArgumentException>(parameter, () => portfolio.EditProject(id, "Changed", "Changed",
            defect == "media" ? new[] { "file:///private" } : new[] { "https://media.example.invalid/new" },
            defect == "link" ? new[] { "javascript:alert(1)" } : new[] { "https://project.example.invalid/new" }));
        AssertUnchanged(portfolio, before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditingEnumerationFailure_PreservesCompletePortfolio(bool media)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var failure = new InvalidOperationException("Synthetic enumeration failure.");
        IEnumerable<string> Broken()
        {
            yield return "https://example.invalid/manual";
            throw failure;
        }
        var error = Assert.Throws<InvalidOperationException>(() => portfolio.EditProject(portfolio.Projects[0].Id, "Changed", "Changed",
            media ? Broken() : new[] { "https://media.example.invalid/new" }, media ? null : Broken()));
        Assert.Same(failure, error);
        AssertUnchanged(portfolio, before);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void CompleteReordering_MovesProjectsToBeginningMiddleAndEndWithoutChangingContent(int first, int second, int third)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var expected = new[] { before.Projects[first], before.Projects[second], before.Projects[third] };
        var order = expected.Select(project => project.Id).ToArray();
        portfolio.ReorderProjects(order);
        order[0] = Guid.Empty;
        Assert.Equal(expected, portfolio.Projects);
        for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i], portfolio.Projects[i]);
        Assert.Equal(3, portfolio.Projects.Select(project => project.Id).Distinct().Count());
        Assert.Equal(before.Projects.Select(project => project.Id).Order(), portfolio.Projects.Select(project => project.Id).Order());
        AssertProjectContents(before.Projects);
        Assert.Equal(before.Id, portfolio.Id);
        Assert.Equal(before.Owner, portfolio.OwnerLearningProfileId);
        Assert.Equal(before.Template, portfolio.Template);
    }

    [Fact]
    public void EmptyAndSingleProjectPortfolios_AcceptTheirCompleteOrder()
    {
        var portfolio = Empty();
        portfolio.ReorderProjects([]);
        Assert.Empty(portfolio.Projects);
        var project = Project(1);
        portfolio.AddProject(project);
        portfolio.ReorderProjects([project.Id]);
        Assert.Same(project, Assert.Single(portfolio.Projects));
        AssertProjectContents(portfolio.Projects);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("foreign")]
    public void InvalidReordering_PreservesCompletePreviousOrderAndState(string defect)
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var ids = portfolio.Projects.Select(project => project.Id).ToArray();
        Guid[]? order = defect switch
        {
            "null" => null,
            "missing" => [ids[0], ids[1]],
            "extra" => [ids[0], ids[1], ids[2], OtherId],
            "duplicate" => [ids[2], ids[1], ids[1]],
            "empty" => [ids[2], ids[1], Guid.Empty],
            _ => [ids[2], ids[1], OtherId]
        };
        if (order is null) Assert.Throws<ArgumentNullException>("projectIds", () => portfolio.ReorderProjects(null!));
        else Assert.Throws<ArgumentException>("projectIds", () => portfolio.ReorderProjects(order));
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void ReorderingEnumerationFailure_DoesNotPartiallyReorderProjects()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var failure = new InvalidOperationException("Synthetic ordering failure.");
        IEnumerable<Guid> Broken()
        {
            yield return before.Projects[2].Id;
            throw failure;
        }
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => portfolio.ReorderProjects(Broken())));
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void ReturnedProjectSnapshot_CannotBeUsedToMutatePortfolio()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var projects = Assert.IsAssignableFrom<IList<PortfolioProject>>(portfolio.Projects);
        Assert.True(projects.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => projects.Add(Project(4)));
        Assert.Throws<NotSupportedException>(() => projects[0] = Project(4));
        Assert.Throws<NotSupportedException>(() => projects.RemoveAt(0));
        Assert.Throws<NotSupportedException>(() => projects.Clear());
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void DefaultJson_ContainsOnlySafeMetadataAndDoesNotMutatePopulatedPrivateState()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var output = JsonSerializer.Serialize(portfolio);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(new[] { "Id", "IsPrivate", "Module", "Template" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PortfolioId, json.RootElement.GetProperty("Id").GetGuid());
        Assert.Equal("learning", json.RootElement.GetProperty("Module").GetString());
        Assert.True(json.RootElement.GetProperty("IsPrivate").GetBoolean());
        Assert.Equal((int)portfolio.Template, json.RootElement.GetProperty("Template").GetInt32());
        Assert.DoesNotContain(nameof(LearningPortfolio.OwnerLearningProfileId), output);
        Assert.DoesNotContain(nameof(LearningPortfolio.Projects), output);
        Assert.DoesNotContain(OwnerId.ToString(), output);
        foreach (var project in portfolio.Projects)
        {
            foreach (var secret in new[] { project.Id.ToString(), project.Title!, project.Description! }
                .Concat(project.MediaReferences).Concat(project.ExternalLinks))
                Assert.DoesNotContain(secret, output);
        }
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void DiagnosticsAndValidationErrors_DoNotRevealOwnerOrPrivateProjectDetails()
    {
        var portfolio = Populated();
        var before = Capture(portfolio);
        var error = Assert.Throws<ArgumentException>(() => portfolio.EditProject(portfolio.Projects[0].Id,
            "Private replacement", null, ["file:///PRIVATE-MARKER"], null));
        foreach (var output in new[] { portfolio.ToString(), error.ToString() })
        {
            Assert.DoesNotContain(PortfolioId.ToString(), output);
            Assert.DoesNotContain(OwnerId.ToString(), output);
            Assert.DoesNotContain("PRIVATE-MARKER", output);
            Assert.DoesNotContain("Private replacement", output);
            foreach (var project in portfolio.Projects)
                foreach (var secret in new[] { project.Title!, project.Description! }.Concat(project.MediaReferences).Concat(project.ExternalLinks))
                    Assert.DoesNotContain(secret, output);
        }
        AssertUnchanged(portfolio, before);
    }

    [Fact]
    public void DomainSurface_ExposesOnlyManualPortfolioOperationsWithoutAutomaticIntegrationHooks()
    {
        var type = typeof(LearningPortfolio);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(new[] { "AddProject", "EditProject", "ReorderProjects", "SelectTemplate", "ToString" }, type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { typeof(Guid), typeof(Guid), typeof(PortfolioTemplate) },
            Assert.Single(type.GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));
        // This guards the existing domain boundary; authentication and external integrations remain deferred.
        Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
    }

    private static LearningPortfolio Empty() => new(PortfolioId, OwnerId, PortfolioTemplate.Developer);

    private static LearningPortfolio Populated()
    {
        var portfolio = Empty();
        for (var i = 1; i <= 3; i++) portfolio.AddProject(Project(i));
        return portfolio;
    }

    private static PortfolioProject Project(int number) => new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        $"Private title {number}", $"Private description {number}",
        [$"https://media.example.invalid/private-{number}"], [$"https://project.example.invalid/private-{number}"]);

    private sealed record State(Guid Id, Guid Owner, PortfolioTemplate Template, IReadOnlyList<PortfolioProject> Projects);

    private static State Capture(LearningPortfolio portfolio) =>
        new(portfolio.Id, portfolio.OwnerLearningProfileId, portfolio.Template, portfolio.Projects);

    private static void AssertUnchanged(LearningPortfolio portfolio, State before)
    {
        Assert.Equal(before.Id, portfolio.Id);
        Assert.Equal(before.Owner, portfolio.OwnerLearningProfileId);
        Assert.Equal(before.Template, portfolio.Template);
        Assert.Equal("learning", portfolio.Module);
        Assert.True(portfolio.IsPrivate);
        Assert.Same(before.Projects, portfolio.Projects);
        AssertProjectContents(portfolio.Projects);
    }

    private static void AssertProjectContents(IReadOnlyList<PortfolioProject> projects)
    {
        foreach (var project in projects)
        {
            var number = int.Parse(project.Id.ToString("N")[^12..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal($"Private title {number}", project.Title);
            Assert.Equal($"Private description {number}", project.Description);
            Assert.Equal($"https://media.example.invalid/private-{number}", Assert.Single(project.MediaReferences));
            Assert.Equal($"https://project.example.invalid/private-{number}", Assert.Single(project.ExternalLinks));
        }
    }
}
