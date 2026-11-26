using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Andril.Learning.CV;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class CvPdfDocumentTests
{
    [Theory]
    [InlineData(CvTemplate.Modern, "Helvetica")]
    [InlineData(CvTemplate.Professional, "Times-Roman")]
    [InlineData(CvTemplate.Creative, "Courier")]
    public void TwoPagePdfHasValidObjectOffsetsStreamsAllSevenLabelsAndTemplate(CvTemplate template, string font)
    {
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), template);
        var bytes = CvPdfDocument.CreateOffline(cv).Bytes;
        var pdf = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF-1.4\n", pdf); Assert.EndsWith("%%EOF\n", pdf);
        Assert.Contains("/Count 2", pdf); Assert.Equal(2, Regex.Matches(pdf, @"/Type /Page\s").Count);
        Assert.Contains("/BaseFont /" + font, pdf);
        foreach (var section in cv.Sections) Assert.Contains("(" + section.Category + ") Tj", pdf);
        Assert.Contains("(" + template + ") Tj", pdf);

        // Parse the cross-reference table and byte-lengths independently of the writer.
        var start = Regex.Match(pdf, @"startxref\n(\d+)\n%%EOF"); Assert.True(start.Success);
        var offset = int.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n0 8\n", pdf[offset..]);
        var entries = pdf[offset..].Split('\n').Skip(3).Take(7).ToArray(); Assert.Equal(7, entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var objectOffset = int.Parse(entries[i][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i + 1} 0 obj\n", pdf[objectOffset..]);
        }
        var streams = Regex.Matches(pdf, @"/Length (\d+) >>\nstream\n([\s\S]*?)endstream"); Assert.Equal(2, streams.Count);
        foreach (Match stream in streams) Assert.Equal(int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture), Encoding.ASCII.GetByteCount(stream.Groups[2].Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HtmlAndPdfWithholdUnclassifiedEmbeddedContactsAndInternalDataAcrossAllSurfaces(bool consent)
    {
        const string text = "SYNTHETIC-PRIVATE me@example.test +44 123456789 https://private.invalid <script>secret</script> ) /URI (file:secret) தமிழ்";
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Creative);
        foreach (var section in cv.Sections) cv.EditSection(section.Id, section.Category, [new(Guid.NewGuid(), section.Id, text)]);
        cv.SetContactInclusionConsent(cv.Id, consent);
        var original = cv.Sections;
        var document = CvPdfDocument.CreateOffline(cv); var pdf = Encoding.ASCII.GetString(document.Bytes); var html = CvPreview.CreateOffline(cv).Html;
        foreach (var marker in new[] { "SYNTHETIC", "example.test", "+44", "private.invalid", "script", "secret", cv.Id.ToString(), cv.OwnerLearningProfileId.ToString() })
        { Assert.DoesNotContain(marker, pdf); Assert.DoesNotContain(marker, html); }
        foreach (var marker in new[] { "/Info", "/Metadata", "/Annot", "/URI", "/Action", "/JavaScript", "/EmbeddedFile", "/Names", "/OCProperties", "/AcroForm" }) Assert.DoesNotContain(marker, pdf);
        Assert.All(cv.Sections, s => { Assert.Single(s.Entries); Assert.Equal(text, s.Entries[0].Text); Assert.DoesNotContain(s.Id.ToString(), pdf); Assert.DoesNotContain(s.Entries[0].Id.ToString(), pdf); });
        Assert.Same(original, cv.Sections); Assert.Equal(consent, cv.IncludeContactInformation);
        Assert.Equal("{}", JsonSerializer.Serialize(document)); Assert.DoesNotContain("%PDF", document.ToString());
        var result = CvPdfResult.Available(document); Assert.Equal("{}", JsonSerializer.Serialize(result)); Assert.DoesNotContain("%PDF", result.ToString());
    }

    [Fact]
    public void TemplateSwitchingPreservesEditingStateAndPreviouslyGeneratedSnapshots()
    {
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern); var sections = cv.Sections;
        var before = CvPdfDocument.CreateOffline(cv); var savedBytes = before.Bytes;
        foreach (var template in Enum.GetValues<CvTemplate>()) { cv.SelectTemplate(template); _ = CvPdfDocument.CreateOffline(cv); Assert.Same(sections, cv.Sections); }
        Assert.Equal(savedBytes, before.Bytes); Assert.False(cv.IncludeContactInformation);
        var writableCopy = before.Bytes; writableCopy[0] = 0; Assert.Equal((byte)'%', before.Bytes[0]);
    }

    [Fact]
    public void CancellationAndFailuresNeverProduceAnAvailableDocument()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cv = new LearningCv(Guid.NewGuid(), Guid.NewGuid(), CvTemplate.Modern);
        Assert.ThrowsAny<OperationCanceledException>(() => CvPdfDocument.CreateOffline(cv, cts.Token));
        foreach (var state in Enum.GetValues<CvOperationState>().Where(s => s != CvOperationState.Available))
        { var result = CvPdfResult.Failure(state); Assert.Null(result.Document); Assert.Equal(state, result.State); Assert.Equal("{}", JsonSerializer.Serialize(result)); }
        Assert.Throws<ArgumentException>(() => CvPdfResult.Failure(CvOperationState.Available));
        Assert.Throws<ArgumentNullException>(() => CvPdfDocument.CreateOffline(null!));
    }
}
