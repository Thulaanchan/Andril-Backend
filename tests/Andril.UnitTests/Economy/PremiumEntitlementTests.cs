using Andril.Economy.Premium;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class PremiumEntitlementTests
{
    private readonly DateTimeOffset _testTime = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDefault_ReturnsFreeTierInactive()
    {
        var accountId = Guid.NewGuid();
        var entitlement = EntitlementRecord.CreateDefault(accountId, _testTime);

        Assert.Equal(accountId, entitlement.AccountId);
        Assert.Equal(SubscriptionTier.Free, entitlement.Tier);
        Assert.False(entitlement.IsActive);
        Assert.Null(entitlement.ExpiresAtUtc);
        Assert.Equal("none", entitlement.Provider);
    }

    [Fact]
    public void ActivateSubscription_SetsPremiumTierAndExpiry()
    {
        var accountId = Guid.NewGuid();
        var entitlement = EntitlementRecord.CreateDefault(accountId, _testTime);

        var expiry = _testTime.AddMonths(1);
        entitlement.ActivateSubscription(
            SubscriptionTier.PremiumMonthly,
            expiry,
            "apple_iap",
            "ext_sub_12345",
            _testTime);

        Assert.Equal(SubscriptionTier.PremiumMonthly, entitlement.Tier);
        Assert.True(entitlement.IsActive);
        Assert.Equal(expiry, entitlement.ExpiresAtUtc);
        Assert.Equal("apple_iap", entitlement.Provider);
        Assert.Equal("ext_sub_12345", entitlement.ExternalSubscriptionId);
    }

    [Fact]
    public void Revoke_ResetsToFreeTier()
    {
        var accountId = Guid.NewGuid();
        var entitlement = EntitlementRecord.CreateDefault(accountId, _testTime);
        entitlement.ActivateSubscription(
            SubscriptionTier.PremiumAnnual,
            _testTime.AddYears(1),
            "stripe",
            "sub_stripe_1",
            _testTime);

        entitlement.Revoke(_testTime.AddDays(5));

        Assert.Equal(SubscriptionTier.Free, entitlement.Tier);
        Assert.False(entitlement.IsActive);
        Assert.Null(entitlement.ExpiresAtUtc);
    }
}
