using System.Text.Json;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class ProjectImportContractTests
{
    [Fact]
    public void ReviewIsAnImmutablePrivateTitleDescriptionSnapshotWithoutMediaOrLinkFields()
    {
        var p = new ReviewedPortfolioProject(Guid.NewGuid(), "SYNTHETIC-PRIVATE", "description"); var projects = new[] { p };
        var review = new ProjectImportReview(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), projects); projects[0] = new(Guid.NewGuid(), "different", null);
        Assert.Same(p, Assert.Single(review.Projects)); Assert.Equal("SYNTHETIC-PRIVATE", review.Projects[0].Title);
        Assert.Equal(new[] { "Description", "Id", "Title" }, typeof(ReviewedPortfolioProject).GetProperties().Select(x => x.Name).OrderBy(x => x));
        foreach (var value in new object[] { p, review, ProjectImportReviewResult.Available(review), new ProjectImportConfirmationResult(ProjectImportState.Committed) })
        { Assert.Equal("{}", JsonSerializer.Serialize(value, value.GetType())); Assert.DoesNotContain("SYNTHETIC", value.ToString()); Assert.DoesNotContain(review.Id.ToString(), value.ToString()); }
    }
    [Fact]
    public void FailureResultsNeverContainReviewedData()
    {
        foreach (var state in Enum.GetValues<ProjectImportState>().Where(x => x != ProjectImportState.Available))
        { var r = ProjectImportReviewResult.Failure(state); Assert.Equal(state, r.State); Assert.Null(r.Review); Assert.Equal("{}", JsonSerializer.Serialize(r)); }
        Assert.Throws<ArgumentException>(() => ProjectImportReviewResult.Failure(ProjectImportState.Available));
    }
    [Fact]
    public void OriginUniqueKeyContainsStableSourceAndDestinationIdentityNotText()
    {
        using var db = Open(); var row = db.Model.GetEntityTypes().Single(x => x.ClrType.Name == "LearningProjectImportOriginRow");
        Assert.Equal(new[] { "OwnerLearningProfileId", "Module", "PortfolioId", "ProjectId", "CvId" }, row.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal("learning_project_import_origins", row.GetTableName()); Assert.Empty(row.GetForeignKeys());
        Assert.DoesNotContain(row.GetProperties(), p => p.Name is "Title" or "Description");
    }
    [Fact]
    public void ReviewScopeAndDurablePortfolioVersionMatchTheFrozenMigrationSnapshot()
    {
        using var db = Open(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("20261126140000_LearningProjectImport", db.Database.GetMigrations().Last());
        var review = db.Model.GetEntityTypes().Single(x => x.ClrType.Name == "LearningProjectImportReviewRow");
        Assert.Equal(new[] { "OwnerLearningProfileId", "Module", "ReviewId" }, review.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.NotNull(review.FindProperty("SourceVersion")); Assert.NotNull(review.FindProperty("SourceFingerprint"));
        var portfolio = db.Model.GetEntityTypes().Single(x => x.ClrType.Name == "LearningPortfolioRow"); Assert.False(portfolio.FindProperty("ContentVersion")!.IsNullable);
    }
    private static LearningDbContext Open() => new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
}
