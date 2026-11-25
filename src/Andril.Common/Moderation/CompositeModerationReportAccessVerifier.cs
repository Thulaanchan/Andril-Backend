using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Combines reporter-profile and resource authorization.
/// Both checks must succeed before submission is authorized.
/// Not approved for runtime activation yet.
/// </summary>
public sealed class CompositeModerationReportAccessVerifier
    : IModerationReportAccessVerifier
{
    private readonly IModerationReportReporterProfileAccessReader _reporterReader;
    private readonly IModerationReportResourceAccessReader _resourceReader;

    public CompositeModerationReportAccessVerifier(
        IModerationReportReporterProfileAccessReader reporterReader,
        IModerationReportResourceAccessReader resourceReader)
    {
        _reporterReader = reporterReader
            ?? throw new ArgumentNullException(nameof(reporterReader));

        _resourceReader = resourceReader
            ?? throw new ArgumentNullException(nameof(resourceReader));
    }

    public async Task<bool> CanSubmitAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (verifiedAccountId == Guid.Empty || report is null)
            return false;

        var reporterAuthorized =
            await _reporterReader.IsAuthorizedReporterAsync(
                verifiedAccountId,
                report,
                cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (!reporterAuthorized)
            return false;

        var resourceAuthorized =
            await _resourceReader.CanReportAsync(
                verifiedAccountId,
                report,
                cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return resourceAuthorized;
    }
}
