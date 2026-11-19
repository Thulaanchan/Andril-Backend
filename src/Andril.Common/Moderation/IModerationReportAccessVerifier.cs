using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Common.Moderation;

/// <summary>
/// Defines trusted authorization checks for moderation
/// report submission within one ANDRIL module.
///
/// Supported modules: Learning, Hobbies and Social.
/// This interface does not grant access by itself.
/// </summary>
public interface IModerationReportAccessVerifier
{
    /// <summary>
    /// Determines whether a verified account may submit
    /// the specified moderation report.
    ///
    /// Implementations must verify:
    /// 1. The account identity is trusted and active.
    /// 2. The reporter module profile belongs to the account.
    /// 3. The reporter profile belongs to the report module.
    /// 4. The target resource belongs to that module.
    /// 5. The target resource is eligible for reporting.
    /// 6. Applicable privacy and security rules are enforced.
    ///
    /// Missing, invalid or uncertain permissions must deny.
    /// Infrastructure failures must never grant access.
    /// The verified account ID must come from server-side
    /// authentication, not a client-supplied identifier.
    /// </summary>
    Task<bool> CanSubmitAsync(
        Guid verifiedAccountId,
        ModerationReport report,
        CancellationToken cancellationToken = default);
}
