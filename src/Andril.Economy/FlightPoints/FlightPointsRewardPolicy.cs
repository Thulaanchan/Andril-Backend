namespace Andril.Economy.FlightPoints;

/// <summary>
/// Eligibility facts supplied by a trusted backend reward service.
/// </summary>
public readonly record struct FlightPointsEligibility(
    bool IsAdminApproved,
    bool IsRejected,
    bool IsEligible,
    bool IsRepeatSubmission,
    bool IsFirstEligibleContribution,
    bool IsFirstExplorer);

public readonly record struct FlightPointsRewardCalculation(
    int FirstContributionPoints,
    int FirstExplorerPoints,
    bool RequiresEligibilityResolution)
{
    public int TotalPoints => FirstContributionPoints + FirstExplorerPoints;
}

/// <summary>
/// Calculates Flight Points from established facts without granting or recording a reward.
/// </summary>
public static class FlightPointsRewardPolicy
{
    public const int FirstEligibleContributionPoints = 5;
    public const int FirstExplorerBonusPoints = 10;

    public static FlightPointsRewardCalculation Calculate(FlightPointsEligibility eligibility)
    {
        if (!eligibility.IsAdminApproved || eligibility.IsRejected
            || !eligibility.IsEligible || eligibility.IsRepeatSubmission)
        {
            return default;
        }

        if (!eligibility.IsFirstEligibleContribution)
        {
            return new FlightPointsRewardCalculation(0, 0,
                RequiresEligibilityResolution: eligibility.IsFirstExplorer);
        }

        return new FlightPointsRewardCalculation(
            FirstEligibleContributionPoints,
            eligibility.IsFirstExplorer ? FirstExplorerBonusPoints : 0,
            RequiresEligibilityResolution: false);
    }
}
