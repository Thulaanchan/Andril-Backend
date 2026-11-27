using System.Text;
using System.Text.RegularExpressions;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvUnicodePdfTests
{
    private static LearningCv Document(CvTemplate template,string text)
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),template);var section=cv.GetSection(CvSectionCategory.Projects);
        cv.EditSection(section.Id,section.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle",text}})]);return cv;
    }
    [Theory] [InlineData(CvTemplate.Modern,"NotoSans-Regular")] [InlineData(CvTemplate.Professional,"NotoSerif-Regular")] [InlineData(CvTemplate.Creative,"NotoSansMono-Regular")]
    public void PinnedEmbeddedFontsEncodePrecomposedLatinGreekAndCyrillicWithUnicodeMaps(CvTemplate template,string font)
    {
        var doc=CvPdfDocument.CreateOffline(Document(template,"Zoë François — Ελληνικά — Привет"));var bytes=doc.Bytes;var pdf=Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.7",pdf);Assert.Contains("/FontFile2 6 0 R",pdf);Assert.Contains("/BaseFont /"+font,pdf);Assert.Contains("/ToUnicode 8 0 R",pdf);
        Assert.Contains("> <03",pdf);Assert.Contains("> <04",pdf);Assert.DoesNotContain("/URI",pdf);Assert.DoesNotContain("/Info",pdf);Assert.DoesNotContain("/Annots",pdf);
        var xref=int.Parse(Regex.Match(pdf,@"startxref\n(\d+)\n").Groups[1].Value);Assert.Equal("xref",Encoding.ASCII.GetString(bytes,xref,4));
    }
    [Theory] [InlineData("العربية")] [InlineData("中文")] [InlineData("abc\u202Ehidden")]
    public void UnsupportedGlyphsComplexShapingAndDirectionControlsDoNotSilentlyCorruptContent(string text)
    {Assert.Throws<NotSupportedException>(()=>CvPdfDocument.CreateOffline(Document(CvTemplate.Modern,text)));}
    [Fact]
    public void ConsentOffAndUnclassifiedContentNeverEnterUnicodeMapsOrHiddenPdfObjects()
    {
        var cv=Document(CvTemplate.Modern,"Renée — Εκπαίδευση");var s=cv.GetSection(CvSectionCategory.PersonalInformation);
        cv.EditSection(s.Id,s.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),s.Id,s.Category,new Dictionary<string,string>{{"Email","hidden@example.test"}}),new(Guid.NewGuid(),s.Id,"LEGACY_SECRET")]);
        var pdf=Encoding.Latin1.GetString(CvPdfDocument.CreateOffline(cv).Bytes);Assert.DoesNotContain("hidden@example.test",pdf);Assert.DoesNotContain("LEGACY_SECRET",pdf);
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>CvPdfDocument.CreateOffline(cv,cancel.Token));
    }
    [Fact]
    public void MultipageUnicodeGenerationKeepsSevenSectionsAndProducesDefensiveBytes()
    {
        var cv=Document(CvTemplate.Professional,string.Join('\n',Enumerable.Repeat("Éducation Ελληνικά Привет",110)));
        var document=CvPdfDocument.CreateOffline(cv);var pdf=Encoding.Latin1.GetString(document.Bytes);Assert.Contains("/Count 4",pdf);
        var copy=document.Bytes;copy[0]=0;Assert.NotEqual((byte)0,document.Bytes[0]);
    }
}
