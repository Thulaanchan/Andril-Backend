namespace Andril.Common.Accounts.Privacy;

/// <summary>
/// Encapsulates user privacy settings (CF-11).
/// Guarantees fail-closed privacy by default and strict relational constraints.
/// </summary>
public sealed class UserPrivacySettings
{
    private UserPrivacySettings() { }

    public Guid AccountId { get; private set; }
    public string ProfileDiscoverability { get; private set; } = PrivacySettingsPolicy.DiscoverabilityPublic;
    public bool GhostModeEnabled { get; private set; }
    public string DirectMessagingPolicy { get; private set; } = PrivacySettingsPolicy.MessagingEveryone;
    public bool AllowFriendRequests { get; private set; } = true;
    public bool ShowActiveModule { get; private set; } = true;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static UserPrivacySettings CreateDefault(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
            throw new PrivacyValidationException("Account identifier cannot be empty.");

        var utc = now.ToUniversalTime();
        return new UserPrivacySettings
        {
            AccountId = accountId,
            ProfileDiscoverability = PrivacySettingsPolicy.DiscoverabilityPublic,
            GhostModeEnabled = false,
            DirectMessagingPolicy = PrivacySettingsPolicy.MessagingEveryone,
            AllowFriendRequests = true,
            ShowActiveModule = true,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    public void Update(
        string profileDiscoverability,
        bool ghostModeEnabled,
        string directMessagingPolicy,
        bool allowFriendRequests,
        bool showActiveModule,
        DateTimeOffset now)
    {
        var normalizedDiscoverability = PrivacySettingsPolicy.NormalizeDiscoverability(profileDiscoverability);
        var normalizedMessaging = PrivacySettingsPolicy.NormalizeMessaging(directMessagingPolicy);

        ProfileDiscoverability = normalizedDiscoverability;
        GhostModeEnabled = ghostModeEnabled;
        DirectMessagingPolicy = normalizedMessaging;
        AllowFriendRequests = allowFriendRequests;
        ShowActiveModule = showActiveModule;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
