
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Notifications;

/// <summary>
/// Fail-closed notification device token lifecycle.
///
/// No device token registration, rotation, or revocation
/// is performed until authoritative backend ownership
/// and secure persistence integrations are available.
///
/// This implementation never contacts Firebase,
/// accesses a database, or stores provider tokens.
///
/// It must not be replaced with an allow-all mock
/// in production.
/// </summary>
public sealed class DenyAllNotificationDeviceTokenLifecycle
    : INotificationDeviceTokenLifecycle
{
    /// <summary>
    /// Always rejects token registration or rotation.
    ///
    /// Returns false without reading, storing,
    /// transmitting, or logging the provider token.
    ///
    /// The cancellation token is checked first.
    /// </summary>
    public Task<bool> RegisterOrRotateAsync(
        Guid authenticatedAccountId,
        NotificationModuleScope authorizedScope,
        Guid deviceRegistrationId,
        string providerToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // M2 ownership and secure token storage are not
        // connected. Registration cannot be authorized.
        return Task.FromResult(false);
    }

    /// <summary>
    /// Always reports that revocation was not completed.
    ///
    /// This fallback does not modify registration state.
    /// A caller must not treat false as successful
    /// revocation.
    ///
    /// The cancellation token is checked first.
    /// </summary>
    public Task<bool> RevokeAsync(
        Guid authenticatedAccountId,
        NotificationModuleScope authorizedScope,
        Guid deviceRegistrationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No authoritative registration storage exists
        // in this fallback implementation.
        return Task.FromResult(false);
    }
}
