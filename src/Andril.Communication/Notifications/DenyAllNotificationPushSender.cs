
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;

namespace Andril.Communication.Notifications;

/// <summary>
/// Fail-closed push notification sender.
///
/// This sender never contacts Firebase or any
/// external push notification provider.
///
/// It must remain the safe fallback until
/// authorized production delivery is configured.
///
/// No device tokens, private identities or
/// notification details are transmitted.
/// </summary>
public sealed class DenyAllNotificationPushSender
    : INotificationPushSender
{
    /// <summary>
    /// Always returns NotSent for a valid request.
    ///
    /// Respects cancellation and rejects null requests.
    /// Does not perform any network or database operation.
    /// </summary>
    public Task<NotificationPushResult> SendAsync(
        NotificationPushRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(
            new NotificationPushResult(
                NotificationPushOutcome.NotSent));
    }
}
