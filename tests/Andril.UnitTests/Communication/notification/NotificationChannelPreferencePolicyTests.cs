
using System;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

public sealed class NotificationChannelPreferencePolicyTests
{
    private static NotificationModuleScope CreateScope(
        NotificationModule module = NotificationModule.Learning,
        Guid? profileId = null)
    {
        return new NotificationModuleScope(
            module,
            profileId ?? Guid.NewGuid());
    }

    private static NotificationChannelPreferences CreatePreferences(
        NotificationModuleScope scope,
        bool inApp = true,
        bool push = true,
        bool email = true)
    {
        return new NotificationChannelPreferences(
            scope,
            inApp,
            push,
            email);
    }

    [Fact]
    public void Constructor_ShouldPreservePreferences()
    {
        var scope = CreateScope();

        var preferences = new NotificationChannelPreferences(
            scope,
            true,
            false,
            true);

        Assert.Equal(scope, preferences.RecipientScope);
        Assert.True(preferences.InAppEnabled);
        Assert.False(preferences.PushEnabled);
        Assert.True(preferences.EmailEnabled);
    }

    [Fact]
    public void Constructor_ShouldRejectNullRecipientScope()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotificationChannelPreferences(
                null!,
                true,
                true,
                true));
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp, true, false, false)]
    [InlineData(NotificationDeliveryChannel.Push, false, true, false)]
    [InlineData(NotificationDeliveryChannel.Email, false, false, true)]
    public void EnabledChannel_ShouldAllowDelivery(
        NotificationDeliveryChannel channel,
        bool inApp,
        bool push,
        bool email)
    {
        var scope = CreateScope();

        var preferences = CreatePreferences(
            scope,
            inApp,
            push,
            email);

        var result = NotificationChannelPreferencePolicy.CanDeliver(
            scope,
            preferences,
            channel);

        Assert.True(result);
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp, false, true, true)]
    [InlineData(NotificationDeliveryChannel.Push, true, false, true)]
    [InlineData(NotificationDeliveryChannel.Email, true, true, false)]
    public void DisabledChannel_ShouldDenyDelivery(
        NotificationDeliveryChannel channel,
        bool inApp,
        bool push,
        bool email)
    {
        var scope = CreateScope();

        var preferences = CreatePreferences(
            scope,
            inApp,
            push,
            email);

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                scope,
                preferences,
                channel));
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp)]
    [InlineData(NotificationDeliveryChannel.Push)]
    [InlineData(NotificationDeliveryChannel.Email)]
    public void MissingAuthorization_ShouldDenyDelivery(
        NotificationDeliveryChannel channel)
    {
        var scope = CreateScope();
        var preferences = CreatePreferences(scope);

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                null,
                preferences,
                channel));
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp)]
    [InlineData(NotificationDeliveryChannel.Push)]
    [InlineData(NotificationDeliveryChannel.Email)]
    public void MissingPreferences_ShouldDenyDelivery(
        NotificationDeliveryChannel channel)
    {
        var scope = CreateScope();

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                scope,
                null,
                channel));
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp)]
    [InlineData(NotificationDeliveryChannel.Push)]
    [InlineData(NotificationDeliveryChannel.Email)]
    public void DifferentModule_ShouldDenyDelivery(
        NotificationDeliveryChannel channel)
    {
        var profileId = Guid.NewGuid();

        var authorizedScope = CreateScope(
            NotificationModule.Learning,
            profileId);

        var foreignScope = CreateScope(
            NotificationModule.Hobbies,
            profileId);

        var preferences = CreatePreferences(foreignScope);

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                authorizedScope,
                preferences,
                channel));
    }

    [Theory]
    [InlineData(NotificationDeliveryChannel.InApp)]
    [InlineData(NotificationDeliveryChannel.Push)]
    [InlineData(NotificationDeliveryChannel.Email)]
    public void SameModuleDifferentProfile_ShouldDenyDelivery(
        NotificationDeliveryChannel channel)
    {
        var authorizedScope = CreateScope(
            NotificationModule.Hobbies);

        var foreignScope = CreateScope(
            NotificationModule.Hobbies);

        var preferences = CreatePreferences(foreignScope);

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                authorizedScope,
                preferences,
                channel));
    }

    [Theory]
    [InlineData((NotificationDeliveryChannel)0)]
    [InlineData((NotificationDeliveryChannel)999)]
    [InlineData((NotificationDeliveryChannel)(-1))]
    public void InvalidChannel_ShouldDenyDelivery(
        NotificationDeliveryChannel channel)
    {
        var scope = CreateScope();
        var preferences = CreatePreferences(scope);

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                scope,
                preferences,
                channel));
    }

    [Fact]
    public void AllChannelsDisabled_ShouldDenyEveryChannel()
    {
        var scope = CreateScope();

        var preferences = CreatePreferences(
            scope,
            false,
            false,
            false);

        foreach (var channel in new[]
        {
            NotificationDeliveryChannel.InApp,
            NotificationDeliveryChannel.Push,
            NotificationDeliveryChannel.Email
        })
        {
            Assert.False(
                NotificationChannelPreferencePolicy.CanDeliver(
                    scope,
                    preferences,
                    channel));
        }
    }

    [Fact]
    public void EquivalentRecipientScope_ShouldAllowEnabledChannel()
    {
        var profileId = Guid.NewGuid();

        var authorizedScope = CreateScope(
            NotificationModule.Learning,
            profileId);

        var preferenceScope = CreateScope(
            NotificationModule.Learning,
            profileId);

        var preferences = CreatePreferences(
            preferenceScope,
            true,
            false,
            false);

        Assert.True(
            NotificationChannelPreferencePolicy.CanDeliver(
                authorizedScope,
                preferences,
                NotificationDeliveryChannel.InApp));

        Assert.False(
            NotificationChannelPreferencePolicy.CanDeliver(
                authorizedScope,
                preferences,
                NotificationDeliveryChannel.Push));
    }
}
