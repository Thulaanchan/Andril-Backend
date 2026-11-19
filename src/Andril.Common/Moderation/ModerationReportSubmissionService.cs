namespace Andril.Common.Moderation;

/// <summary>
/// Coordinates authorized moderation report submission.
///
/// The verified account ID must come from trusted server-side
/// authentication and persisted account resolution.
///
/// This service does not implement authentication, evidence
/// access, resource ownership or database persistence.
/// </summary>
public sealed class ModerationReportSubmissionService
{
    private readonly IModerationReportAccessVerifier _accessVerifier;
    private readonly IModerationReportRepository _repository;

    public ModerationReportSubmissionService(
        IModerationReportAccessVerifier accessVerifier,
        IModerationReportRepository repository)
    {
        ArgumentNullException.ThrowIfNull(accessVerifier);
        ArgumentNullException.ThrowIfNull(repository);

        _accessVerifier = accessVerifier;
        _repository = repository;
    }

    /// <summary>
    /// Attempts to submit a moderation report.
    ///
    /// Returns false when authorization is denied.
    /// Returns true only after repository persistence completes.
    ///
    /// Authorization or persistence exceptions are propagated.
    /// No failure is treated as successful submission.
    /// </summary>
    public async Task<bool> SubmitAsync(
        Guid verifiedAccountId,
        ModerationReport? report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (verifiedAccountId == Guid.Empty || report is null)
        {
            return false;
        }

        var authorized = await _accessVerifier.CanSubmitAsync(
            verifiedAccountId,
            report,
            cancellationToken);

        if (!authorized)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _repository.AddAsync(
            report,
            cancellationToken);

        return true;
    }
}
