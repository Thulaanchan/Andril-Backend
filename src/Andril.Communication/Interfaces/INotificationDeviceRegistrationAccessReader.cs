
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Backend-only device registration access contract.
///
/// This interface does not send push notifications,
/// expose FCM tokens, or grant module authorization.
///
/// Actual device registration storage and ownership
/// integration must be coordinated with M2.
/// </summary>
public interface INotificationDeviceRegistrationAccessReader
{
    /// <summary>
    /// Determines whether the specified device registration
    /// can be used for an authorized notification recipient.
    ///
    /// Implementations must verify:
    ///
    /// 1. The authenticated account ID is valid.
    /// 2. The device registration exists.
    /// 3. The registration belongs to that account.
    /// 4. The registration is active and not revoked.
    /// 5. The registration is permitted for the exact
    ///    authorized module-profile context.
    /// 6. The registration has a valid server-side
    ///    delivery destination.
    ///
    /// The caller must first establish the private
    /// module-profile authorization through M2's
    /// authoritative identity system.
    ///
    /// Return false when authorization cannot be proven,
    /// the registration is missing, or the supporting
    /// infrastructure is unavailable.
    ///
    /// Never trust a client-provided account ID,
    /// module-profile ID, or device ownership claim.
    ///
    /// This contract does not specify whether device
    /// records are stored per account or per module.
    /// That persistence decision remains pending.
    /// </summary>
    Task<bool> IsAuthorizedActiveRegistrationAsync(
        Guid authenticatedAccountId,
        NotificationPushRequest request,
        CancellationToken cancellationToken = default);
}
