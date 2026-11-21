using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Defines trusted reporter profile verification
/// for moderation report submissions.
///
/// This interface does not grant permission by itself.
/// </summary>
public interface IModerationReportReporterProfileAccessReader
{
    /// <summary>
    /// Returns true only after authoritative backend
    /// verification of the reporter profile.
    ///
    /// Implementations must:
    ///
    /// 1. Require a server-verified internal Account ID.
    /// 2. Verify that the reporter profile exists.
    /// 3. Verify that the profile belongs to the account.
    /// 4. Match the exact ReporterModuleProfileId.
    /// 5. Match the exact report module scope.
    /// 6. Verify active membership and eligibility.
    /// 7. Enforce applicable module privacy rules.
    /// 8. Never trust client-supplied ownership claims.
    /// 9. Never create or modify profiles during checks.
    /// 10. Deny missing or uncertain permissions.
    /// 11. Never grant access after infrastructure failure.
    /// 12. Respect cancellation.
    ///
    /// Resource-side authorization must be checked
    /// separately before any report is persisted.
    /// </summary>
    Task<bool> IsAuthorizedReporterAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default);
}
