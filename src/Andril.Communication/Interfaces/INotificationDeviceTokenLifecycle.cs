
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Server-side lifecycle contract for push device tokens.
///
/// This contract does not communicate with Firebase,
/// create database tables, or authorize a module profile.
///
/// Actual storage and device ownership integration
/// require coordination with M2.
/// </summary>
public interface INotificationDeviceTokenLifecycle
{
    /// <summary>
    /// Registers a new device token or rotates an
    /// existing token after verification.
    ///
    /// authenticatedAccountId must come from the
    /// verified server authentication context.
    ///
    /// authorizedScope must be resolved using the
    /// authoritative module-profile access rules.
    ///
    /// deviceRegistrationId must be generated or
    /// validated by the backend.
    ///
    /// providerToken is sensitive input received from
    /// the client. Its value must never be written to
    /// ordinary logs, errors, or API responses.
    ///
    /// Implementations must verify ownership, revoked
    /// state, token validity, and uniqueness before
    /// changing a registration.
    ///
    /// Return true only after a verified, durable
    /// registration or rotation succeeds.
    ///
    /// Return false when authorization or registration
    /// eligibility cannot be established.
    /// </summary>
    Task<bool> RegisterOrRotateAsync(
        Guid authenticatedAccountId,
        NotificationModuleScope authorizedScope,
        Guid deviceRegistrationId,
        string providerToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a device registration in an authorized
    /// module-profile context.
    ///
    /// An implementation must verify the registration
    /// belongs to the authenticated account and cannot
    /// expose registrations belonging to other users.
    ///
    /// A successful revocation must prevent future
    /// push delivery through the revoked registration.
    ///
    /// Logout-wide and account-wide revocation policies
    /// require separate M2 contract approval.
    ///
    /// Return true only after the authorized revocation
    /// is durably completed.
    ///
    /// Return false when the registration cannot be
    /// safely revoked.
    /// </summary>
    Task<bool> RevokeAsync(
        Guid authenticatedAccountId,
        NotificationModuleScope authorizedScope,
        Guid deviceRegistrationId,
        CancellationToken cancellationToken = default);
}
