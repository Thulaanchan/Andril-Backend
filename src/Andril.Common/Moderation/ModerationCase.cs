namespace Andril.Common.Moderation;

/// <summary>
/// Identifies a moderation case originating from one
/// ANDRIL product module.
///
/// This model does not authorize evidence access,
/// perform moderation decisions, or impose sanctions.
/// Workflow states and persistence require approval.
/// </summary>
public sealed class ModerationCase
{
    public ModerationCase(
        Guid caseId,
        Guid sourceReportId,
        string moduleScope,
        DateTimeOffset openedAtUtc)
    {
        if (caseId == Guid.Empty)
            throw new ArgumentException(
                "A case identifier is required.",
                nameof(caseId));

        if (sourceReportId == Guid.Empty)
            throw new ArgumentException(
                "A source report identifier is required.",
                nameof(sourceReportId));

        if (string.IsNullOrWhiteSpace(moduleScope))
            throw new ArgumentException(
                "A module scope is required.",
                nameof(moduleScope));

        if (moduleScope is not
            ("learning" or "hobbies" or "social"))
            throw new ArgumentOutOfRangeException(
                nameof(moduleScope),
                "Unsupported ANDRIL module.");

        if (openedAtUtc == default)
            throw new ArgumentOutOfRangeException(
                nameof(openedAtUtc),
                "A valid opening timestamp is required.");

        CaseId = caseId;
        SourceReportId = sourceReportId;
        ModuleScope = moduleScope;
        OpenedAtUtc = openedAtUtc.ToUniversalTime();
    }

    public Guid CaseId { get; }

    public Guid SourceReportId { get; }

    public string ModuleScope { get; }

    public DateTimeOffset OpenedAtUtc { get; }
}
