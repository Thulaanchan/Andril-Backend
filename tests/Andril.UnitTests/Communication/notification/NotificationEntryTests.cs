
using System;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationEntryTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static NotificationEntry CreateEntry(
        DateTimeOffset? createdAt = null)
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Social,
            Guid.NewGuid());

        return new NotificationEntry(
            Guid.NewGuid(),
            scope,
            "message",
            "New Message",
            "You have received a message.",
            createdAt ?? CreatedAt);
    }

    [Fact]
    public void Constructor_ShouldCreateUnreadNotification()
    {
        var entry = CreateEntry();

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("message", entry.Type);
        Assert.Equal("New Message", entry.Title);
        Assert.Equal(
            "You have received a message.",
            entry.Preview);

        Assert.Equal(
            NotificationModule.Social,
            entry.RecipientScope.Module);

        Assert.Equal(CreatedAt, entry.CreatedAtUtc);
        Assert.Null(entry.ReadAtUtc);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public void Constructor_ShouldRejectEmptyId()
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            new NotificationEntry(
                Guid.Empty,
                scope,
                "message",
                "New Message",
                null,
                CreatedAt));
    }

    [Fact]
    public void Constructor_ShouldRejectDefaultCreationTimestamp()
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Learning,
            Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NotificationEntry(
                Guid.NewGuid(),
                scope,
                "message",
                "New Message",
                null,
                default));
    }

    [Fact]
    public void Constructor_ShouldRejectNullScope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotificationEntry(
                Guid.NewGuid(),
                null!,
                "message",
                "New Message",
                null,
                CreatedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldRejectInvalidType(
        string? type)
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Hobbies,
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            new NotificationEntry(
                Guid.NewGuid(),
                scope,
                type!,
                "New Message",
                null,
                CreatedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldRejectInvalidTitle(
        string? title)
    {
        var scope = new NotificationModuleScope(
            NotificationModule.Social,
            Guid.NewGuid());

        Assert.Throws<ArgumentException>(() =>
            new NotificationEntry(
                Guid.NewGuid(),
                scope,
                "message",
                title!,
                null,
                CreatedAt));
    }

    [Fact]
    public void Constructor_ShouldConvertCreatedAtToUtc()
    {
        var localTime = new DateTimeOffset(
            2026, 11, 16, 14, 30, 0,
            TimeSpan.FromMinutes(330));

        var entry = CreateEntry(localTime);

        Assert.Equal(
            CreatedAt,
            entry.CreatedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            entry.CreatedAtUtc.Offset);
    }

    [Fact]
    public void MarkAsRead_ShouldSetReadTimestamp()
    {
        var entry = CreateEntry();
        var readAt = CreatedAt.AddMinutes(10);

        entry.MarkAsRead(readAt);

        Assert.True(entry.IsRead);
        Assert.Equal(readAt, entry.ReadAtUtc);
    }

    [Fact]
    public void MarkAsRead_ShouldConvertTimestampToUtc()
    {
        var entry = CreateEntry();

        var localReadTime = new DateTimeOffset(
            2026, 11, 16, 14, 40, 0,
            TimeSpan.FromMinutes(330));

        entry.MarkAsRead(localReadTime);

        Assert.Equal(
            CreatedAt.AddMinutes(10),
            entry.ReadAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            entry.ReadAtUtc!.Value.Offset);
    }

    [Fact]
    public void MarkAsRead_ShouldRejectEarlierTimestamp()
    {
        var entry = CreateEntry();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            entry.MarkAsRead(
                CreatedAt.AddMinutes(-1)));

        Assert.False(entry.IsRead);
        Assert.Null(entry.ReadAtUtc);
    }

    [Fact]
    public void MarkAsRead_ShouldBeIdempotent()
    {
        var entry = CreateEntry();

        var firstReadAt = CreatedAt.AddMinutes(5);
        var secondReadAt = CreatedAt.AddMinutes(15);

        entry.MarkAsRead(firstReadAt);
        entry.MarkAsRead(secondReadAt);

        Assert.True(entry.IsRead);
        Assert.Equal(firstReadAt, entry.ReadAtUtc);
    }
}
