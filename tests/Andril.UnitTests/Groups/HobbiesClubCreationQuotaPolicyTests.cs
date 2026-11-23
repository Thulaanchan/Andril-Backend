using System;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class HobbiesClubCreationQuotaPolicyTests
{
    private static readonly GroupModuleScope HobbiesClubScope = new("hobbies", GroupKind.Club);
    private static readonly GroupModuleScope LearningClassroomScope = new("learning", GroupKind.Classroom);
    private static readonly GroupModuleScope SocialZoneScope = new("social", GroupKind.Zone);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void Evaluate_FreeTier_BelowLimit_ReturnsEligible(int count)
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            count,
            HobbiesClubEntitlementTier.Free);

        Assert.NotNull(result);
        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.Eligible, result.Status);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Evaluate_FreeTier_AtOrAboveLimit_ReturnsQuotaReached(int count)
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            count,
            HobbiesClubEntitlementTier.Free);

        Assert.NotNull(result);
        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.QuotaReached, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Evaluate_PremiumTier_LargeCounts_ReturnsEligible(int count)
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            count,
            HobbiesClubEntitlementTier.Premium);

        Assert.NotNull(result);
        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.Eligible, result.Status);
    }

    [Fact]
    public void Evaluate_MissingOrUnknownEntitlement_ReturnsMissingOrInvalidEntitlement()
    {
        var unknownResult = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            0,
            HobbiesClubEntitlementTier.Unknown);

        var invalidEnumResult = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            0,
            (HobbiesClubEntitlementTier)999);

        Assert.False(unknownResult.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.MissingOrInvalidEntitlement, unknownResult.Status);

        Assert.False(invalidEnumResult.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.MissingOrInvalidEntitlement, invalidEnumResult.Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Evaluate_NegativeCount_ReturnsInvalidCount(int negativeCount)
    {
        var freeResult = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            negativeCount,
            HobbiesClubEntitlementTier.Free);

        var premiumResult = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            negativeCount,
            HobbiesClubEntitlementTier.Premium);

        Assert.False(freeResult.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.InvalidCount, freeResult.Status);

        Assert.False(premiumResult.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.InvalidCount, premiumResult.Status);
    }

    [Fact]
    public void Evaluate_ClassroomScope_ReturnsInvalidScope()
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            LearningClassroomScope,
            0,
            HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.InvalidScope, result.Status);
    }

    [Fact]
    public void Evaluate_ZoneScope_ReturnsInvalidScope()
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            SocialZoneScope,
            0,
            HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.InvalidScope, result.Status);
    }

    [Fact]
    public void Evaluate_NullScope_ReturnsInvalidScope()
    {
        var result = HobbiesClubCreationQuotaPolicy.Evaluate(
            null,
            0,
            HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.InvalidScope, result.Status);
    }

    [Fact]
    public void Evaluate_ExpiredPremium_FollowsFreeTierQuota()
    {
        var underLimit = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            4,
            HobbiesClubEntitlementTier.ExpiredPremium);

        var atLimit = HobbiesClubCreationQuotaPolicy.Evaluate(
            HobbiesClubScope,
            5,
            HobbiesClubEntitlementTier.ExpiredPremium);

        Assert.True(underLimit.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.Eligible, underLimit.Status);

        Assert.False(atLimit.IsEligible);
        Assert.Equal(HobbiesClubCreationQuotaStatus.QuotaReached, atLimit.Status);
    }

    [Fact]
    public void Evaluate_IsDeterministic_HasNoSideEffects()
    {
        var result1 = HobbiesClubCreationQuotaPolicy.Evaluate(HobbiesClubScope, 2, HobbiesClubEntitlementTier.Free);
        var result2 = HobbiesClubCreationQuotaPolicy.Evaluate(HobbiesClubScope, 2, HobbiesClubEntitlementTier.Free);

        Assert.Equal(result1.Status, result2.Status);
        Assert.Equal(result1.IsEligible, result2.IsEligible);
        Assert.Equal(result1.Reason, result2.Reason);
    }
}
