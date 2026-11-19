using Andril.Common.Moderation;
using Xunit;

namespace Andril.UnitTests.Moderation;

public sealed class ModerationReportTests
{
    private static readonly Guid ValidReportId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ValidReporterId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid ValidResourceId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset ValidSubmittedAt =
        new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void Constructor_AcceptsValidModules(string module)
    {
        var report = CreateReport(moduleScope: module);

        Assert.Equal(module, report.ModuleScope);
    }

    [Fact]
    public void Constructor_PreservesIdentifiersAndMetadata()
    {
        var report = CreateReport();

        Assert.Equal(ValidReportId, report.ReportId);
        Assert.Equal(ValidReporterId, report.ReporterModuleProfileId);
        Assert.Equal(ValidResourceId, report.ReportedResourceId);
        Assert.Equal("profile", report.ReportedResourceType);
        Assert.Equal("learning", report.ModuleScope);
        Assert.Equal(ValidSubmittedAt, report.SubmittedAtUtc);
    }

    [Fact]
    public void Constructor_TrimsReportedResourceType()
    {
        var report = CreateReport(resourceType: "  profile  ");

        Assert.Equal("profile", report.ReportedResourceType);
    }

    [Fact]
    public void Constructor_ConvertsSubmissionTimeToUtc()
    {
        var localTime = new DateTimeOffset(
            2026, 11, 18, 14, 30, 0, TimeSpan.FromMinutes(330));

        var report = CreateReport(submittedAt: localTime);

        Assert.Equal(TimeSpan.Zero, report.SubmittedAtUtc.Offset);
        Assert.Equal(ValidSubmittedAt, report.SubmittedAtUtc);
    }

    [Fact]
    public void Constructor_RejectsEmptyReportId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateReport(reportId: Guid.Empty));

        Assert.Equal("reportId", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsEmptyReporterId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateReport(reporterId: Guid.Empty));

        Assert.Equal("reporterModuleProfileId", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsEmptyResourceId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateReport(resourceId: Guid.Empty));

        Assert.Equal("reportedResourceId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsMissingResourceType(
        string? resourceType)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateReport(resourceType: resourceType));

        Assert.Equal("reportedResourceType", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsMissingModule(string? module)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateReport(moduleScope: module));

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
                CreateReport(moduleScope: module));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsDefaultTimestamp()
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CreateReport(submittedAt: default(DateTimeOffset)));

        Assert.Equal("submittedAtUtc", exception.ParamName);
    }

    private static ModerationReport CreateReport(
        Guid? reportId = null,
        Guid? reporterId = null,
        Guid? resourceId = null,
        string? resourceType = "profile",
        string? moduleScope = "learning",
        DateTimeOffset? submittedAt = null)
    {
        return new ModerationReport(
            reportId ?? ValidReportId,
            reporterId ?? ValidReporterId,
            resourceId ?? ValidResourceId,
            resourceType!,
            moduleScope!,
            submittedAt ?? ValidSubmittedAt);
    }
}
