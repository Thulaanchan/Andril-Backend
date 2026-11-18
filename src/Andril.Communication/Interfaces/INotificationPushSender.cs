
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Result of an attempted push notification dispatch.
///
/// ProviderAccepted does not guarantee device delivery.
/// </summary>
public enum NotificationPushOutcome
{
    NotSent = 0,
    ProviderAccepted = 1,
    TemporaryFailure = 2,
    PermanentFailure = 3
}

/// <summary>
/// Represents the result of a push dispatch attempt.
///
/// Do not include device tokens, private profile names,
/// provider credentials, or sensitive error details.
/// </summary>
public sealed record NotificationPushResult(
    NotificationPushOutcome Outcome)
{
    public bool WasAccepted =>
        Outcome == NotificationPushOutcome.ProviderAccepted;
}

/// <summary>
/// Minimal server-side request for push delivery.
///
/// No private display name, module identity details,
/// message preview, or sensitive conversation data
/// is included in the request.
///
/// All identifiers must originate from trusted backend
/// validation, not unverified client input.
/// </summary>
public sealed class NotificationPushRequest
{
    public Guid NotificationId { get; }

    public NotificationModuleScope RecipientScope { get; }

    public Guid DeviceRegistrationId { get; }

    public NotificationPushRequest(
        Guid notificationId,
        NotificationModuleScope recipientScope,
        Guid deviceRegistrationId)
    {
        if (notificationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Notification identifier is required.",
                nameof(notificationId));
        }

        RecipientScope = recipientScope
            ?? throw new ArgumentNullException(
                nameof(recipientScope));

        if (deviceRegistrationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Device registration identifier is required.",
                nameof(deviceRegistrationId));
        }

        NotificationId = notificationId;
        DeviceRegistrationId = deviceRegistrationId;
    }
}

/// <summary>
/// Backend push notification delivery contract.
///
/// A future implementation may use Firebase Cloud
/// Messaging after provider configuration approval.
///
/// Before calling SendAsync, the orchestration service
/// must verify:
///
/// 1. Authenticated account and active module profile.
/// 2. Notification belongs to that module profile.
/// 3. Recipient notification preferences.
/// 4. Device registration belongs to the recipient.
/// 5. Applicable block and privacy restrictions.
/// 6. Notification payload contains no hidden identity.
///
/// This interface does not grant authorization
/// and does not send notifications by itself.
/// </summary>
public interface INotificationPushSender
{
    Task<NotificationPushResult> SendAsync(
        NotificationPushRequest request,
        CancellationToken cancellationToken = default);
}
