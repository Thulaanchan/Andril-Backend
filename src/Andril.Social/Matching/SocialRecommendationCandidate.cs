using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, privacy-protected container pairing candidate evidence with its compatibility score result and recommendation tier.
/// </summary>
public sealed class SocialRecommendationCandidate : IEquatable<SocialRecommendationCandidate>
{
    [JsonIgnore]
    public Guid CandidateProfileId => CandidateEvidence.ProfileId;

    [JsonIgnore]
    public SocialScoringProfileEvidence CandidateEvidence { get; }

    [JsonIgnore]
    public SocialCompatibilityScoreResult ScoreResult { get; }

    [JsonIgnore]
    public SocialRecommendationTier Tier { get; }

    [JsonIgnore]
    public int? ApproximateDistanceKm { get; }

    public SocialRecommendationCandidate(
        SocialScoringProfileEvidence candidateEvidence,
        SocialCompatibilityScoreResult scoreResult,
        SocialRecommendationTier tier,
        int? approximateDistanceKm = null)
    {
        CandidateEvidence = candidateEvidence ?? throw new ArgumentNullException(nameof(candidateEvidence));
        ScoreResult = scoreResult ?? throw new ArgumentNullException(nameof(scoreResult));
        Tier = tier;
        ApproximateDistanceKm = approximateDistanceKm;
    }

    public bool Equals(SocialRecommendationCandidate? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return CandidateProfileId == other.CandidateProfileId &&
               Tier == other.Tier &&
               ScoreResult.Equals(other.ScoreResult);
    }

    public override bool Equals(object? obj) => obj is SocialRecommendationCandidate other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(CandidateProfileId, Tier, ScoreResult);

    public override string ToString() => "SocialRecommendationCandidate [private]";
}
