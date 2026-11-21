using System.Globalization;
using System.Text.Json;
using Andril.Common.Diary;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class DiaryReflectionTests
{
    private const string PrivateTitle = "PRIVATE-REFLECTION-TITLE";
    private const string PrivateBody = "  PRIVATE-REFLECTION-BODY\r\nExact manual content  ";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("A valid title")]
    [InlineData("  தமிழ் සිංහල e\u0301 👩‍💻  ")]
    public void OptionalTitleAndValidBody_ArePreservedExactly(string? title)
    {
        var reflection = new DiaryReflection(title, PrivateBody);
        Assert.Equal(title, reflection.Title);
        Assert.Equal(PrivateBody, reflection.Body);
    }

    [Theory]
    [InlineData("a", 100, true)]
    [InlineData("a", 101, false)]
    [InlineData("e\u0301", 100, true)]
    [InlineData("e\u0301", 101, false)]
    [InlineData("👩‍💻", 100, true)]
    [InlineData("👩‍💻", 101, false)]
    public void TitleBoundary_UsesTextElementsWithoutNormalization(string unit, int count, bool accepted)
    {
        var title = string.Concat(Enumerable.Repeat(unit, count));
        Assert.Equal(count, new StringInfo(title).LengthInTextElements);
        if (accepted)
        {
            var reflection = new DiaryReflection(title, PrivateBody);
            Assert.Equal(title, reflection.Title);
            Assert.Equal(PrivateBody, reflection.Body);
        }
        else Assert.Throws<ArgumentException>("title", () => new DiaryReflection(title, PrivateBody));
    }

    [Theory]
    [InlineData("a", 10_000, true)]
    [InlineData("a", 10_001, false)]
    [InlineData("e\u0301", 10_000, true)]
    [InlineData("e\u0301", 10_001, false)]
    [InlineData("👩‍💻", 10_000, true)]
    [InlineData("👩‍💻", 10_001, false)]
    public void BodyBoundary_UsesTextElementsWithoutNormalization(string unit, int count, bool accepted)
    {
        var body = string.Concat(Enumerable.Repeat(unit, count));
        Assert.Equal(count, new StringInfo(body).LengthInTextElements);
        if (accepted)
        {
            var reflection = new DiaryReflection(PrivateTitle, body);
            Assert.Equal(PrivateTitle, reflection.Title);
            Assert.Equal(body, reflection.Body);
        }
        else Assert.Throws<ArgumentException>("body", () => new DiaryReflection(PrivateTitle, body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u2003")]
    public void MissingOrWhitespaceBody_IsRejected(string? body)
    {
        Assert.Throws<ArgumentException>("body", () => new DiaryReflection(PrivateTitle, body!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  Edited e\u0301 👩‍💻 title  ")]
    public void ValidEdit_ReturnsIndependentContentAndPreservesOriginalAndUnrelatedReflection(string? title)
    {
        var original = Original();
        var unrelated = new DiaryReflection("Other title", "Other body");
        const string body = "  Edited private body\r\n  ";
        var edited = original.WithContent(title, body);
        Assert.NotSame(original, edited);
        Assert.Equal(title, edited.Title);
        Assert.Equal(body, edited.Body);
        AssertOriginal(original);
        Assert.Equal("Other title", unrelated.Title);
        Assert.Equal("Other body", unrelated.Body);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    public void OversizedEdit_IsRejectedAndPreservesCompleteExistingState(string field)
    {
        var original = Original();
        var unrelated = new DiaryReflection("Other title", "Other body");
        var title = field == "title" ? string.Concat(Enumerable.Repeat("e\u0301", 101)) : "Changed title";
        var body = field == "body" ? string.Concat(Enumerable.Repeat("👩‍💻", 10_001)) : "Changed body";
        Assert.Throws<ArgumentException>(field, () => original.WithContent(title, body));
        AssertOriginal(original);
        Assert.Equal("Other title", unrelated.Title);
        Assert.Equal("Other body", unrelated.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u2003")]
    public void InvalidBodyEdit_DoesNotPartiallyApplyReplacementTitle(string? body)
    {
        var original = Original();
        Assert.Throws<ArgumentException>("body", () => original.WithContent("Changed title", body!));
        AssertOriginal(original);
    }

    [Fact]
    public void MaximumBoundaryEdit_AcceptsExactUnicodeContentAndPreservesPreviousValue()
    {
        var original = Original();
        var title = string.Concat(Enumerable.Repeat("e\u0301", 100));
        var body = string.Concat(Enumerable.Repeat("👩‍💻", 10_000));
        var edited = original.WithContent(title, body);
        Assert.Equal(100, new StringInfo(edited.Title!).LengthInTextElements);
        Assert.Equal(10_000, new StringInfo(edited.Body).LengthInTextElements);
        Assert.Equal(title, edited.Title);
        Assert.Equal(body, edited.Body);
        AssertOriginal(original);
    }

    [Fact]
    public void DefaultJson_ExcludesAllPrivatePropertiesAndPreservesPopulatedSource()
    {
        var reflection = Original();
        var output = JsonSerializer.Serialize(reflection);
        Assert.Equal("{}", output);
        using var json = JsonDocument.Parse(output);
        Assert.Empty(json.RootElement.EnumerateObject());
        Assert.DoesNotContain(PrivateTitle, output);
        Assert.DoesNotContain(PrivateBody, output);
        AssertOriginal(reflection);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    public void ConstructorAndEditErrors_DoNotDisclosePrivateContent(string field)
    {
        var original = Original();
        var title = field == "title" ? PrivateTitle + new string('x', 101) : PrivateTitle;
        var body = field == "body" ? PrivateBody + new string('x', 10_001) : PrivateBody;
        var creationError = Assert.Throws<ArgumentException>(field, () => new DiaryReflection(title, body));
        var editError = Assert.Throws<ArgumentException>(field, () => original.WithContent(title, body));
        foreach (var output in new[] { creationError.ToString(), editError.ToString(), original.ToString() })
        {
            Assert.DoesNotContain(PrivateTitle, output);
            Assert.DoesNotContain(PrivateBody, output);
            Assert.DoesNotContain(title, output);
            Assert.DoesNotContain(body, output);
        }
        AssertOriginal(original);
    }

    [Fact]
    public void PublicContentSurface_HasNoWritablePropertiesOrFields()
    {
        Assert.True(typeof(DiaryReflection).IsSealed);
        Assert.All(typeof(DiaryReflection).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(DiaryReflection).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    private static DiaryReflection Original() => new(PrivateTitle, PrivateBody);

    private static void AssertOriginal(DiaryReflection reflection)
    {
        Assert.Equal(PrivateTitle, reflection.Title);
        Assert.Equal(PrivateBody, reflection.Body);
    }
}
