namespace Andril.Common.Moderation;

/// <summary>
/// Represents metadata for a module-scoped moderation report.
/// This model does not grant access or perform sanctions.
/// Evidence, decisions and persistence are separate concerns.
/// </summary>
public sealed class ModerationReport
{
    public ModerationReport(
        Guid reportId,
        Guid reporterModuleProfileId,
        Guid reportedResourceId,
        string reportedResourceType,
        string moduleScope,
        DateTimeOffset submittedAtUtc)
    {
        if (reportId == Guid.Empty)
            throw new ArgumentException(
                "A report identifier is required.",
                nameof(reportId));

        if (reporterModuleProfileId == Guid.Empty)
            throw new ArgumentException(
                "A reporter module profile is required.",
                nameof(reporterModuleProfileId));

        if (reportedResourceId == Guid.Empty)
            throw new ArgumentException(
                "A reported resource identifier is required.",
                nameof(reportedResourceId));

        if (string.IsNullOrWhiteSpace(reportedResourceType))
            throw new ArgumentException(
                "A reported resource type is required.",
                nameof(reportedResourceType));

        if (string.IsNullOrWhiteSpace(moduleScope))
            throw new ArgumentException(
                "A module scope is required.",
                nameof(moduleScope));

        if (moduleScope is not
            ("learning" or "hobbies" or "social"))
            throw new ArgumentOutOfRangeException(
                nameof(moduleScope),
                "Unsupported ANDRIL module.");

        if (submittedAtUtc == default)
            throw new ArgumentOutOfRangeException(
                nameof(submittedAtUtc),
                "A valid submission timestamp is required.");

        ReportId = reportId;
        ReporterModuleProfileId = reporterModuleProfileId;
        ReportedResourceId = reportedResourceId;
        ReportedResourceType = reportedResourceType.Trim();
        ModuleScope = moduleScope;
        SubmittedAtUtc = submittedAtUtc.ToUniversalTime();
    }

    public Guid ReportId { get; }
    public Guid ReporterModuleProfileId { get; }
    public Guid ReportedResourceId { get; }
    public string ReportedResourceType { get; }
    public string ModuleScope { get; }
    public DateTimeOffset SubmittedAtUtc { get; }
}
