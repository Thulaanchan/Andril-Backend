using System.Reflection;
using Andril.Economy.FlightPoints;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class FlightPointsRewardPolicyTests
{
    private static readonly FlightPointsEligibility FirstContribution = new(
        IsAdminApproved: true,
        IsRejected: false,
        IsEligible: true,
        IsRepeatSubmission: false,
        IsFirstEligibleContribution: true,
        IsFirstExplorer: false);

    [Fact]
    public void RewardConstants_MatchConfirmedAmounts()
    {
        Assert.Equal(5, FlightPointsRewardPolicy.FirstEligibleContributionPoints);
        Assert.Equal(10, FlightPointsRewardPolicy.FirstExplorerBonusPoints);
    }

    [Theory]
    [InlineData(false, 0, 5)]
    [InlineData(true, 10, 15)]
    public void Calculate_ApprovedFirstContribution_ReturnsSeparateRewardComponents(
        bool firstExplorer, int explorerPoints, int total)
    {
        var result = FlightPointsRewardPolicy.Calculate(FirstContribution with { IsFirstExplorer = firstExplorer });

        Assert.Equal(5, result.FirstContributionPoints);
        Assert.Equal(explorerPoints, result.FirstExplorerPoints);
        Assert.Equal(total, result.TotalPoints);
        Assert.False(result.RequiresEligibilityResolution);
    }

    [Theory]
    [InlineData(false, false, true, false)] // Approval pending.
    [InlineData(false, true, true, false)]  // Rejected.
    [InlineData(true, true, true, false)]   // Rejection denies contradictory approval facts.
    [InlineData(true, false, false, false)] // Ineligible.
    [InlineData(true, false, true, true)]   // Repeated.
    public void Calculate_DeniedContribution_ReturnsZeroEvenWithBothRewardFlags(
        bool approved, bool rejected, bool eligible, bool repeated)
    {
        var facts = new FlightPointsEligibility(approved, rejected, eligible, repeated,
            IsFirstEligibleContribution: true, IsFirstExplorer: true);

        Assert.Equal(default(FlightPointsRewardCalculation), FlightPointsRewardPolicy.Calculate(facts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Calculate_WithoutFirstContribution_ReturnsZeroAndFlagsExplorerOnly(bool firstExplorer)
    {
        var facts = FirstContribution with
        {
            IsFirstEligibleContribution = false,
            IsFirstExplorer = firstExplorer
        };

        var result = FlightPointsRewardPolicy.Calculate(facts);

        Assert.Equal(0, result.FirstContributionPoints);
        Assert.Equal(0, result.FirstExplorerPoints);
        Assert.Equal(0, result.TotalPoints);
        Assert.Equal(firstExplorer, result.RequiresEligibilityResolution);
    }

    [Fact]
    public void Calculate_IsRepeatableWithoutBalanceOrLedgerState()
    {
        var facts = FirstContribution with { IsFirstExplorer = true };
        var first = FlightPointsRewardPolicy.Calculate(facts);

        Assert.Equal(default(FlightPointsRewardCalculation), FlightPointsRewardPolicy.Calculate(default));
        Assert.Equal(first, FlightPointsRewardPolicy.Calculate(facts));
        Assert.All(typeof(FlightPointsRewardPolicy).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), field => Assert.True(field.IsLiteral));
    }
}
