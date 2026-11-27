using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvShapedPdfTests
{
    private static LearningCv Document(CvTemplate template,string text)
    {
        var cv=new LearningCv(Guid.NewGuid(),Guid.NewGuid(),template);var section=cv.GetSection(CvSectionCategory.Projects);
        cv.EditSection(section.Id,section.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle",text}})]);return cv;
    }
    [Theory]
    [InlineData(CvTemplate.Modern)] [InlineData(CvTemplate.Professional)] [InlineData(CvTemplate.Creative)]
    public void ShapesSinhalaTamilAndCombiningLatinWithLogicalTextAndPinnedEmbeddedFonts(CvTemplate template)
    {
        const string text="தமிழ் கல்வி — සිංහල අධ්‍යාපනය — Cafe\u0301";
        var cv=Document(template,text);var pdf=Encoding.Latin1.GetString(CvPdfDocument.CreateOffline(cv).Bytes);
        Assert.Contains("/BaseFont /NotoSansTamil-Regular",pdf);Assert.Contains("/BaseFont /NotoSansSinhala-Regular",pdf);
        Assert.Contains("/ActualText <FEFF"+Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("ProjectTitle: "+text)),pdf);
        Assert.Contains("/FontFile2",pdf);Assert.Contains("/ToUnicode",pdf);Assert.Contains(" Tm <",pdf);
        Assert.DoesNotContain("/URI",pdf);Assert.DoesNotContain("/Annots",pdf);Assert.DoesNotContain("/Info",pdf);
        Assert.Equal(text,cv.GetSection(CvSectionCategory.Projects).Entries.Single().Fields["ProjectTitle"]);
    }
    [Fact]
    public void WrapsWholeClustersAndPaginatesWithoutCultureDependentPdfNumbers()
    {
        var before=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var text=string.Join(" ",Enumerable.Repeat("தமிழ் கல்வி සිංහල අධ්‍යාපනය",90));
            var bytes=CvPdfDocument.CreateOffline(Document(CvTemplate.Modern,text)).Bytes;var pdf=Encoding.Latin1.GetString(bytes);
            var pages=int.Parse(Regex.Match(pdf,@"/Count (\d+)").Groups[1].Value,CultureInfo.InvariantCulture);Assert.True(pages>=3);
            var offset=int.Parse(Regex.Match(pdf,@"startxref\n(\d+)").Groups[1].Value,CultureInfo.InvariantCulture);
            Assert.Equal("xref",Encoding.ASCII.GetString(bytes,offset,4));Assert.DoesNotMatch(@"\d,\d+ Tm",pdf);
        }
        finally{CultureInfo.CurrentCulture=before;}
    }
    [Fact]
    public void ConsentAndLegacyWithholdingApplyToHiddenLogicalTextAndFontMaps()
    {
        var cv=Document(CvTemplate.Modern,"தமிழ்");var person=cv.GetSection(CvSectionCategory.PersonalInformation);
        cv.EditSection(person.Id,person.Category,[new(Guid.NewGuid(),person.Id,"PRIVATE_LEGACY"),CvStructuredFields.ReviewedEntry(Guid.NewGuid(),person.Id,person.Category,new Dictionary<string,string>{{"Email","hidden@example.test"}})]);
        var pdf=Encoding.Latin1.GetString(CvPdfDocument.CreateOffline(cv).Bytes);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("hidden@example.test")),pdf);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("PRIVATE_LEGACY")),pdf);
        Assert.DoesNotContain("hidden@example.test",pdf);Assert.DoesNotContain("PRIVATE_LEGACY",pdf);
        cv.SetContactInclusionConsent(cv.Id,true);pdf=Encoding.Latin1.GetString(CvPdfDocument.CreateOffline(cv).Bytes);
        Assert.Contains(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("hidden@example.test")),pdf);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("PRIVATE_LEGACY")),pdf);
    }
    [Theory] [InlineData("தமிழ் العربية")] [InlineData("සිංහල 中文")] [InlineData("தமிழ்\u202Ehidden")] [InlineData("தமிழ்\u0000")]
    public void MixedUnsupportedScriptsAndControlsFailClosed(string text)
    {
        var failure=Record.Exception(()=>CvPdfDocument.CreateOffline(Document(CvTemplate.Modern,text)));
        Assert.True(failure is NotSupportedException or ArgumentException);
    }
    [Fact]
    public async Task NativeOwnedFontsSurviveCompactingCollectionsAcrossTemplatesAndRepeatedDisposal()
    {
        var text=string.Join("\n",Enumerable.Repeat("தமிழ் கல்வி සිංහල අධ්‍යාපනය Cafe\u0301",65));
        var documents=Enum.GetValues<CvTemplate>().Select(template=>Document(template,text)).ToArray();
        using var stop=new CancellationTokenSource();
        var collector=Task.Run(()=>
        {
            while(!stop.IsCancellationRequested)
            {
                GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,true,true);
                Thread.Sleep(20); // Exercise compaction without starving unrelated validation timers.
            }
        });
        try
        {
            foreach(var document in documents)
            for(var iteration=0;iteration<3;iteration++)
            {
                var bytes=CvPdfDocument.CreateOffline(document).Bytes;
                Assert.StartsWith("%PDF-1.7",Encoding.ASCII.GetString(bytes,0,8));
                Assert.Contains("%%EOF",Encoding.ASCII.GetString(bytes[^40..]));
            }
        }
        finally{stop.Cancel();await collector;}
    }
    [Fact]
    public void CancellationNeverPublishesAPartialDocument()
    {
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>CvPdfDocument.CreateOffline(Document(CvTemplate.Modern,"தமிழ்"),cancelled.Token));
    }
}
