using System.Globalization;
using Andril.Common.Validators;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class PrivateProfileFieldValidatorTests
{
    [Theory]
    [InlineData("Alex_2000", "Alex_2000")]
    [InlineData("  A B-C_2  ", "A B-C_2")]
    [InlineData("123", "123")]
    [InlineData("௧௨௩", "௧௨௩")]
    [InlineData("AdminStudent", "AdminStudent")]
    [InlineData("SupportEngineer", "SupportEngineer")]
    [InlineData("andril-team-member", "andril-team-member")]
    public void ValidNamesAreTrimmedAndPreserved(string input, string expected)
    {
        Assert.True(PrivateProfileFieldValidator.TryNormalizeDisplayName(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("தமிழ்", 3)]
    [InlineData("සිංහල", 3)]
    [InlineData("A\u0301BC", 3)]
    [InlineData("க்\u200Dஷ் ABC", null)]
    [InlineData("க்\u200Cஷ் ABC", null)]
    [InlineData("ක\u0DCA\u200Dර ABC", null)]
    [InlineData("ක\u200D්ර ABC", null)]
    public void MultilingualCombiningTextIsPreserved(string name, int? expectedCount)
    {
        var count = new StringInfo(name).LengthInTextElements;
        if (expectedCount is int expected) Assert.Equal(expected, count);
        Assert.InRange(count, 3, 30);
        Assert.True(PrivateProfileFieldValidator.TryNormalizeDisplayName(name, out var normalized));
        Assert.Equal(name, normalized);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void NameBoundariesCountGraphemes(int count, bool accepted)
    {
        var name = string.Concat(Enumerable.Repeat("a\u0301", count));
        Assert.Equal(count, new StringInfo(name).LengthInTextElements);
        Assert.Equal(accepted, PrivateProfileFieldValidator.TryNormalizeDisplayName(name, out var normalized));
        Assert.Equal(accepted ? name : "", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("_ -_")]
    [InlineData("A.B")]
    [InlineData("Ab€c")]
    [InlineData("Ab😀")]
    [InlineData("Abℹ")]
    [InlineData("Ab👩‍💻")]
    [InlineData("Ab1\u20E3")]
    [InlineData("Ab1\uFE0F\u20E3")]
    [InlineData("Ab\uFE0Fc")]
    [InlineData("\u0301Abc")]
    [InlineData("A-\u0301bc")]
    [InlineData("Ab\u034Fc")]
    [InlineData("Ab\u200Bc")]
    [InlineData("Ab\u202Ec")]
    [InlineData("Ab\u2067c")]
    [InlineData("Ab\u200Dc")]
    [InlineData("Ab\u200Cc")]
    [InlineData("க்\u200Dක ABC")]
    [InlineData("Ab\0c")]
    public void InvalidNamesReturnAnEmptyResult(string? name)
    {
        Assert.False(PrivateProfileFieldValidator.TryNormalizeDisplayName(name, out var normalized));
        Assert.Equal("", normalized);
    }

    [Theory]
    [InlineData("andril")]
    [InlineData("admin")]
    [InlineData("administrator")]
    [InlineData("moderator")]
    [InlineData("support")]
    [InlineData("system")]
    [InlineData("official")]
    [InlineData("andriladmin")]
    [InlineData("andrilmoderator")]
    [InlineData("andrilsupport")]
    [InlineData("andrilofficial")]
    [InlineData("andrilteam")]
    [InlineData(" ADMIN ")]
    [InlineData("Andril_Admin")]
    [InlineData("Andril Support")]
    [InlineData("Andril-Support")]
    [InlineData("A_d-m iN")]
    public void ReservedNamesAreRejected(string name) => InvalidNamesReturnAnEmptyResult(name);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("English தமிழ் සිංහල 👩‍💻")]
    [InlineData("  first\r\nsecond\nthird  ")]
    public void OptionalBioAcceptsPlainUnicodeText(string? bio) =>
        Assert.True(PrivateProfileFieldValidator.IsValidBio(bio));

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void BioBoundariesCountGraphemes(int count, bool accepted)
    {
        var bio = string.Concat(Enumerable.Repeat("👩‍💻", count));
        Assert.Equal(count, new StringInfo(bio).LengthInTextElements);
        Assert.Equal(accepted, PrivateProfileFieldValidator.IsValidBio(bio));
    }

    [Fact]
    public void NulAndMalformedUtf16AreRejected()
    {
        foreach (var text in new[] { "\0Abc", "\uD800Abc", "Abc\uDC00", "Abc\uD800" })
        {
            InvalidNamesReturnAnEmptyResult(text);
            Assert.False(PrivateProfileFieldValidator.IsValidBio(text));
        }
    }
}
