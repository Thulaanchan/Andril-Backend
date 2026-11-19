using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Denies moderation report submission until a trusted
/// access verifier has been implemented and registered.
///
/// This implementation never grants access.
/// Supported modules are Learning, Hobbies and Social.
/// </summary>
public sealed class DenyAllModerationReportAccessVerifier
    : IModerationReportAccessVerifier
{
    /// <summary>
    /// Always denies report submission.
    ///
    /// No account, profile or resource is trusted here.
    /// Cancellation is respected before returning denial.
    /// </summary>
    public Task<bool> CanSubmitAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(false);
    }
}
