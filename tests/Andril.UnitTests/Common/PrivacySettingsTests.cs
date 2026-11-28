using Andril.Common.Accounts.Privacy;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class PrivacySettingsTests
{
    private readonly DateTimeOffset _testTime = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDefault_SetsCorrectDefaultValues()
    {
        var accountId = Guid.NewGuid();
        var settings = UserPrivacySettings.CreateDefault(accountId, _testTime);

        Assert.Equal(accountId, settings.AccountId);
        Assert.Equal(PrivacySettingsPolicy.DiscoverabilityPublic, settings.ProfileDiscoverability);
        Assert.False(settings.GhostModeEnabled);
        Assert.Equal(PrivacySettingsPolicy.MessagingEveryone, settings.DirectMessagingPolicy);
        Assert.True(settings.AllowFriendRequests);
        Assert.True(settings.ShowActiveModule);
        Assert.Equal(_testTime, settings.CreatedAtUtc);
        Assert.Equal(_testTime, settings.UpdatedAtUtc);
    }

    [Fact]
    public void CreateDefault_EmptyAccountId_ThrowsPrivacyValidationException()
    {
        Assert.Throws<PrivacyValidationException>(() =>
            UserPrivacySettings.CreateDefault(Guid.Empty, _testTime));
    }

    [Fact]
    public void Update_ValidInputs_UpdatesFieldsAndTimestamp()
    {
        var accountId = Guid.NewGuid();
        var settings = UserPrivacySettings.CreateDefault(accountId, _testTime);

        var updateTime = _testTime.AddHours(2);
        settings.Update(
            PrivacySettingsPolicy.DiscoverabilityContactsOnly,
            ghostModeEnabled: true,
            PrivacySettingsPolicy.MessagingFriendsOnly,
            allowFriendRequests: false,
            showActiveModule: false,
            updateTime);

        Assert.Equal(PrivacySettingsPolicy.DiscoverabilityContactsOnly, settings.ProfileDiscoverability);
        Assert.True(settings.GhostModeEnabled);
        Assert.Equal(PrivacySettingsPolicy.MessagingFriendsOnly, settings.DirectMessagingPolicy);
        Assert.False(settings.AllowFriendRequests);
        Assert.False(settings.ShowActiveModule);
        Assert.Equal(updateTime, settings.UpdatedAtUtc);
        Assert.Equal(_testTime, settings.CreatedAtUtc);
    }

    [Theory]
    [InlineData("invalid_discoverability")]
    [InlineData("")]
    [InlineData(" ")]
    public void Update_InvalidDiscoverability_ThrowsPrivacyValidationException(string invalid)
    {
        var settings = UserPrivacySettings.CreateDefault(Guid.NewGuid(), _testTime);

        Assert.Throws<PrivacyValidationException>(() =>
            settings.Update(
                invalid,
                false,
                PrivacySettingsPolicy.MessagingEveryone,
                true,
                true,
                _testTime.AddMinutes(1)));
    }

    [Theory]
    [InlineData("invalid_messaging")]
    [InlineData("")]
    [InlineData(null)]
    public void Update_InvalidMessagingPolicy_ThrowsPrivacyValidationException(string? invalid)
    {
        var settings = UserPrivacySettings.CreateDefault(Guid.NewGuid(), _testTime);

        Assert.Throws<PrivacyValidationException>(() =>
            settings.Update(
                PrivacySettingsPolicy.DiscoverabilityPublic,
                false,
                invalid!,
                true,
                true,
                _testTime.AddMinutes(1)));
    }
}
