using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Detailed breakdown of an individual factor's evaluation within the Social compatibility engine (P-04).
/// </summary>
public sealed class SocialScoringFactorBreakdown : IEquatable<SocialScoringFactorBreakdown>
{
    [JsonIgnore]
    public SocialCompatibilityFactor Factor { get; }

    [JsonIgnore]
    public double BaseWeight { get; }

    [JsonIgnore]
    public bool IsAvailable { get; }

    [JsonIgnore]
    public double? NormalizedScore { get; }

    [JsonIgnore]
    public double EffectiveWeight { get; }

    [JsonIgnore]
    public double? WeightedContribution { get; }

    public SocialScoringFactorBreakdown(
        SocialCompatibilityFactor factor,
        double baseWeight,
        bool isAvailable,
        double? normalizedScore,
        double effectiveWeight,
        double? weightedContribution)
    {
        if (normalizedScore.HasValue && (normalizedScore.Value < 0.0 || normalizedScore.Value > 1.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizedScore),
                normalizedScore.Value,
                "Normalized factor score must be between 0.0 and 1.0.");
        }

        Factor = factor;
        BaseWeight = baseWeight;
        IsAvailable = isAvailable;
        NormalizedScore = normalizedScore;
        EffectiveWeight = effectiveWeight;
        WeightedContribution = weightedContribution;
    }

    public static SocialScoringFactorBreakdown Available(
        SocialCompatibilityFactor factor,
        double baseWeight,
        double normalizedScore,
        double effectiveWeight,
        double weightedContribution) =>
        new(factor, baseWeight, true, normalizedScore, effectiveWeight, weightedContribution);

    public static SocialScoringFactorBreakdown Unavailable(
        SocialCompatibilityFactor factor,
        double baseWeight) =>
        new(factor, baseWeight, false, null, 0.0, null);

    public bool Equals(SocialScoringFactorBreakdown? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Factor == other.Factor &&
               Math.Abs(BaseWeight - other.BaseWeight) < 1e-9 &&
               IsAvailable == other.IsAvailable &&
               Nullable.Equals(NormalizedScore, other.NormalizedScore) &&
               Math.Abs(EffectiveWeight - other.EffectiveWeight) < 1e-9 &&
               Nullable.Equals(WeightedContribution, other.WeightedContribution);
    }

    public override bool Equals(object? obj) => obj is SocialScoringFactorBreakdown other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Factor, BaseWeight, IsAvailable, NormalizedScore, EffectiveWeight, WeightedContribution);

    public override string ToString() => "SocialScoringFactorBreakdown [private]";
}
