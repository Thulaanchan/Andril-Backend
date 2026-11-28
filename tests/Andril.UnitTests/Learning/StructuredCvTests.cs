using System.Text;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class StructuredCvTests
{
    [Fact]
    public void LongReviewedTokensRenderWhileEmbeddedContactAndPrivateContentRemainBlocked()
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),CvTemplate.Modern);
        var section=cv.GetSection(CvSectionCategory.Projects);var text=new string('a',32_768);
        void Set(string value)=>cv.EditSection(section.Id,section.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{"Description",value}})]);
        Set(text);Assert.Contains(text,CvPreview.CreateOffline(cv).Html);
        cv.SetContactInclusionConsent(cv.Id,true);
        Set(text+"@example.test");Assert.DoesNotContain(text,CvPreview.CreateOffline(cv).Html);
        Assert.Throws<ArgumentException>(()=>Set(text+" password"));
        Set(text+"@example.test");Assert.DoesNotContain(text,CvPreview.CreateOffline(cv).Html);
        cv.SetContactInclusionConsent(cv.Id,true);Assert.DoesNotContain(text,CvPreview.CreateOffline(cv).Html);
        Assert.Throws<ArgumentException>(()=>Set(text+" passport"));
    }

    [Fact]
    public void DateValidationIsCultureIndependentAndInternalIdsCannotBecomeRenderableContent()
    {
        var id=Guid.NewGuid();var section=Guid.NewGuid();
        var good=CvStructuredFields.ReviewedEntry(id,section,CvSectionCategory.Education,new Dictionary<string,string>{{"StartDate","2024-02-29"}});
        Assert.Equal("2024-02-29",good.Fields["StartDate"]);
        Assert.Throws<ArgumentException>(()=>CvStructuredFields.ReviewedEntry(id,section,CvSectionCategory.Education,new Dictionary<string,string>{{"StartDate","2023-02-29"}}));
        Assert.Throws<ArgumentException>(()=>CvStructuredFields.ReviewedEntry(id,section,CvSectionCategory.Projects,new Dictionary<string,string>{{"Description",Guid.NewGuid().ToString()}}));
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),CvTemplate.Modern);var education=cv.GetSection(CvSectionCategory.Education);
        cv.EditSection(education.Id,education.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),education.Id,education.Category,new Dictionary<string,string>{{"StartDate","2024-02-29"}})]);
        Assert.Contains("2024-02-29",CvPreview.CreateOffline(cv).Html);Assert.Contains("2024-02-29",Encoding.ASCII.GetString(CvPdfDocument.CreateOffline(cv).Bytes));
    }
    [Theory]
    [InlineData(CvTemplate.Modern)] [InlineData(CvTemplate.Professional)] [InlineData(CvTemplate.Creative)]
    public void EveryApprovedCategoryRendersOnlyExplicitReviewedFieldsAcrossPreviewAndPdf(CvTemplate template)
    {
        var cv = new LearningCv(Guid.NewGuid(),Guid.NewGuid(),template);
        foreach(var section in cv.Sections)
        {
            var key=CvStructuredFields.Names(section.Category).First();
            cv.EditSection(section.Id,section.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{key,"approved professional value"}}),new(Guid.NewGuid(),section.Id,"UNCLASSIFIED_SECRET")]);
        }
        var html=CvPreview.CreateOffline(cv).Html; var pdf=Encoding.ASCII.GetString(CvPdfDocument.CreateOffline(cv).Bytes);
        foreach(var category in Enum.GetValues<CvSectionCategory>()){Assert.Contains(category.ToString(),html);Assert.Contains(category.ToString(),pdf);}
        Assert.Contains("approved professional value",html);Assert.Contains("approved professional value",pdf);
        Assert.DoesNotContain("UNCLASSIFIED_SECRET",html);Assert.DoesNotContain("UNCLASSIFIED_SECRET",pdf);
        Assert.DoesNotContain("/URI",pdf);Assert.DoesNotContain("/Info",pdf);Assert.DoesNotContain("/Annots",pdf);
    }
    [Fact]
    public void ConsentIsOffByDefaultAndNeverClassifiesLegacyOrEmbeddedContact()
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),CvTemplate.Modern);var personal=cv.GetSection(CvSectionCategory.PersonalInformation);
        cv.EditSection(personal.Id,personal.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),personal.Id,personal.Category,new Dictionary<string,string>{{"ProfessionalDisplayName","Professional Name"},{"Email","private@example.test"},{"CityCountry","Private City"},{"ProfessionalProfileLinks","https://example.test/profile"}}),new(Guid.NewGuid(),personal.Id,"LEGACY_PRIVATE_CONTACT")]);
        var summary=cv.GetSection(CvSectionCategory.ProfessionalSummary);cv.EditSection(summary.Id,summary.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),summary.Id,summary.Category,new Dictionary<string,string>{{"CareerSummary","Embedded private@example.test"}})]);
        Assert.False(cv.IncludeContactInformation);
        Assert.DoesNotContain("private@example.test",CvPreview.CreateOffline(cv).Html);
        cv.SetContactInclusionConsent(cv.Id,true);var html=CvPreview.CreateOffline(cv).Html;
        Assert.Contains("private@example.test",html);Assert.Contains("Private City",html);Assert.DoesNotContain("Embedded",html);Assert.DoesNotContain("LEGACY_PRIVATE_CONTACT",html);
        cv.SetContactInclusionConsent(cv.Id,false);Assert.DoesNotContain("private@example.test",Encoding.ASCII.GetString(CvPdfDocument.CreateOffline(cv).Bytes));
    }
    [Theory]
    [InlineData("Password","secret")] [InlineData("Description","wrong section")] [InlineData("ProfessionalDisplayName","passport 123")]
    [InlineData("ProfessionalDisplayName","123-45-6789")] [InlineData("ProfessionalDisplayName","12 Main Street")]
    [InlineData("ProfessionalProfileLinks","javascript:alert(1)")] [InlineData("Email","not an email")]
    public void InvalidProhibitedOrCrossSectionFieldClassificationIsRejected(string key,string value)
    {Assert.Throws<ArgumentException>(()=>CvStructuredFields.ReviewedEntry(Guid.NewGuid(),Guid.NewGuid(),CvSectionCategory.PersonalInformation,new Dictionary<string,string>{{key,value}}));}
    [Fact]
    public void ExplicitReviewedMergePreservesLegacyTextEntryIdsAndOtherSections()
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),CvTemplate.Professional);var section=cv.GetSection(CvSectionCategory.Projects);var id=Guid.NewGuid();
        cv.EditSection(section.Id,section.Category,[new(id,section.Id,"IMPORTED_UNCLASSIFIED")]);
        CvSaveChange.ReviewedFields(section.Category,[CvStructuredFields.ReviewedEntry(id,section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle","Reviewed project"}})]).Apply(cv);
        var entry=Assert.Single(cv.GetSection(section.Category).Entries);Assert.Equal(id,entry.Id);Assert.Equal("IMPORTED_UNCLASSIFIED",entry.Text);
        foreach(var template in Enum.GetValues<CvTemplate>()){cv.SelectTemplate(template);Assert.Equal("Reviewed project",cv.GetSection(section.Category).Entries[0].Fields["ProjectTitle"]);}
        Assert.DoesNotContain("IMPORTED_UNCLASSIFIED",CvPreview.CreateOffline(cv).Html);
    }
    [Fact]
    public void HtmlIsEscapedPdfMultipageAndUnsupportedUnicodeFailsWithoutCorruptingStorage()
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),CvTemplate.Creative);var s=cv.GetSection(CvSectionCategory.Projects);
        cv.EditSection(s.Id,s.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),s.Id,s.Category,new Dictionary<string,string>{{"Description",string.Join('\n',Enumerable.Repeat("<script>inert text</script>",110))}})]);
        Assert.DoesNotContain("<script>",CvPreview.CreateOffline(cv).Html);var bytes=CvPdfDocument.CreateOffline(cv).Bytes;var pdf=Encoding.ASCII.GetString(bytes);
        Assert.Contains("/Count 4",pdf);bytes[0]=0;Assert.NotEqual((byte)0,CvPdfDocument.CreateOffline(cv).Bytes[0]);
        cv.EditSection(s.Id,s.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),s.Id,s.Category,new Dictionary<string,string>{{"ProjectTitle","العربية"}})]);
        Assert.Contains("العربية",CvPreview.CreateOffline(cv).Html);Assert.Throws<NotSupportedException>(()=>CvPdfDocument.CreateOffline(cv));
        Assert.Equal("العربية",cv.GetSection(s.Category).Entries[0].Fields["ProjectTitle"]);
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>CvPdfDocument.CreateOffline(cv,cancel.Token));
    }
}
