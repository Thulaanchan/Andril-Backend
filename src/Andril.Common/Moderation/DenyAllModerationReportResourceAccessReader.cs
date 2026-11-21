using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Fail-closed resource access reader.
///
/// This implementation never grants permission
/// to submit a moderation report.
///
/// It is the safe fallback until authoritative
/// resource access verification is implemented.
/// </summary>
public sealed class DenyAllModerationReportResourceAccessReader
    : IModerationReportResourceAccessReader
{
    /// <summary>
    /// Always denies resource reporting access.
    ///
    /// Cancellation is checked before returning.
    /// No database access, network requests,
    /// resource creation or permission bypasses.
    /// </summary>
    public Task<bool> CanReportAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
