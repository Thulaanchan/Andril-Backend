using System;
using System.Collections.Generic;

namespace Andril.Social.Matching;

/// <summary>
/// Deterministic, eligibility-gated recommendation ranking service.
/// Composes discovery eligibility pre-filtering, reciprocal hard-filter evaluation,
/// multi-factor compatibility scoring, and deterministic recommendation tier ranking.
/// </summary>
public static class SocialRankedDiscoveryService
{
    /// <summary>
    /// Discovers, filters, scores, and ranks candidate profiles against a discovering caller.
    /// Ineligible, excluded, blocked, or indeterminate candidates are strictly omitted before ranking.
    /// </summary>
    /// <param name="caller">The discovering caller's authoritative evidence bundle.</param>
    /// <param name="candidates">The collection of candidate evidence bundles with approximate distances.</param>
    /// <param name="rotationOffset">Deterministic offset applied for exploratory candidate rotation within ranking tie groups.</param>
    /// <returns>A defensively ranked, immutable list of eligible <see cref="SocialRecommendationCandidate"/> instances.</returns>
    public static IReadOnlyList<SocialRecommendationCandidate> DiscoverRankedCandidates(
        SocialScoringProfileEvidence caller,
        IEnumerable<(SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)> candidates,
        int rotationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(candidates);

        if (rotationOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationOffset),
                rotationOffset,
                "Rotation offset cannot be negative.");
        }

        // 1. Caller baseline eligibility check (fails closed if caller is ineligible or lacks authoritative evidence)
        var callerPreFilterEvidence = new SocialDiscoveryCallerEvidence(
            caller.ProfileId,
            caller.Age,
            caller.IsOptedIn,
            caller.IsActive,
            caller.HasAuthoritativeEvidence);

        if (caller.Age < 18 || !caller.IsOptedIn || !caller.IsActive || !caller.HasAuthoritativeEvidence)
        {
            return Array.Empty<SocialRecommendationCandidate>();
        }

        var eligibleCandidates = new List<(SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)>();

        foreach (var (candidate, distanceKm) in candidates)
        {
            if (candidate is null) continue;

            // 2. Self-discovery and mutual blocking check
            if (caller.ProfileId == candidate.ProfileId)
            {
                continue;
            }

            var isBlockedByCaller = caller.BlockedProfileIds.Contains(candidate.ProfileId);
            var isCallerBlockedByCandidate = candidate.BlockedProfileIds.Contains(caller.ProfileId);

            if (isBlockedByCaller || isCallerBlockedByCandidate)
            {
                continue;
            }

            // 3. Stage 1: Eligibility and privacy pre-filter
            var candidatePreFilterEvidence = new SocialDiscoveryCandidateEvidence(
                candidate.ProfileId,
                candidate.Age,
                candidate.IsOptedIn,
                candidate.IsActive,
                candidate.IsDiscoverable,
                isBlockedByCaller,
                isCallerBlockedByCandidate,
                candidate.HasAuthoritativeEvidence);

            var preFilterResult = SocialDiscoveryEligibilityPreFilter.Evaluate(
                callerPreFilterEvidence,
                candidatePreFilterEvidence);

            if (!preFilterResult.IsEligible)
            {
                continue;
            }

            // 4. Stage 2: Reciprocal hard demographic filters (age bands and show-me preferences)
            var filterInput = new SocialCandidateEvaluationInput(
                caller.Age,
                caller.DemographicGender,
                caller.Preferences,
                candidate.Age,
                candidate.DemographicGender,
                candidate.Preferences,
                distanceKm);

            var filterResult = SocialCandidateFilterEvaluator.Evaluate(filterInput);

            // Hard filter exclusions strictly omit candidate (age mismatch, gender mismatch, distance mismatch)
            if (filterResult.IsExcluded)
            {
                continue;
            }

            eligibleCandidates.Add((candidate, distanceKm));
        }

        // 5. Stage 3 & 4: Multi-factor compatibility scoring and deterministic ranking
        return SocialRecommendationRankingPolicy.RankCandidates(caller, eligibleCandidates, rotationOffset);
    }
}
