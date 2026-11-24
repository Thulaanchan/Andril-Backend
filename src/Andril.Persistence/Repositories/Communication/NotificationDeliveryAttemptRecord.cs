using System;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// D14-B PROPOSED, DORMANT delivery-attempt persistence candidate.
/// Not mapped, migrated, registered, or activated in production.
/// Each instance represents one dispatch attempt, not an entire notification.
/// This model does not authorize delivery or enforce database uniqueness.
/// </summary>
internal enum NotificationDeliveryAttemptOutcome
{
    Claimed = 0,
    ProviderAccepted = 1,
    TemporaryFailure = 2,
    PermanentFailure = 3,
    Unknown = 4
}

/// <summary>
/// Minimal privacy-safe record for a single push delivery attempt.
/// No provider tokens, message previews, profile names, provider credentials,
/// error descriptions, or payload contents are stored here.
/// </summary>
internal sealed class NotificationDeliveryAttemptRecord
{
    // Reserved for a future reviewed EF Core mapping.
    private NotificationDeliveryAttemptRecord() { }

    public Guid Id { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid RecipientAccountId { get; private set; }
    public Guid RecipientModuleProfileId { get; private set; }
    public string Module { get; private set; } = string.Empty;
    public Guid DeviceRegistrationId { get; private set; }
    public NotificationDeliveryAttemptOutcome Outcome { get; private set; }
    public DateTimeOffset ClaimedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>
    /// The caller MUST have independently resolved trusted event identity,
    /// authoritative account/profile scope, current preferences, device
    /// eligibility, and restrictions. This factory grants no authorization.
    /// </summary>
    internal static NotificationDeliveryAttemptRecord CreateClaimed(
        Guid attemptId,
        Guid notificationId,
        Guid recipientAccountId,
        Guid recipientModuleProfileId,
        string module,
        Guid deviceRegistrationId,
        DateTimeOffset claimedAtUtc)
    {
        if (attemptId == Guid.Empty)
            throw new ArgumentException("Attempt ID is required.", nameof(attemptId));
        if (notificationId == Guid.Empty)
            throw new ArgumentException("Notification ID is required.", nameof(notificationId));
        if (recipientAccountId == Guid.Empty)
            throw new ArgumentException("Recipient account ID is required.", nameof(recipientAccountId));
        if (recipientModuleProfileId == Guid.Empty)
            throw new ArgumentException("Recipient module profile ID is required.", nameof(recipientModuleProfileId));
        if (deviceRegistrationId == Guid.Empty)
            throw new ArgumentException("Device registration ID is required.", nameof(deviceRegistrationId));

        if (!string.Equals(module, "learning", StringComparison.Ordinal) &&
            !string.Equals(module, "hobbies", StringComparison.Ordinal) &&
            !string.Equals(module, "social", StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported module.", nameof(module));
        }

        if (claimedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(claimedAtUtc));

        return new NotificationDeliveryAttemptRecord
        {
            Id = attemptId,
            NotificationId = notificationId,
            RecipientAccountId = recipientAccountId,
            RecipientModuleProfileId = recipientModuleProfileId,
            Module = module,
            DeviceRegistrationId = deviceRegistrationId,
            Outcome = NotificationDeliveryAttemptOutcome.Claimed,
            ClaimedAtUtc = claimedAtUtc.ToUniversalTime()
        };
    }

    /// <summary>
    /// Records one outcome for one attempt. Unknown is an unresolved outcome,
    /// not permission to resend. A new attempt requires a separately reviewed
    /// dispatch-claim and retry policy, which is NOT implemented here.
    /// </summary>
    internal bool TryRecordOutcome(
        NotificationDeliveryAttemptOutcome outcome,
        DateTimeOffset completedAtUtc)
    {
        if (!Enum.IsDefined(outcome) ||
            outcome == NotificationDeliveryAttemptOutcome.Claimed)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (completedAtUtc == default)
            throw new ArgumentOutOfRangeException(nameof(completedAtUtc));

        var utc = completedAtUtc.ToUniversalTime();
        if (utc < ClaimedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(completedAtUtc));

        if (Outcome != NotificationDeliveryAttemptOutcome.Claimed)
            return false;

        Outcome = outcome;
        CompletedAtUtc = utc;
        return true;
    }
}
