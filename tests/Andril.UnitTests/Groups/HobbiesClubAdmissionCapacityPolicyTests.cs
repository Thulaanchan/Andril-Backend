using System;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class HobbiesClubAdmissionCapacityPolicyTests
{
    private static readonly GroupModuleScope HobbiesClubScope = new("hobbies", GroupKind.Club);
    private static readonly GroupModuleScope LearningClassroomScope = new("learning", GroupKind.Classroom);
    private static readonly GroupModuleScope SocialZoneScope = new("social", GroupKind.Zone);

    [Fact]
    public void Evaluate_FreeTier_Count49_ReturnsEligible()
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            49,
            HobbiesClubEntitlementTier.Free);

        Assert.NotNull(result);
        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.Eligible, result.Status);
        Assert.Equal(50, result.MaxCapacity);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    public void Evaluate_FreeTier_AtOrAbove50_ReturnsCapacityReached(int count)
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            count,
            HobbiesClubEntitlementTier.Free);

        Assert.NotNull(result);
        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.CapacityReached, result.Status);
        Assert.Equal(50, result.MaxCapacity);
    }

    [Fact]
    public void Evaluate_PremiumTier_Count99_ReturnsEligible()
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            99,
            HobbiesClubEntitlementTier.Premium);

        Assert.NotNull(result);
        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.Eligible, result.Status);
        Assert.Equal(100, result.MaxCapacity);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public void Evaluate_PremiumTier_AtOrAbove100_ReturnsCapacityReached(int count)
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            count,
            HobbiesClubEntitlementTier.Premium);

        Assert.NotNull(result);
        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.CapacityReached, result.Status);
        Assert.Equal(100, result.MaxCapacity);
    }

    [Fact]
    public void Evaluate_ExpiredPremium_Count30_ReturnsEligible()
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            30,
            HobbiesClubEntitlementTier.ExpiredPremium);

        Assert.NotNull(result);
        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.Eligible, result.Status);
        Assert.Equal(50, result.MaxCapacity);
    }

    [Fact]
    public void Evaluate_ExpiredPremium_Count50_ReturnsCapacityReached()
    {
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            50,
            HobbiesClubEntitlementTier.ExpiredPremium);

        Assert.NotNull(result);
        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.CapacityReached, result.Status);
        Assert.Equal(50, result.MaxCapacity);
    }

    [Fact]
    public void Evaluate_ExpiredPremium_Count75_ReturnsCapacityReached_RetainsExisting()
    {
        // When a Club was created/grown under Premium with 75 members, and Premium expires,
        // new joins are blocked (>= 50), but existing members remain unaffected.
        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            75,
            HobbiesClubEntitlementTier.ExpiredPremium);

        Assert.NotNull(result);
        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.CapacityReached, result.Status);
        Assert.Equal(50, result.MaxCapacity);
        Assert.Contains("Existing members are retained", result.Reason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    public void Evaluate_NegativeCount_ReturnsInvalidCount(int negativeCount)
    {
        var freeResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            negativeCount,
            HobbiesClubEntitlementTier.Free);

        var premiumResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            negativeCount,
            HobbiesClubEntitlementTier.Premium);

        Assert.False(freeResult.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.InvalidCount, freeResult.Status);

        Assert.False(premiumResult.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.InvalidCount, premiumResult.Status);
    }

    [Fact]
    public void Evaluate_NonClubScopes_Rejected()
    {
        var classroomResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            LearningClassroomScope,
            10,
            HobbiesClubEntitlementTier.Free);

        var zoneResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            SocialZoneScope,
            10,
            HobbiesClubEntitlementTier.Free);

        var nullResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            null,
            10,
            HobbiesClubEntitlementTier.Free);

        Assert.Equal(HobbiesClubAdmissionCapacityStatus.InvalidScope, classroomResult.Status);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.InvalidScope, zoneResult.Status);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.InvalidScope, nullResult.Status);
    }

    [Fact]
    public void Evaluate_MissingOrUnknownEntitlement_ReturnsMissingOrInvalidEntitlement()
    {
        var unknownResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            10,
            HobbiesClubEntitlementTier.Unknown);

        var invalidEnumResult = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            10,
            (HobbiesClubEntitlementTier)999);

        Assert.Equal(HobbiesClubAdmissionCapacityStatus.MissingOrInvalidEntitlement, unknownResult.Status);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.MissingOrInvalidEntitlement, invalidEnumResult.Status);
    }

    [Fact]
    public void Evaluate_AllMemberRolesCountTowardSameCapacity()
    {
        // Regardless of role breakdown (e.g. 1 Owner, 4 Admins, 45 Members = 50 total),
        // total count of 50 reaches Free tier capacity.
        const int totalCountAcrossAllRoles = 50;

        var result = HobbiesClubAdmissionCapacityPolicy.Evaluate(
            HobbiesClubScope,
            totalCountAcrossAllRoles,
            HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionCapacityStatus.CapacityReached, result.Status);
    }

    [Fact]
    public void Evaluate_IsDeterministic_HasNoSideEffects()
    {
        var res1 = HobbiesClubAdmissionCapacityPolicy.Evaluate(HobbiesClubScope, 40, HobbiesClubEntitlementTier.Free);
        var res2 = HobbiesClubAdmissionCapacityPolicy.Evaluate(HobbiesClubScope, 40, HobbiesClubEntitlementTier.Free);

        Assert.Equal(res1.Status, res2.Status);
        Assert.Equal(res1.IsEligible, res2.IsEligible);
        Assert.Equal(res1.MaxCapacity, res2.MaxCapacity);
        Assert.Equal(res1.Reason, res2.Reason);
    }
}
