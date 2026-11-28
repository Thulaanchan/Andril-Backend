using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Privacy;

public sealed record UserPrivacySettingsDto(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("profileDiscoverability")] string ProfileDiscoverability,
    [property: JsonPropertyName("ghostModeEnabled")] bool GhostModeEnabled,
    [property: JsonPropertyName("directMessagingPolicy")] string DirectMessagingPolicy,
    [property: JsonPropertyName("allowFriendRequests")] bool AllowFriendRequests,
    [property: JsonPropertyName("showActiveModule")] bool ShowActiveModule,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc
);

public sealed record UpdatePrivacySettingsRequest(
    [property: JsonPropertyName("profileDiscoverability")] string ProfileDiscoverability,
    [property: JsonPropertyName("ghostModeEnabled")] bool GhostModeEnabled,
    [property: JsonPropertyName("directMessagingPolicy")] string DirectMessagingPolicy,
    [property: JsonPropertyName("allowFriendRequests")] bool AllowFriendRequests,
    [property: JsonPropertyName("showActiveModule")] bool ShowActiveModule
);
