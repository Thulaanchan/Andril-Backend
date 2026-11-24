using System;
using System.Collections.Generic;

namespace Andril.Social.Matching;

/// <summary>
/// The six approved compatibility factors for the P-04 Social matching engine.
/// </summary>
public enum SocialCompatibilityFactor
{
    /// <summary>Reciprocal personality alignment (Weight: 35).</summary>
    Personality = 1,

    /// <summary>Jaccard similarity of confirmed connection goals (Weight: 20).</summary>
    ConnectionGoals = 2,

    /// <summary>Confirmed shared communication languages (Weight: 15).</summary>
    CommonLanguages = 3,

    /// <summary>Reciprocal accepted distance range compatibility (Weight: 10).</summary>
    Distance = 4,

    /// <summary>Reciprocal ideal age band piecewise interpolation (Weight: 10).</summary>
    AgePreference = 5,

    /// <summary>Jaccard similarity of confirmed canonical Social interest IDs (Weight: 10).</summary>
    SharedInterests = 6
}

/// <summary>
/// Base weights and constants for the six Social compatibility factors.
/// </summary>
public static class SocialCompatibilityFactorWeights
{
    public const double PersonalityWeight = 35.0;
    public const double ConnectionGoalsWeight = 20.0;
    public const double CommonLanguagesWeight = 15.0;
    public const double DistanceWeight = 10.0;
    public const double AgePreferenceWeight = 10.0;
    public const double SharedInterestsWeight = 10.0;

    public const double TotalBaseWeight = 100.0;
    public const double NonPersonalityBaseWeight = 65.0;

    public static readonly IReadOnlyDictionary<SocialCompatibilityFactor, double> BaseWeights =
        new Dictionary<SocialCompatibilityFactor, double>
        {
            [SocialCompatibilityFactor.Personality] = PersonalityWeight,
            [SocialCompatibilityFactor.ConnectionGoals] = ConnectionGoalsWeight,
            [SocialCompatibilityFactor.CommonLanguages] = CommonLanguagesWeight,
            [SocialCompatibilityFactor.Distance] = DistanceWeight,
            [SocialCompatibilityFactor.AgePreference] = AgePreferenceWeight,
            [SocialCompatibilityFactor.SharedInterests] = SharedInterestsWeight
        };

    public static double GetBaseWeight(SocialCompatibilityFactor factor) =>
        BaseWeights.TryGetValue(factor, out var weight)
            ? weight
            : throw new ArgumentOutOfRangeException(nameof(factor), factor, "Unknown compatibility factor.");
}
