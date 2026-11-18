using Andril.Common.Moderation;
using Xunit;

namespace Andril.UnitTests.Moderation;

public sealed class ModerationCaseTests
{
    private static readonly Guid ValidCaseId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ValidReportId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly DateTimeOffset ValidOpenedAt =
        new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void Constructor_AcceptsValidModules(string module)
    {
        var moderationCase = new ModerationCase(
            ValidCaseId, ValidReportId, module, ValidOpenedAt);

        Assert.Equal(module, moderationCase.ModuleScope);
    }

    [Fact]
    public void Constructor_PreservesCaseAndReportIdentifiers()
    {
        var moderationCase = new ModerationCase(
            ValidCaseId, ValidReportId,
            "learning", ValidOpenedAt);

        Assert.Equal(ValidCaseId, moderationCase.CaseId);
        Assert.Equal(ValidReportId, moderationCase.SourceReportId);
        Assert.Equal(ValidOpenedAt, moderationCase.OpenedAtUtc);
    }

    [Fact]
    public void Constructor_ConvertsTimestampToUtc()
    {
        var localTime = new DateTimeOffset(
            2026, 11, 18, 14, 30, 0, TimeSpan.FromMinutes(330));

        var moderationCase = new ModerationCase(
            ValidCaseId, ValidReportId,
            "hobbies", localTime);

        Assert.Equal(TimeSpan.Zero,
            moderationCase.OpenedAtUtc.Offset);
        Assert.Equal(ValidOpenedAt,
            moderationCase.OpenedAtUtc);
    }

    [Fact]
    public void Constructor_RejectsEmptyCaseId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ModerationCase(
                Guid.Empty, ValidReportId,
                "learning", ValidOpenedAt));

        Assert.Equal("caseId", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsEmptySourceReportId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ModerationCase(
                ValidCaseId, Guid.Empty,
                "learning", ValidOpenedAt));

        Assert.Equal("sourceReportId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsMissingModule(string? module)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ModerationCase(
                ValidCaseId, ValidReportId,
                module!, ValidOpenedAt));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Hobbies")]
    [InlineData("Social")]
    [InlineData("unknown")]
    [InlineData("admin")]
    [InlineData("learning ")]
    public void Constructor_RejectsUnsupportedModule(string module)
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ModerationCase(
                    ValidCaseId, ValidReportId,
                    module, ValidOpenedAt));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsDefaultTimestamp()
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ModerationCase(
                    ValidCaseId, ValidReportId,
                    "social", default));

        Assert.Equal("openedAtUtc", exception.ParamName);
    }
}
