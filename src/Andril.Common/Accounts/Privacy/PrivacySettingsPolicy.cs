namespace Andril.Common.Accounts.Privacy;

public static class PrivacySettingsPolicy
{
    public const string DiscoverabilityPublic = "public";
    public const string DiscoverabilityContactsOnly = "contacts_only";
    public const string DiscoverabilityPrivate = "private";

    public const string MessagingEveryone = "everyone";
    public const string MessagingFriendsOnly = "friends_only";
    public const string MessagingDisabled = "disabled";

    public static string NormalizeDiscoverability(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PrivacyValidationException("Profile discoverability value is required.");

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            DiscoverabilityPublic => DiscoverabilityPublic,
            DiscoverabilityContactsOnly => DiscoverabilityContactsOnly,
            DiscoverabilityPrivate => DiscoverabilityPrivate,
            _ => throw new PrivacyValidationException($"Invalid profile discoverability '{value}'. Allowed: {DiscoverabilityPublic}, {DiscoverabilityContactsOnly}, {DiscoverabilityPrivate}.")
        };
    }

    public static string NormalizeMessaging(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PrivacyValidationException("Direct messaging policy is required.");

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            MessagingEveryone => MessagingEveryone,
            MessagingFriendsOnly => MessagingFriendsOnly,
            MessagingDisabled => MessagingDisabled,
            _ => throw new PrivacyValidationException($"Invalid direct messaging policy '{value}'. Allowed: {MessagingEveryone}, {MessagingFriendsOnly}, {MessagingDisabled}.")
        };
    }
}
