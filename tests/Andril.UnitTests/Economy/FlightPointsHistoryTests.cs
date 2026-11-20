using System;
using System.Linq;
using System.Reflection;
using Andril.Economy.FlightPoints;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class FlightPointsHistoryTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 25)]
    [InlineData(int.MaxValue, 100)]
    public void Query_PreservesValidBounds(int offset, int limit)
    {
        var query = new FlightPointsHistoryQuery(offset, limit);

        Assert.Equal(offset, query.Offset);
        Assert.Equal(limit, query.Limit);
    }

    [Theory]
    [InlineData(-1, 1, "offset")]
    [InlineData(0, 0, "limit")]
    [InlineData(0, -1, "limit")]
    [InlineData(0, 101, "limit")]
    public void Query_RejectsInvalidBounds(int offset, int limit, string parameter) =>
        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => new FlightPointsHistoryQuery(offset, limit));

    [Fact]
    public void Models_HaveOnlyReadOnlyAuthorizedProperties()
    {
        AssertReadOnlyShape(typeof(FlightPointsHistoryQuery), ["Limit", "Offset"]);
        AssertReadOnlyShape(typeof(FlightPointsHistoryItem),
            ["AttractionId", "ContributionId", "CreatedAtUtc", "EntryType", "LedgerEntryId",
                "PointsDelta", "ReversedEntryId", "RewardType"]);
    }

    [Theory]
    [InlineData(FlightPointsLedgerEntryType.Grant, 5)]
    [InlineData(FlightPointsLedgerEntryType.Reversal, -5)]
    public void Item_PreservesLedgerValues(FlightPointsLedgerEntryType entryType, int delta)
    {
        var id = new Guid("10000000-0000-4000-8000-000000000001");
        var contribution = new Guid("20000000-0000-4000-8000-000000000001");
        var attraction = new Guid("30000000-0000-4000-8000-000000000001");
        Guid? original = delta < 0 ? new Guid("10000000-0000-4000-8000-000000000002") : null;
        var createdAt = new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.Zero);
        var item = new FlightPointsHistoryItem(id, contribution, attraction, entryType,
            FlightPointsRewardType.FirstEligibleContribution, delta, original, createdAt);

        Assert.Equal(id, item.LedgerEntryId);
        Assert.Equal(contribution, item.ContributionId);
        Assert.Equal(attraction, item.AttractionId);
        Assert.Equal(entryType, item.EntryType);
        Assert.Equal(FlightPointsRewardType.FirstEligibleContribution, item.RewardType);
        Assert.Equal(delta, item.PointsDelta);
        Assert.Equal(original, item.ReversedEntryId);
        Assert.Equal(createdAt, item.CreatedAtUtc);
    }

    private static void AssertReadOnlyShape(Type type, string[] expected)
    {
        var properties = type.GetProperties();
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(expected, properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(properties, property => Assert.Null(property.SetMethod));
    }
}
