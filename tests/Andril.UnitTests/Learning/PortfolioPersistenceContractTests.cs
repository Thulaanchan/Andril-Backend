using System.Text.Json;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class PortfolioPersistenceContractTests
{
    [Theory]
    [InlineData(PortfolioTemplate.Developer)]
    [InlineData(PortfolioTemplate.Creative)]
    [InlineData(PortfolioTemplate.Professional)]
    public void PrivateSnapshotPreservesAllContentIdentitiesOrderAndTemplates(PortfolioTemplate template)
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), template);
        p.AddProject(new(Guid.NewGuid(), " தமிழ் e\u0301 <script>SYNTHETIC</script> ", null, ["https://example.test/media"], ["http://example.test/project"]));
        p.AddProject(new(Guid.NewGuid()));
        var copy = LearningPortfolio.FromStoredSnapshot(p.Id, p.OwnerLearningProfileId, p.Template, p.Projects); Assert.Equal(p.Id, copy.Id); Assert.Equal(p.OwnerLearningProfileId, copy.OwnerLearningProfileId);
        Assert.Equal(template, copy.Template); Assert.Equal(p.Projects.Select(x => x.Id), copy.Projects.Select(x => x.Id));
        Assert.Equal(p.Projects[0].Title, copy.Projects[0].Title); Assert.Null(copy.Projects[0].Description); Assert.Equal(p.Projects[0].MediaReferences, copy.Projects[0].MediaReferences); Assert.Equal(p.Projects[0].ExternalLinks, copy.Projects[0].ExternalLinks);
        Assert.True(copy.IsPrivate); Assert.Equal("learning", copy.Module);
    }
    [Fact]
    public void InvalidStoredSnapshotCannotReuseProjectIdentitiesOrGrantOwnership()
    {
        var p = new PortfolioProject(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => LearningPortfolio.FromStoredSnapshot(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Developer, [p, p]));
        Assert.Throws<ArgumentException>(() => LearningPortfolio.FromStoredSnapshot(Guid.Empty, Guid.NewGuid(), PortfolioTemplate.Developer, []));
        Assert.Throws<ArgumentException>(() => LearningPortfolio.FromStoredSnapshot(Guid.NewGuid(), Guid.Empty, PortfolioTemplate.Developer, []));
        Assert.Throws<ArgumentNullException>(() => LearningPortfolio.FromStoredSnapshot(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Developer, null!));
    }
    [Fact]
    public void ExplicitCommandsCopyEnumerationsAndPreserveUnrelatedProjects()
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Creative); var a = new PortfolioProject(Guid.NewGuid(), "a"); var b = new PortfolioProject(Guid.NewGuid(), "b");
        p.AddProject(a); p.AddProject(b); var prior = p.Projects;
        var media = new[] { "https://example.test/original" }; var edit = PortfolioSaveChange.Edit(a.Id, "changed", null, media); media[0] = "file:///secret"; edit.Apply(p);
        Assert.Equal("https://example.test/original", p.Projects[0].MediaReferences[0]); Assert.Same(b, p.Projects[1]); Assert.Equal("a", prior[0].Title);
        var order = new[] { b.Id, a.Id }; var change = PortfolioSaveChange.Reorder(order); Array.Reverse(order); change.Apply(p); Assert.Equal(new[] { b.Id, a.Id }, p.Projects.Select(x => x.Id));
        var snapshot = p.Projects; PortfolioSaveChange.Template(PortfolioTemplate.Professional).Apply(p); Assert.Same(snapshot, p.Projects);
    }
    [Fact]
    public void InvalidChangesPreserveExistingSnapshotsAndValidation()
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Developer); var project = new PortfolioProject(Guid.NewGuid()); p.AddProject(project); var before = p.Projects;
        Assert.Throws<ArgumentException>(() => PortfolioSaveChange.Edit(project.Id, "x", null, ["file:///secret"]));
        Assert.Throws<ArgumentException>(() => PortfolioSaveChange.Template((PortfolioTemplate)99).Apply(p));
        Assert.Throws<ArgumentException>(() => PortfolioSaveChange.Reorder([Guid.NewGuid()]).Apply(p));
        Assert.Throws<ArgumentException>(() => PortfolioSaveChange.Add(project).Apply(p)); Assert.Same(before, p.Projects);
    }
    [Fact]
    public void ProtectedResultsOmitPrivateContentFromDefaultSerializationAndDiagnostics()
    {
        var p = new LearningPortfolio(Guid.NewGuid(), Guid.NewGuid(), PortfolioTemplate.Developer); p.AddProject(new(Guid.NewGuid(), "SYNTHETIC-PRIVATE"));
        foreach (var value in new object[] { PortfolioReadResult.Available(p), PortfolioListResult.Available([p.Id]), new PortfolioDeleteResult(PortfolioOperationState.Available), PortfolioSaveChange.Add(p.Projects[0]) })
        { Assert.Equal("{}", JsonSerializer.Serialize(value, value.GetType())); Assert.DoesNotContain("SYNTHETIC", value.ToString()); Assert.DoesNotContain(p.Id.ToString(), value.ToString()); }
        foreach (var state in Enum.GetValues<PortfolioOperationState>().Where(s => s != PortfolioOperationState.Available))
        { Assert.Null(PortfolioReadResult.Failure(state).Portfolio); Assert.Null(PortfolioListResult.Failure(state).Ids); }
    }
    [Fact]
    public void MappingSnapshotAndMigrationHistoryAreConsistentAndDoNotAddSharedForeignKeys()
    {
        using var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        var entity = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "LearningPortfolioRow"); Assert.Equal("learning_portfolios", entity.GetTableName()); Assert.Empty(entity.GetForeignKeys());
        Assert.Equal(new[] { "OwnerLearningProfileId", "Module", "PortfolioId" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal("20261126110000_LearningPortfolioPersistence", db.Database.GetMigrations().Last());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
