
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;

namespace Andril.Communication.Services;

/// <summary>
/// Coordinates module-scoped push delivery.
/// Requires trusted server-side account identity.
/// </summary>
public sealed class NotificationPushOrchestrationService
{
    private readonly INotificationRecipientAccessReader _access;
    private readonly INotificationRepository _repository;
    private readonly INotificationChannelPreferenceReader _preferences;
    private readonly INotificationDeviceRegistrationAccessReader _devices;
    private readonly INotificationPushSender _sender;

    public NotificationPushOrchestrationService(
        INotificationRecipientAccessReader access,
        INotificationRepository repository,
        INotificationChannelPreferenceReader preferences,
        INotificationDeviceRegistrationAccessReader devices,
        INotificationPushSender sender)
    {
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public async Task<NotificationPushResult> TrySendAsync(
        Guid authenticatedAccountId,
        NotificationModule module,
        Guid notificationId,
        Guid deviceRegistrationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (authenticatedAccountId == Guid.Empty ||
            notificationId == Guid.Empty ||
            deviceRegistrationId == Guid.Empty ||
            !Enum.IsDefined(module))
        {
            return Denied();
        }

        var scope = await _access.ResolveOwnedActiveScopeAsync(
            authenticatedAccountId,
            module,
            cancellationToken);

        if (scope is null ||
            scope.Module != module ||
            scope.RecipientModuleProfileId == Guid.Empty)
        {
            return Denied();
        }

        var notification = await _repository.FindAsync(
            scope,
            notificationId,
            cancellationToken);

        if (notification is null ||
            notification.Id != notificationId ||
            notification.RecipientScope != scope)
        {
            return Denied();
        }

        var preferences = await _preferences.GetForScopeAsync(
            scope,
            cancellationToken);

        if (!NotificationChannelPreferencePolicy.CanDeliver(
                scope,
                preferences,
                NotificationDeliveryChannel.Push))
        {
            return Denied();
        }

        var request = new NotificationPushRequest(
            notificationId,
            scope,
            deviceRegistrationId);

        var authorized =
            await _devices.IsAuthorizedActiveRegistrationAsync(
                authenticatedAccountId,
                request,
                cancellationToken);

        if (!authorized)
        {
            return Denied();
        }

        var result = await _sender.SendAsync(
            request,
            cancellationToken);

        return result ?? Denied();
    }

    private static NotificationPushResult Denied() =>
        new(NotificationPushOutcome.NotSent);
}
