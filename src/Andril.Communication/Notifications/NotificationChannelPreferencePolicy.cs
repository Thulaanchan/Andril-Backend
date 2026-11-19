
using System;

namespace Andril.Communication.Notifications;

/// <summary>
/// Supported ANDRIL notification delivery channels.
/// </summary>
public enum NotificationDeliveryChannel
{
    InApp = 1,
    Push = 2,
    Email = 3
}

/// <summary>
/// Preference snapshot for one private module profile.
///
/// Values must come from a trusted backend preference store.
/// Client-supplied preferences must not be treated as authority.
/// </summary>
public sealed class NotificationChannelPreferences
{
    public NotificationModuleScope RecipientScope { get; }

    public bool InAppEnabled { get; }

    public bool PushEnabled { get; }

    public bool EmailEnabled { get; }

    public NotificationChannelPreferences(
        NotificationModuleScope recipientScope,
        bool inAppEnabled,
        bool pushEnabled,
        bool emailEnabled)
    {
        RecipientScope = recipientScope
            ?? throw new ArgumentNullException(
                nameof(recipientScope));

        InAppEnabled = inAppEnabled;
        PushEnabled = pushEnabled;
        EmailEnabled = emailEnabled;
    }
}

/// <summary>
/// Fail-closed preference evaluation for ordinary,
/// preference-controlled module notifications.
///
/// Authorization must already have established the
/// authenticated account's owned, eligible module scope.
///
/// This policy does not send notifications.
/// </summary>
public static class NotificationChannelPreferencePolicy
{
    public static bool CanDeliver(
        NotificationModuleScope? authorizedScope,
        NotificationChannelPreferences? preferences,
        NotificationDeliveryChannel channel)
    {
        // Missing authorization or preferences:
        // do not deliver.
        if (authorizedScope is null || preferences is null)
        {
            return false;
        }

        // Never use preferences belonging to another
        // module or another private module profile.
        if (authorizedScope != preferences.RecipientScope)
        {
            return false;
        }

        return channel switch
        {
            NotificationDeliveryChannel.InApp =>
                preferences.InAppEnabled,

            NotificationDeliveryChannel.Push =>
                preferences.PushEnabled,

            NotificationDeliveryChannel.Email =>
                preferences.EmailEnabled,

            // Invalid channels fail closed.
            _ => false
        };
    }
}

