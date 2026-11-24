using System;
using System.Collections.Generic;
using System.Linq;

namespace Andril.Social.Matching;

/// <summary>
/// Deterministic recommendation ranking policy implementing approved P-04 / Task 151 rules:
/// - Personality-based and preference-based Strong tier qualification (Score >= 75, Coverage >= 70%).
/// - Standard tier (Score >= 50).
/// - Other eligible tier (Score < 50).
/// - Unscored tier (W = 0).
/// - Stable, privacy-safe multi-factor sorting and deterministic tie-breaking.
/// </summary>
public static class SocialRecommendationRankingPolicy
{
    public const double StrongScoreThreshold = 75.0;
    public const double StandardScoreThreshold = 50.0;
    public const double StrongCoverageThreshold = 70.0;

    /// <summary>
    /// Determines the recommendation tier for a compatibility score result.
    /// Excluded or indeterminate results cannot receive a recommendation tier.
    /// </summary>
    public static SocialRecommendationTier DetermineTier(SocialCompatibilityScoreResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == SocialCompatibilityEvaluationStatus.Unscored)
        {
            return SocialRecommendationTier.Unscored;
        }

        if (!result.IsScored || !result.CompatibilityScore.HasValue)
        {
            throw new ArgumentException(
                $"Cannot determine recommendation tier for non-scored result with status '{result.Status}'.",
                nameof(result));
        }

        double score = result.CompatibilityScore.Value;
        double coverage = result.ApplicableCoveragePercentage;

        if (score >= StrongScoreThreshold && coverage >= StrongCoverageThreshold)
        {
            return SocialRecommendationTier.Strong;
        }

        if (score >= StandardScoreThreshold)
        {
            return SocialRecommendationTier.Standard;
        }

        return SocialRecommendationTier.OtherEligible;
    }

    /// <summary>
    /// Evaluates and ranks a collection of candidate profiles against a discovering caller.
    /// Ineligible, excluded, and indeterminate candidates are strictly omitted before ranking.
    /// </summary>
    /// <param name="caller">The discovering caller's evidence bundle.</param>
    /// <param name="candidates">The collection of candidate evidence bundles with approximate distances.</param>
    /// <param name="rotationOffset">Deterministic offset applied for exploratory candidate rotation.</param>
    /// <returns>A defensively ranked, immutable list of <see cref="SocialRecommendationCandidate"/> instances.</returns>
    public static IReadOnlyList<SocialRecommendationCandidate> RankCandidates(
        SocialScoringProfileEvidence caller,
        IEnumerable<(SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)> candidates,
        int rotationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(candidates);

        if (rotationOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationOffset), rotationOffset, "Rotation offset cannot be negative.");
        }

        var rankedList = new List<SocialRecommendationCandidate>();

        foreach (var (candidate, distanceKm) in candidates)
        {
            if (candidate is null) continue;

            var scoreResult = SocialCompatibilityScoringEngine.Evaluate(caller, candidate, distanceKm);

            // Ineligible, excluded, or indeterminate candidates are strictly omitted
            if (scoreResult.Status != SocialCompatibilityEvaluationStatus.Scored &&
                scoreResult.Status != SocialCompatibilityEvaluationStatus.Unscored)
            {
                continue;
            }

            var tier = DetermineTier(scoreResult);
            rankedList.Add(new SocialRecommendationCandidate(candidate, scoreResult, tier, distanceKm));
        }

        // Multi-level deterministic sorting:
        // 1. Tier (Strong -> Standard -> OtherEligible -> Unscored)
        // 2. Compatibility Score (Descending unrounded)
        // 3. Applicable Coverage (Descending)
        var orderedCandidates = rankedList
            .OrderBy(c => (int)c.Tier)
            .ThenByDescending(c => c.ScoreResult.CompatibilityScore ?? -1.0)
            .ThenByDescending(c => c.ScoreResult.ApplicableCoveragePercentage)
            .ToList();

        // Apply rotation EXCLUSIVELY within exact ranking tie groups (Tier, Score, and Coverage equality).
        // Different tiers, scores, or coverage values are never rotated across boundaries.
        var result = new List<SocialRecommendationCandidate>(orderedCandidates.Count);
        int i = 0;
        while (i < orderedCandidates.Count)
        {
            int j = i + 1;
            while (j < orderedCandidates.Count && AreExactTie(orderedCandidates[i], orderedCandidates[j]))
            {
                j++;
            }

            var tieGroup = orderedCandidates.GetRange(i, j - i);
            tieGroup.Sort((a, b) => a.CandidateProfileId.CompareTo(b.CandidateProfileId));

            if (tieGroup.Count > 1 && rotationOffset > 0)
            {
                int effectiveOffset = rotationOffset % tieGroup.Count;
                if (effectiveOffset > 0)
                {
                    result.AddRange(tieGroup.Skip(effectiveOffset));
                    result.AddRange(tieGroup.Take(effectiveOffset));
                }
                else
                {
                    result.AddRange(tieGroup);
                }
            }
            else
            {
                result.AddRange(tieGroup);
            }

            i = j;
        }

        return result.AsReadOnly();
    }

    private static bool AreExactTie(SocialRecommendationCandidate a, SocialRecommendationCandidate b)
    {
        if (a.Tier != b.Tier)
        {
            return false;
        }

        var scoreA = a.ScoreResult.CompatibilityScore;
        var scoreB = b.ScoreResult.CompatibilityScore;

        if (scoreA.HasValue != scoreB.HasValue)
        {
            return false;
        }

        if (scoreA.HasValue && scoreB.HasValue && Math.Abs(scoreA.Value - scoreB.Value) > 1e-9)
        {
            return false;
        }

        return Math.Abs(a.ScoreResult.ApplicableCoveragePercentage - b.ScoreResult.ApplicableCoveragePercentage) < 1e-9;
    }
}
