using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed record ModuleWelcomeStatusDto(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("isEligible")] bool IsEligible,
    [property: JsonPropertyName("ineligibilityReason")] string? IneligibilityReason,
    [property: JsonPropertyName("hasExistingProfile")] bool HasExistingProfile,
    [property: JsonPropertyName("profileSetupStage")] string ProfileSetupStage,
    [property: JsonPropertyName("requiresSocialOptIn")] bool RequiresSocialOptIn,
    [property: JsonPropertyName("socialOptInConfirmed")] bool SocialOptInConfirmed,
    [property: JsonPropertyName("canProceedToProfileSetup")] bool CanProceedToProfileSetup
);

public sealed record ModuleWelcomeGetStartedRequest(
    [property: JsonPropertyName("socialOptIn")] bool? SocialOptIn
);

public sealed record ModuleWelcomeGetStartedResponse(
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("canProceed")] bool CanProceed,
    [property: JsonPropertyName("nextStep")] string NextStep,
    [property: JsonPropertyName("profileSetupStage")] string ProfileSetupStage,
    [property: JsonPropertyName("message")] string? Message
);

public sealed class ModuleWelcomeIneligibleException : Exception
{
    public ModuleWelcomeIneligibleException(string message) : base(message) { }
}

public sealed class SocialOptInRequiredException : Exception
{
    public SocialOptInRequiredException(string message) : base(message) { }
}
