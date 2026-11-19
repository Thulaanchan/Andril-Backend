
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Notifications;

/// <summary>
/// Fail-closed device registration access reader.
///
/// Until M2 provides authoritative device ownership
/// and module-profile authorization, no registration
/// is eligible for push delivery.
///
/// This class performs no database or network access.
/// It does not expose or generate FCM device tokens.
///
/// Do not replace this fallback with an allow-all mock
/// in production.
/// </summary>
public sealed class DenyAllNotificationDeviceRegistrationAccessReader
    : INotificationDeviceRegistrationAccessReader
{
    /// <summary>
    /// Always denies device registration access.
    ///
    /// Returns false for all accounts, module profiles,
    /// device registrations, and missing requests.
    ///
    /// Cancellation is checked before returning.
    /// </summary>
    public Task<bool> IsAuthorizedActiveRegistrationAsync(
        Guid authenticatedAccountId,
        NotificationPushRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No authoritative M2 device ownership resolver
        // is connected. Therefore access cannot be proven.
        return Task.FromResult(false);
    }
}
