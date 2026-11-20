
using Andril.Communication.Interfaces;
using Andril.Communication.Notifications;
using Andril.Communication.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Andril.API.Extensions;

/// <summary>
/// Registers the safe notification API foundation.
///
/// This configuration uses deny-all dependencies.
///
/// Do not register PostgreSQL notification persistence
/// until database schema, migrations and M2 authorization
/// have been approved and verified.
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotifications(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<
            INotificationRecipientAccessReader,
            DenyAllNotificationRecipientAccessReader>();

        services.TryAddScoped<
            INotificationRepository,
            DenyAllNotificationRepository>();

        services.TryAddScoped<
            NotificationApplicationService>();

        return services;
    }
}
