using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed record DisplayNameAvailabilityResponse(
    [property: JsonPropertyName("isAvailable")] bool IsAvailable,
    [property: JsonPropertyName("normalizedName")] string? NormalizedName,
    [property: JsonPropertyName("reason")] string? Reason
);
