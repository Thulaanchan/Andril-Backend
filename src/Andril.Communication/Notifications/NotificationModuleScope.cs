
using System;

namespace Andril.Communication.Notifications;

/// <summary>
/// The three supported ANDRIL product modules.
/// </summary>
public enum NotificationModule
{
    Learning = 1,
    Hobbies = 2,
    Social = 3
}

/// <summary>
/// Identifies the intended private module profile
/// for a notification.
///
/// This value does not itself grant authorization.
/// The backend must verify the recipient's actual
/// module-profile ownership and eligibility.
/// </summary>
public sealed record NotificationModuleScope
{
    public NotificationModule Module { get; }

    public Guid RecipientModuleProfileId { get; }

    public NotificationModuleScope(
        NotificationModule module,
        Guid recipientModuleProfileId)
    {
        if (!Enum.IsDefined(module))
        {
            throw new ArgumentOutOfRangeException(
                nameof(module),
                "Unsupported ANDRIL module.");
        }

        if (recipientModuleProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A module profile identifier is required.",
                nameof(recipientModuleProfileId));
        }

        Module = module;
        RecipientModuleProfileId = recipientModuleProfileId;
    }
}
