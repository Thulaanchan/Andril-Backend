using System;
using System.Reflection;
using Andril.Economy.FlightPoints;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class FlightPointsLedgerEntryTests
{
    private static readonly Guid EntryId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ProfileId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid ContributionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid AttractionId = new("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ReversalId = new("55555555-5555-4555-8555-555555555555");
    private static readonly DateTimeOffset GrantedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromMinutes(330));
    private static readonly DateTimeOffset ReversedAt = new(2026, 11, 19, 13, 0, 0, TimeSpan.FromHours(-4));
    private const string GrantKey = "synthetic-grant";
    private const string ReversalKey = "synthetic-reversal";

    [Theory]
    [InlineData(FlightPointsRewardType.FirstEligibleContribution, 5)]
    [InlineData(FlightPointsRewardType.FirstExplorer, 10)]
    public void CreateGrant_PreservesIdentityAndDerivesPoints(FlightPointsRewardType rewardType, int points)
    {
        AssertGrant(CreateGrant(rewardType), rewardType, points);
    }

    [Fact]
    public void CreateGrant_RejectsEmptyLedgerEntryId() =>
        Assert.Throws<ArgumentException>("ledgerEntryId", () => FlightPointsLedgerEntry.CreateGrant(
            Guid.Empty, ProfileId, ContributionId, AttractionId, FlightPointsRewardType.FirstExplorer, GrantKey, GrantedAt));

    [Fact]
    public void CreateGrant_RejectsEmptyOwnerProfileId() =>
        Assert.Throws<ArgumentException>("ownerHobbiesModuleProfileId", () => FlightPointsLedgerEntry.CreateGrant(
            EntryId, Guid.Empty, ContributionId, AttractionId, FlightPointsRewardType.FirstExplorer, GrantKey, GrantedAt));

    [Fact]
    public void CreateGrant_RejectsEmptyContributionId() =>
        Assert.Throws<ArgumentException>("contributionId", () => FlightPointsLedgerEntry.CreateGrant(
            EntryId, ProfileId, Guid.Empty, AttractionId, FlightPointsRewardType.FirstExplorer, GrantKey, GrantedAt));

    [Fact]
    public void CreateGrant_RejectsEmptyAttractionId() =>
        Assert.Throws<ArgumentException>("attractionId", () => FlightPointsLedgerEntry.CreateGrant(
            EntryId, ProfileId, ContributionId, Guid.Empty, FlightPointsRewardType.FirstExplorer, GrantKey, GrantedAt));

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Factories_RejectBlankIdempotencyKeys(string key)
    {
        Assert.Throws<ArgumentException>("idempotencyKey", () => CreateGrant(key: key));
        Assert.Throws<ArgumentException>("idempotencyKey", () =>
            FlightPointsLedgerEntry.CreateReversal(ReversalId, CreateGrant(), key, ReversedAt));
    }

    [Fact]
    public void Factories_RejectNullIdempotencyKeys()
    {
        Assert.Throws<ArgumentNullException>("idempotencyKey", () => CreateGrant(key: null!));
        Assert.Throws<ArgumentNullException>("idempotencyKey", () =>
            FlightPointsLedgerEntry.CreateReversal(ReversalId, CreateGrant(), null!, ReversedAt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    public void CreateGrant_RejectsUndefinedRewardType(int rewardType) =>
        Assert.Throws<ArgumentOutOfRangeException>("rewardType", () => CreateGrant((FlightPointsRewardType)rewardType));

    [Theory]
    [InlineData(FlightPointsRewardType.FirstEligibleContribution, 5)]
    [InlineData(FlightPointsRewardType.FirstExplorer, 10)]
    public void CreateReversal_OffsetsOriginalGrantWithoutChangingIt(FlightPointsRewardType rewardType, int points)
    {
        var grant = CreateGrant(rewardType);
        var reversal = FlightPointsLedgerEntry.CreateReversal(ReversalId, grant, ReversalKey, ReversedAt);

        Assert.Equal(ReversalId, reversal.LedgerEntryId);
        Assert.Equal(EntryId, reversal.ReversedEntryId);
        Assert.Equal(ProfileId, reversal.OwnerHobbiesModuleProfileId);
        Assert.Equal(ContributionId, reversal.ContributionId);
        Assert.Equal(AttractionId, reversal.AttractionId);
        Assert.Equal(FlightPointsLedgerEntryType.Reversal, reversal.EntryType);
        Assert.Equal(rewardType, reversal.RewardType);
        Assert.Equal(-points, reversal.PointsDelta);
        Assert.Equal(ReversalKey, reversal.IdempotencyKey);
        Assert.True(reversal.CreatedAtUtc.EqualsExact(ReversedAt.ToUniversalTime()));
        AssertGrant(grant, rewardType, points);
    }

    [Fact]
    public void CreateReversal_RejectsMissingOriginalGrant() =>
        Assert.Throws<ArgumentNullException>("originalGrant", () =>
            FlightPointsLedgerEntry.CreateReversal(ReversalId, null!, ReversalKey, ReversedAt));

    [Fact]
    public void CreateReversal_RejectsEmptyOrOriginalEntryId()
    {
        var grant = CreateGrant();
        Assert.Throws<ArgumentException>("ledgerEntryId", () =>
            FlightPointsLedgerEntry.CreateReversal(Guid.Empty, grant, ReversalKey, ReversedAt));
        Assert.Throws<ArgumentException>("ledgerEntryId", () =>
            FlightPointsLedgerEntry.CreateReversal(grant.LedgerEntryId, grant, ReversalKey, ReversedAt));
    }

    [Fact]
    public void CreateReversal_RejectsReversingAReversal()
    {
        var reversal = FlightPointsLedgerEntry.CreateReversal(ReversalId, CreateGrant(), ReversalKey, ReversedAt);
        Assert.Throws<ArgumentException>("originalGrant", () =>
            FlightPointsLedgerEntry.CreateReversal(AttractionId, reversal, "synthetic-second-reversal", ReversedAt));
    }

    [Fact]
    public void DomainState_HasNoPublicMutationOrDirectConstruction()
    {
        var ledgerType = typeof(FlightPointsLedgerEntry);
        Assert.True(ledgerType.IsSealed);
        Assert.Empty(ledgerType.GetConstructors());
        Assert.Empty(ledgerType.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(ledgerType.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true, property.Name));
        Assert.DoesNotContain(ledgerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
    }

    private static FlightPointsLedgerEntry CreateGrant(
        FlightPointsRewardType rewardType = FlightPointsRewardType.FirstEligibleContribution,
        string key = GrantKey) =>
        FlightPointsLedgerEntry.CreateGrant(EntryId, ProfileId, ContributionId, AttractionId, rewardType, key, GrantedAt);

    private static void AssertGrant(FlightPointsLedgerEntry grant, FlightPointsRewardType rewardType, int points)
    {
        Assert.Equal(EntryId, grant.LedgerEntryId);
        Assert.Equal(ProfileId, grant.OwnerHobbiesModuleProfileId);
        Assert.Equal(ContributionId, grant.ContributionId);
        Assert.Equal(AttractionId, grant.AttractionId);
        Assert.Equal(FlightPointsLedgerEntryType.Grant, grant.EntryType);
        Assert.Equal(rewardType, grant.RewardType);
        Assert.Equal(points, grant.PointsDelta);
        Assert.Equal(GrantKey, grant.IdempotencyKey);
        Assert.Null(grant.ReversedEntryId);
        Assert.True(grant.CreatedAtUtc.EqualsExact(GrantedAt.ToUniversalTime()));
    }
}
