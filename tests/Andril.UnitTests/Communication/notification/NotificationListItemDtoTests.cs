using System;
using Andril.Communication.DTOs;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationListItemDtoTests
{
    [Fact]
    public void NewNotification_ShouldBeUnread()
    {
        var notification = new NotificationListItemDto(
            Guid.NewGuid(),
            "message",
            "New Message",
            "You have a new message",
            DateTimeOffset.UtcNow,
            null);

        Assert.False(notification.IsRead);
        Assert.Null(notification.ReadAtUtc);
    }

    [Fact]
    public void ReadNotification_ShouldBeMarkedAsRead()
    {
        var now = DateTimeOffset.UtcNow;

        var notification = new NotificationListItemDto(
            Guid.NewGuid(),
            "message",
            "New Message",
            "You have a new message",
            now.AddMinutes(-5),
            now);

        Assert.True(notification.IsRead);
        Assert.Equal(now, notification.ReadAtUtc);
    }

    [Fact]
    public void Notification_ShouldPreserveProvidedValues()
    {
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var notification = new NotificationListItemDto(
            id,
            "call",
            "Missed Call",
            "You missed a call",
            createdAt,
            null);

        Assert.Equal(id, notification.Id);
        Assert.Equal("call", notification.Type);
        Assert.Equal("Missed Call", notification.Title);
        Assert.Equal("You missed a call", notification.Preview);
        Assert.Equal(createdAt, notification.CreatedAtUtc);
    }
}
