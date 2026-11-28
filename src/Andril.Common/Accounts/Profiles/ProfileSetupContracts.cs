using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed record ProfileSetupSubmissionRequest(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("locationCity")] string? LocationCity,
    [property: JsonPropertyName("locationRegion")] string? LocationRegion
);

public sealed record PreferencesHandoffDto(
    [property: JsonPropertyName("targetModule")] string TargetModule,
    [property: JsonPropertyName("ownerMember")] string OwnerMember,
    [property: JsonPropertyName("handoffRoute")] string HandoffRoute,
    [property: JsonPropertyName("isProfileComplete")] bool IsProfileComplete,
    [property: JsonPropertyName("isPreferencesComplete")] bool IsPreferencesComplete,
    [property: JsonPropertyName("note")] string Note
);

public sealed record ProfileSetupSubmissionResponse(
    [property: JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("bio")] string? Bio,
    [property: JsonPropertyName("locationCity")] string? LocationCity,
    [property: JsonPropertyName("locationRegion")] string? LocationRegion,
    [property: JsonPropertyName("hasMainPhoto")] bool HasMainPhoto,
    [property: JsonPropertyName("additionalPhotoCount")] int AdditionalPhotoCount,
    [property: JsonPropertyName("setupStage")] string SetupStage,
    [property: JsonPropertyName("nextRoute")] string NextRoute,
    [property: JsonPropertyName("handoff")] PreferencesHandoffDto Handoff
);

public sealed record ProfileSetupStatusDto(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("hasProfile")] bool HasProfile,
    [property: JsonPropertyName("profileId")] Guid? ProfileId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("hasMainPhoto")] bool HasMainPhoto,
    [property: JsonPropertyName("mainPhotoRequired")] bool MainPhotoRequired,
    [property: JsonPropertyName("additionalPhotoCount")] int AdditionalPhotoCount,
    [property: JsonPropertyName("hasLocation")] bool HasLocation,
    [property: JsonPropertyName("locationCity")] string? LocationCity,
    [property: JsonPropertyName("setupStage")] string SetupStage,
    [property: JsonPropertyName("isReadyForPreferences")] bool IsReadyForPreferences,
    [property: JsonPropertyName("handoffRoute")] string HandoffRoute
);
