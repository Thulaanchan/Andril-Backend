
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Resolves an authenticated account to its authorized,
/// active private module profile for notification access.
///
/// The account ID must come from trusted server-side
/// authentication, never directly from a client request.
///
/// This contract does not grant access by itself.
/// </summary>
public interface INotificationRecipientAccessReader
{
    /// <summary>
    /// Returns the authoritative notification scope only when:
    ///
    /// 1. The account is authenticated and valid.
    /// 2. The requested module is supported.
    /// 3. The account owns the private module profile.
    /// 4. The module profile is active and eligible.
    /// 5. Any applicable module restrictions are satisfied.
    ///
    /// Social additionally requires 18+ eligibility
    /// and explicit opt-in.
    ///
    /// Returns null when authorization cannot be established,
    /// including when the identity infrastructure is unavailable.
    ///
    /// Implementations must never return an invented or
    /// client-supplied module profile identity.
    /// </summary>
    Task<NotificationModuleScope?> ResolveOwnedActiveScopeAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        CancellationToken cancellationToken = default);
}
