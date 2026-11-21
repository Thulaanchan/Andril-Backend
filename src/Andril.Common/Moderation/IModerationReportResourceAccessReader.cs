using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Defines trusted resource-side verification for
/// moderation report submissions.
/// This interface does not grant permission by itself.
/// </summary>
public interface IModerationReportResourceAccessReader
{
    /// <summary>
    /// Returns true only when authoritative backend data
    /// confirms all required resource-side permissions.
    ///
    /// Implementations must:
    /// - Require a valid server-verified internal account ID.
    /// - Require a valid moderation report.
    /// - Verify the reported resource exists.
    /// - Verify its exact module and resource type.
    /// - Verify reporting eligibility and privacy rules.
    /// - Never trust client-supplied ownership claims.
    /// - Never create or modify resources during checks.
    /// - Deny missing, invalid or uncertain permissions.
    /// - Never grant access after infrastructure failure.
    /// - Respect cancellation.
    ///
    /// Reporter profile ownership is checked separately.
    /// </summary>
    Task<bool> CanReportAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default);
}
