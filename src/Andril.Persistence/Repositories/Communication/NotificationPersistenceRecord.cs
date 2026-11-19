
using System;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Proposed internal persistence model for a private,
/// module-scoped ANDRIL notification.
///
/// This type is not an authorization mechanism.
/// It is not registered with a DbContext yet.
///
/// Actual module values must come from M2's verified
/// module-profile contract.
/// </summary>
internal sealed class NotificationPersistenceRecord
{
    // Required by EF Core materialization.
    private NotificationPersistenceRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid RecipientModuleProfileId { get; private set; }

    public string Module { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string? Preview { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public bool IsRead => ReadAtUtc.HasValue;

    /// <summary>
    /// Creates a storage record from previously validated,
    /// authorized notification data.
    ///
    /// The caller must independently verify module-profile
    /// ownership and eligibility before persistence.
    /// </summary>
    internal static NotificationPersistenceRecord Create(
        Guid id,
        Guid recipientModuleProfileId,
        string module,
        string type,
        string title,
        string? preview,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Notification ID is required.",
                nameof(id));
        }

        if (recipientModuleProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "Recipient module profile ID is required.",
                nameof(recipientModuleProfileId));
        }

        if (string.IsNullOrWhiteSpace(module))
        {
            throw new ArgumentException(
                "Module is required.",
                nameof(module));
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException(
                "Notification type is required.",
                nameof(type));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                "Notification title is required.",
                nameof(title));
        }

        if (createdAtUtc == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAtUtc));
        }

        return new NotificationPersistenceRecord
        {
            Id = id,
            RecipientModuleProfileId = recipientModuleProfileId,
            Module = module,
            Type = type,
            Title = title,
            Preview = preview,
            CreatedAtUtc = createdAtUtc.ToUniversalTime()
        };
    }

    /// <summary>
    /// Marks a notification as read once.
    /// Repeated requests preserve the first read timestamp.
    /// </summary>
    internal void MarkAsRead(DateTimeOffset readAtUtc)
    {
        if (IsRead)
        {
            return;
        }

        var utc = readAtUtc.ToUniversalTime();

        if (utc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAtUtc));
        }

        ReadAtUtc = utc;
    }
}
