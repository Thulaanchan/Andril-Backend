namespace Andril.Social.Matching;

/// <summary>
/// The approved recommendation tiers for Social matching candidates (P-04 / Task 151).
/// </summary>
public enum SocialRecommendationTier
{
    /// <summary>Strong compatibility: score >= 75 and coverage >= 70%.</summary>
    Strong = 1,

    /// <summary>Standard compatibility: score >= 50 and does not qualify as strong.</summary>
    Standard = 2,

    /// <summary>Other eligible candidate: score < 50.</summary>
    OtherEligible = 3,

    /// <summary>Eligible candidate with no calculable compatibility score (W = 0).</summary>
    Unscored = 4
}
