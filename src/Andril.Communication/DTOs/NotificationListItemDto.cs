
using System;

namespace Andril.Communication.DTOs;

/// <summary>
/// Represents one notification displayed in an authorized
/// module's notification center.
/// </summary>
public sealed record NotificationListItemDto(
    Guid Id,
    string Type,
    string Title,
    string? Preview,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc)
{
    public bool IsRead => ReadAtUtc.HasValue;
}
