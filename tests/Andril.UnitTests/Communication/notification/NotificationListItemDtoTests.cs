using System;
using System.Linq;
using System.Text.Json;
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

    [Fact]
    public void UnreadNotification_JsonShouldPreserveExactContractAndNulls()
    {
        var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var createdAt = new DateTimeOffset(
            2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var notification = new NotificationListItemDto(
            id, "message", "Synthetic notification", null, createdAt, null);

        var serialized = JsonSerializer.Serialize(
            notification, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(serialized);
        var json = document.RootElement;

        AssertExactJsonProperties(json);
        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal(id, json.GetProperty("id").GetGuid());
        AssertJsonString(json, "type", "message");
        AssertJsonString(json, "title", "Synthetic notification");
        AssertJsonTimestamp(json, "createdAtUtc", createdAt);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("preview").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("readAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.False, json.GetProperty("isRead").ValueKind);
        Assert.False(json.GetProperty("isRead").GetBoolean());
    }

    [Fact]
    public void ReadNotification_JsonShouldPreserveExactContractAndValues()
    {
        var id = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var createdAt = new DateTimeOffset(
            2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
        var readAt = createdAt.AddMinutes(5);
        var notification = new NotificationListItemDto(
            id, "call", "Synthetic missed call", "Synthetic preview",
            createdAt, readAt);

        var serialized = JsonSerializer.Serialize(
            notification, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(serialized);
        var json = document.RootElement;

        AssertExactJsonProperties(json);
        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal(id, json.GetProperty("id").GetGuid());
        AssertJsonString(json, "type", "call");
        AssertJsonString(json, "title", "Synthetic missed call");
        AssertJsonString(json, "preview", "Synthetic preview");
        AssertJsonTimestamp(json, "createdAtUtc", createdAt);
        AssertJsonTimestamp(json, "readAtUtc", readAt);
        Assert.Equal(JsonValueKind.True, json.GetProperty("isRead").ValueKind);
        Assert.True(json.GetProperty("isRead").GetBoolean());
    }

    private static void AssertExactJsonProperties(JsonElement json)
    {
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        var names = json.EnumerateObject().Select(property => property.Name).ToArray();
        var expected = new[]
        {
            "id", "type", "title", "preview", "createdAtUtc", "readAtUtc", "isRead"
        };

        Assert.Equal(7, names.Length);
        Assert.Equal(7, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            names.OrderBy(name => name, StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static void AssertJsonString(
        JsonElement json, string propertyName, string expected)
    {
        var value = json.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(expected, value.GetString());
    }

    private static void AssertJsonTimestamp(
        JsonElement json, string propertyName, DateTimeOffset expected)
    {
        var value = json.GetProperty(propertyName);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(expected, value.GetDateTimeOffset());
    }
}
