using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Fail-closed reporter profile access reader.
///
/// Never authorizes a reporter profile.
/// Safe fallback until trusted account/profile
/// ownership verification is implemented.
/// </summary>
public sealed class DenyAllModerationReportReporterProfileAccessReader
    : IModerationReportReporterProfileAccessReader
{
    /// <summary>
    /// Always denies reporter profile access.
    ///
    /// No database queries, network calls, profile
    /// creation, updates or configurable bypasses.
    ///
    /// Cancellation is respected before denial.
    /// </summary>
    public Task<bool> IsAuthorizedReporterAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
