namespace Andril.Common.Moderation;

/// <summary>
/// Persistence contract for module-scoped moderation reports.
///
/// This interface does not authorize callers.
/// Authentication, reporter identity verification, target
/// authorization and module isolation must be enforced
/// by the application service and persistence layer.
/// </summary>
public interface IModerationReportRepository
{
    /// <summary>
    /// Finds a report within the requested module scope.
    /// Implementations must enforce the module filter.
    /// Missing or inaccessible reports must not expose
    /// information from other modules.
    /// </summary>
    Task<ModerationReport?> FindByIdAsync(
        Guid reportId,
        string moduleScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a validated moderation report.
    /// The caller must already be authorized to submit it.
    /// Implementations must handle duplicate identifiers
    /// safely and must not persist partial writes.
    /// </summary>
    Task AddAsync(
        ModerationReport report,
        CancellationToken cancellationToken = default);
}
