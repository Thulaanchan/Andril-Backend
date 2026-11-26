using System.Text.Json;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvPersistenceContractTests
{
    private static LearningCv Cv() => new(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern);

    [Fact]
    public void StoredSnapshotRehydratesAllIdentitiesContentTemplateAndConsentLosslessly()
    {
        var cv = Cv(); foreach (var section in cv.Sections.ToArray()) cv.EditSection(section.Id, section.Category, [new(Guid.NewGuid(), section.Id, "SYNTHETIC-" + section.Category)]);
        cv.SelectTemplate(CvTemplate.Creative); cv.SetContactInclusionConsent(cv.Id, true);
        var copy = LearningCv.FromStoredSnapshot(cv.Id, cv.OwnerLearningProfileId, cv.Template, cv.Sections, cv.IncludeContactInformation);
        Assert.Equal(cv.Id, copy.Id); Assert.Equal(cv.OwnerLearningProfileId, copy.OwnerLearningProfileId); Assert.Equal(cv.Template, copy.Template); Assert.True(copy.IncludeContactInformation);
        Assert.Equal(cv.Sections.Select(s => s.Id), copy.Sections.Select(s => s.Id));
        Assert.Equal(cv.Sections.SelectMany(s => s.Entries).Select(e => (e.Id, e.SectionId, e.Text)), copy.Sections.SelectMany(s => s.Entries).Select(e => (e.Id, e.SectionId, e.Text)));
        copy.EditSection(copy.Sections[0].Id, copy.Sections[0].Category, []); Assert.Single(cv.Sections[0].Entries);
    }

    [Fact]
    public void InvalidStoredShapeCannotLoseCategoriesOrReuseIdentities()
    {
        var cv = Cv();
        Assert.Throws<ArgumentException>(() => LearningCv.FromStoredSnapshot(cv.Id, cv.OwnerLearningProfileId, cv.Template, cv.Sections.Skip(1), false));
        Assert.Throws<ArgumentException>(() => LearningCv.FromStoredSnapshot(cv.Id, cv.OwnerLearningProfileId, cv.Template, Enumerable.Repeat(cv.Sections[0], 7), false));
        var entries = cv.Sections.Select(s => new CvSection(s.Id, s.Category, [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), s.Id, null)])).ToArray();
        Assert.Throws<ArgumentException>(() => LearningCv.FromStoredSnapshot(cv.Id, cv.OwnerLearningProfileId, cv.Template, entries, false));
    }

    [Fact]
    public void SaveCommandsReuseDomainValidationWithoutNormalizingOrClassifyingText()
    {
        var cv = Cv(); var section = cv.Sections[0]; var entry = new CvSection.Entry(Guid.NewGuid(), section.Id, " <script>UNCLASSIFIED</script> ");
        CvSaveChange.Section(section.Id, section.Category, [entry]).Apply(cv); Assert.Equal(entry.Text, cv.Sections[0].Entries.Single().Text);
        Assert.Throws<ArgumentException>(() => CvSaveChange.Template((CvTemplate)99).Apply(cv)); Assert.Equal(CvTemplate.Modern, cv.Template);
        Assert.Throws<ArgumentException>(() => CvSaveChange.Section(section.Id, section.Category, [new(Guid.NewGuid(), Guid.NewGuid(), "wrong")]));
        CvSaveChange.ContactConsent(true).Apply(cv); Assert.True(cv.IncludeContactInformation);
        Assert.DoesNotContain("UNCLASSIFIED", CvPreview.CreateOffline(cv).Html);
    }

    [Fact]
    public void PrivateFailureAndPreviewResultsDoNotBecomePublicPayloads()
    {
        Assert.Null(CvReadResult.Failure(CvOperationState.Denied).Cv); Assert.Null(CvListResult.Failure(CvOperationState.Unavailable).CvIds);
        var cv = Cv(); var read = CvReadResult.Available(cv); var preview = CvPreviewResult.Available(CvPreview.CreateOffline(cv));
        Assert.Equal("{}", JsonSerializer.Serialize(read)); Assert.Equal("{}", JsonSerializer.Serialize(preview)); Assert.DoesNotContain(cv.Id.ToString(), read.ToString());
    }

    [Fact]
    public void CvModelUsesExistingLearningHistoryAndPreservesFrozenV1Migration()
    {
        using var db = new LearningDbContext(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Pooling=false").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(new[] { "20261125100000_LearningDurableRevisions", "20261126090000_LearningCvPersistence", "20261126110000_LearningPortfolioPersistence" }, db.Database.GetMigrations());
        var entity = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "LearningCvRow");
        Assert.Equal(new[] { "OwnerLearningProfileId", "Module", "CvId" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Empty(entity.GetForeignKeys());
        var script = db.GetService<IMigrator>().GenerateScript("20261125100000_LearningDurableRevisions", "20261126090000_LearningCvPersistence");
        Assert.Contains("FORCE ROW LEVEL SECURITY", script); Assert.Contains("lock_learning_owners", script);
        Assert.DoesNotContain("ALTER TABLE andril_identity", script); Assert.DoesNotContain("ON DELETE CASCADE", script);
    }
}
