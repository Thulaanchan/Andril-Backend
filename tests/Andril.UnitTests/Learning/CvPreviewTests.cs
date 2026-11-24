using System.Text.Json;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvPreviewTests
{
    private const string Unsafe = "<script>alert('PRIVATE')</script><img src=\"https://private.example.test/x\" onerror=\"alert(1)\"> private@example.test & <svg/onload=alert(2)>";

    [Theory]
    [InlineData(CvTemplate.Modern)]
    [InlineData(CvTemplate.Professional)]
    [InlineData(CvTemplate.Creative)]
    public void StructuralPreview_ContainsSevenSectionsAndTemplateButNoPrivateTextOrIdentities(CvTemplate template)
    {
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), template);
        Assert.False(cv.IncludeContactInformation);
        foreach (var section in cv.Sections.ToArray())
            cv.EditSection(section.Id, section.Category, [new(Guid.NewGuid(), section.Id, Unsafe)]);
        var sections = cv.Sections; var originalTemplate = cv.Template;
        var preview = CvPreview.CreateOffline(cv);
        Assert.Contains("<p>" + template + "</p>", preview.Html);
        Assert.Equal(7, preview.Html.Split("<section>", StringSplitOptions.None).Length - 1);
        foreach (var section in sections)
        {
            Assert.Contains("<h2>" + section.Category + "</h2>", preview.Html);
            Assert.DoesNotContain(section.Id.ToString(), preview.Html);
            var entry = Assert.Single(section.Entries);
            Assert.DoesNotContain(entry.Id.ToString(), preview.Html);
            Assert.Equal(Unsafe, entry.Text);
        }
        foreach (var forbidden in new[] { Unsafe, "PRIVATE", "private@example.test", "https://", "<script", "<img", "<svg", "onerror", "onload",
            cv.Id.ToString(), cv.OwnerLearningProfileId.ToString() })
            Assert.DoesNotContain(forbidden, preview.Html);
        Assert.Same(sections, cv.Sections); Assert.Equal(originalTemplate, cv.Template);
        Assert.False(cv.IncludeContactInformation); Assert.True(cv.IsPrivate); Assert.Equal("learning", cv.Module);
        Assert.Equal("{}", JsonSerializer.Serialize(preview));
        Assert.DoesNotContain(preview.Html, preview.ToString());
    }

    [Fact]
    public void ConsentChanges_NeverClassifyGenericTextOrMutateEditingState()
    {
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern);
        foreach (var section in cv.Sections.ToArray())
            cv.EditSection(section.Id, section.Category, [new(Guid.NewGuid(), section.Id, Unsafe)]);
        var sections = cv.Sections;
        var before = CvPreview.CreateOffline(cv);
        cv.SetContactInclusionConsent(cv.Id, true);
        var included = CvPreview.CreateOffline(cv);
        Assert.True(cv.IncludeContactInformation);
        cv.SetContactInclusionConsent(cv.Id, false);
        var revoked = CvPreview.CreateOffline(cv);
        Assert.Equal(before.Html, included.Html); Assert.Equal(before.Html, revoked.Html);
        Assert.Same(sections, cv.Sections);
        Assert.All(cv.Sections, section => Assert.Equal(Unsafe, Assert.Single(section.Entries).Text));
        Assert.Contains("Unclassified content withheld", revoked.Html);
    }

    [Fact]
    public void PreviewSnapshots_PreserveTemplateSelectionAndDoNotReadOtherDocuments()
    {
        var first = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern);
        var other = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Creative);
        var section = other.GetSection(CvSectionCategory.PersonalInformation);
        other.EditSection(section.Id, section.Category, [new(Guid.NewGuid(), section.Id, Unsafe)]);
        other.SetContactInclusionConsent(other.Id, true);
        var otherState = other.Sections;
        var old = CvPreview.CreateOffline(first);
        first.SelectTemplate(CvTemplate.Professional);
        var next = CvPreview.CreateOffline(first);
        Assert.Contains("<p>Modern</p>", old.Html);
        Assert.Contains("<p>Professional</p>", next.Html);
        Assert.Equal(CvTemplate.Professional, first.Template);
        Assert.Equal(CvTemplate.Creative, other.Template); Assert.True(other.IncludeContactInformation);
        Assert.Same(otherState, other.Sections);
        Assert.DoesNotContain(Unsafe, next.Html);
        // No actor/profile/module parameter or external reader exists; this proves only local
        // structural minimization, not production ownership, release authorization or revocation.
    }

    [Fact]
    public void MissingDocument_IsRejected() =>
        Assert.Throws<ArgumentNullException>("cv", () => CvPreview.CreateOffline(null!));
}
