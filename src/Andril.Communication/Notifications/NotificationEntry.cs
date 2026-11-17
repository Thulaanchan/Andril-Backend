
using System;

namespace Andril.Communication.Notifications;

public sealed class NotificationEntry
{
    public Guid Id { get; }

    public NotificationModuleScope RecipientScope { get; }

    public string Type { get; }

    public string Title { get; }

    public string? Preview { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public bool IsRead => ReadAtUtc.HasValue;

    public NotificationEntry(
        Guid id,
        NotificationModuleScope recipientScope,
        string type,
        string title,
        string? preview,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Notification ID is required.",
                nameof(id));
        }

        RecipientScope = recipientScope
            ?? throw new ArgumentNullException(
                nameof(recipientScope));

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

        Id = id;
        Type = type;
        Title = title;
        Preview = preview;
        CreatedAtUtc = createdAt.ToUniversalTime();
    }

    public void MarkAsRead(DateTimeOffset readAt)
    {
        if (IsRead)
            return;

        var utc = readAt.ToUniversalTime();

        if (utc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readAt));
        }

        ReadAtUtc = utc;
    }
}
