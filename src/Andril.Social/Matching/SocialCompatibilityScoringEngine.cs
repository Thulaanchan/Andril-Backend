using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Andril.Social.Entities;
using Andril.Social.Personality;

namespace Andril.Social.Matching;

/// <summary>
/// Pure, deterministic domain scoring engine implementing the approved P-04 Six-Factor Social Compatibility
/// Scoring Engine and P-04-E Dynamic Weight Redistribution.
/// </summary>
public static class SocialCompatibilityScoringEngine
{
    public const string PolicyVersion = "P-04-1.0";

    /// <summary>
    /// Evaluates reciprocal compatibility between two user profile evidence bundles.
    /// Strictly guarantees symmetric outcomes: Evaluate(A, B, dist) == Evaluate(B, A, dist).
    /// </summary>
    /// <param name="profileA">Evidence bundle for user A.</param>
    /// <param name="profileB">Evidence bundle for user B.</param>
    /// <param name="approximateDistanceKm">Authorized approximate distance in km between users.</param>
    /// <returns>An immutable <see cref="SocialCompatibilityScoreResult"/>.</returns>
    public static SocialCompatibilityScoreResult Evaluate(
        SocialScoringProfileEvidence profileA,
        SocialScoringProfileEvidence profileB,
        int? approximateDistanceKm = null)
    {
        ArgumentNullException.ThrowIfNull(profileA);
        ArgumentNullException.ThrowIfNull(profileB);

        // 1. Mandatory Eligibility & Privacy Pre-Check
        var eligibilityResult = PreCheckEligibility(profileA, profileB, approximateDistanceKm);
        if (eligibilityResult is not null)
        {
            return eligibilityResult;
        }

        // 2. Evaluate each of the 6 factors
        var personality = EvaluatePersonalityFactor(profileA, profileB);
        var goals = EvaluateConnectionGoalsFactor(profileA, profileB);
        var languages = EvaluateCommonLanguagesFactor(profileA, profileB);
        var distance = EvaluateDistanceFactor(profileA, profileB, approximateDistanceKm);
        var agePreference = EvaluateAgePreferenceFactor(profileA, profileB);
        var sharedInterests = EvaluateSharedInterestsFactor(profileA, profileB);

        var rawFactors = new Dictionary<SocialCompatibilityFactor, (bool isAvailable, double? score, double baseWeight)>
        {
            [SocialCompatibilityFactor.Personality] = (personality.isAvailable, personality.score, SocialCompatibilityFactorWeights.PersonalityWeight),
            [SocialCompatibilityFactor.ConnectionGoals] = (goals.isAvailable, goals.score, SocialCompatibilityFactorWeights.ConnectionGoalsWeight),
            [SocialCompatibilityFactor.CommonLanguages] = (languages.isAvailable, languages.score, SocialCompatibilityFactorWeights.CommonLanguagesWeight),
            [SocialCompatibilityFactor.Distance] = (distance.isAvailable, distance.score, SocialCompatibilityFactorWeights.DistanceWeight),
            [SocialCompatibilityFactor.AgePreference] = (agePreference.isAvailable, agePreference.score, SocialCompatibilityFactorWeights.AgePreferenceWeight),
            [SocialCompatibilityFactor.SharedInterests] = (sharedInterests.isAvailable, sharedInterests.score, SocialCompatibilityFactorWeights.SharedInterestsWeight)
        };

        // 3. Dynamic Weight Redistribution (P-04-E)
        double totalAvailableWeight = 0.0;
        foreach (var kvp in rawFactors)
        {
            if (kvp.Value.isAvailable)
            {
                totalAvailableWeight += kvp.Value.baseWeight;
            }
        }

        // Build factor breakdowns
        var breakdowns = new Dictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>();
        double weightedNumerator = 0.0;

        foreach (var kvp in rawFactors)
        {
            var factor = kvp.Key;
            var (isAvailable, score, baseWeight) = kvp.Value;

            if (isAvailable && score.HasValue && totalAvailableWeight > 0.0)
            {
                double effectiveWeight = baseWeight / totalAvailableWeight;
                double weightedContribution = effectiveWeight * score.Value * 100.0;
                weightedNumerator += baseWeight * score.Value;

                breakdowns[factor] = SocialScoringFactorBreakdown.Available(
                    factor,
                    baseWeight,
                    score.Value,
                    effectiveWeight,
                    weightedContribution);
            }
            else
            {
                breakdowns[factor] = SocialScoringFactorBreakdown.Unavailable(factor, baseWeight);
            }
        }

        var readOnlyBreakdowns = new ReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>(breakdowns);

        // If W == 0: Return an unscored result, not zero compatibility
        if (totalAvailableWeight <= 0.0)
        {
            return SocialCompatibilityScoreResult.Unscored(readOnlyBreakdowns, PolicyVersion);
        }

        // C = 100 * sum(weight * factor_score) / W
        double calculatedScore = 100.0 * (weightedNumerator / totalAvailableWeight);
        calculatedScore = Math.Clamp(calculatedScore, 0.0, 100.0);

        // Coverage calculations
        double evidenceCoverage = totalAvailableWeight; // Since total base weight is 100, W is already %

        bool personalityAvailable = rawFactors[SocialCompatibilityFactor.Personality].isAvailable;
        double applicableCoverage = personalityAvailable
            ? evidenceCoverage
            : Math.Min(100.0, (totalAvailableWeight / SocialCompatibilityFactorWeights.NonPersonalityBaseWeight) * 100.0);

        return SocialCompatibilityScoreResult.Scored(
            calculatedScore,
            totalAvailableWeight,
            evidenceCoverage,
            applicableCoverage,
            readOnlyBreakdowns,
            PolicyVersion);
    }

    private static SocialCompatibilityScoreResult? PreCheckEligibility(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b,
        int? approximateDistanceKm)
    {
        // Self-matching check
        if (a.ProfileId == b.ProfileId)
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "SelfMatchingNotPermitted" });
        }

        // Underage check
        if (a.Age < 18 || b.Age < 18)
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "UnderageNotPermitted" });
        }

        // Explicit Social opt-in check
        if (!a.IsOptedIn || !b.IsOptedIn)
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "SocialDiscoveryNotOptedIn" });
        }

        // Active profile standing check
        if (!a.IsActive || !b.IsActive)
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "ProfileNotActive" });
        }

        // Discoverability permission check
        if (!a.IsDiscoverable || !b.IsDiscoverable)
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "ProfileHidden" });
        }

        // Bidirectional block clearance
        if (a.BlockedProfileIds.Contains(b.ProfileId) || b.BlockedProfileIds.Contains(a.ProfileId))
        {
            return SocialCompatibilityScoreResult.Ineligible(new[] { "BidirectionalBlockActive" });
        }

        // Authoritative evidence check
        if (!a.HasAuthoritativeEvidence || !b.HasAuthoritativeEvidence)
        {
            return SocialCompatibilityScoreResult.Indeterminate("MissingAuthoritativeEvidence");
        }

        // Hard Demographic Filter Reciprocal Evaluation (Age, Gender, Distance)
        var hardFilterResult = SocialCandidateFilterEvaluator.Evaluate(
            a.Age,
            a.DemographicGender,
            a.Preferences,
            b.Age,
            b.DemographicGender,
            b.Preferences,
            approximateDistanceKm);

        if (hardFilterResult.IsExcluded && hardFilterResult.ExclusionReasons.Count > 0)
        {
            return SocialCompatibilityScoreResult.Excluded(hardFilterResult.ExclusionReasons[0]);
        }

        if (hardFilterResult.IsIndeterminate && hardFilterResult.IndeterminateReasons.Count > 0)
        {
            return SocialCompatibilityScoreResult.Indeterminate(hardFilterResult.IndeterminateReasons[0].ToString());
        }

        return null;
    }

    private static (bool isAvailable, double? score) EvaluatePersonalityFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b)
    {
        var profileA = a.ToPersonalityEvaluationProfile();
        var profileB = b.ToPersonalityEvaluationProfile();

        if (profileA is null || profileB is null)
        {
            return (false, null);
        }

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(profileA, profileB);
        if (result.IsAvailable && result.Score.HasValue)
        {
            return (true, Math.Clamp(result.Score.Value, 0.0, 1.0));
        }

        return (false, null);
    }

    private static (bool isAvailable, double? score) EvaluateConnectionGoalsFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b)
    {
        if (a.ConfirmedConnectionGoals is null || a.ConfirmedConnectionGoals.Count == 0 ||
            b.ConfirmedConnectionGoals is null || b.ConfirmedConnectionGoals.Count == 0)
        {
            return (false, null);
        }

        int intersection = 0;
        foreach (var goal in a.ConfirmedConnectionGoals)
        {
            if (b.ConfirmedConnectionGoals.Contains(goal))
            {
                intersection++;
            }
        }

        var union = new HashSet<string>(a.ConfirmedConnectionGoals, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(b.ConfirmedConnectionGoals);

        if (union.Count == 0)
        {
            return (false, null);
        }

        double jaccard = (double)intersection / union.Count;
        return (true, Math.Clamp(jaccard, 0.0, 1.0));
    }

    private static (bool isAvailable, double? score) EvaluateCommonLanguagesFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b)
    {
        if (a.ConfirmedLanguages is null || a.ConfirmedLanguages.Count == 0 ||
            b.ConfirmedLanguages is null || b.ConfirmedLanguages.Count == 0)
        {
            return (false, null);
        }

        bool hasShared = false;
        foreach (var lang in a.ConfirmedLanguages)
        {
            if (b.ConfirmedLanguages.Contains(lang))
            {
                hasShared = true;
                break;
            }
        }

        return (true, hasShared ? 1.0 : 0.0);
    }

    private static (bool isAvailable, double? score) EvaluateDistanceFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b,
        int? approximateDistanceKm)
    {
        if (!approximateDistanceKm.HasValue)
        {
            return (false, null);
        }

        int dist = approximateDistanceKm.Value;

        static double DirectionalDistance(int distance, int min, int max)
        {
            if (min == max)
            {
                return distance == min ? 1.0 : 0.0;
            }

            double score = 1.0 - (double)(distance - min) / (max - min);
            return Math.Clamp(score, 0.0, 1.0);
        }

        double scoreA = DirectionalDistance(dist, a.Preferences.MinDistanceKm, a.Preferences.MaxDistanceKm);
        double scoreB = DirectionalDistance(dist, b.Preferences.MinDistanceKm, b.Preferences.MaxDistanceKm);

        double average = (scoreA + scoreB) / 2.0;
        return (true, Math.Clamp(average, 0.0, 1.0));
    }

    private static (bool isAvailable, double? score) EvaluateAgePreferenceFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b)
    {
        double? termA = null;
        if (a.IdealAgeBand is not null && a.IdealAgeBand.IsWithinAcceptedRange(a.Preferences.MinAge, a.Preferences.MaxAge))
        {
            termA = a.IdealAgeBand.EvaluateCandidateAge(b.Age, a.Preferences.MinAge, a.Preferences.MaxAge);
        }

        double? termB = null;
        if (b.IdealAgeBand is not null && b.IdealAgeBand.IsWithinAcceptedRange(b.Preferences.MinAge, b.Preferences.MaxAge))
        {
            termB = b.IdealAgeBand.EvaluateCandidateAge(a.Age, b.Preferences.MinAge, b.Preferences.MaxAge);
        }

        if (termA.HasValue && termB.HasValue)
        {
            return (true, Math.Clamp((termA.Value + termB.Value) / 2.0, 0.0, 1.0));
        }

        if (termA.HasValue)
        {
            return (true, Math.Clamp(termA.Value, 0.0, 1.0));
        }

        if (termB.HasValue)
        {
            return (true, Math.Clamp(termB.Value, 0.0, 1.0));
        }

        return (false, null);
    }

    private static (bool isAvailable, double? score) EvaluateSharedInterestsFactor(
        SocialScoringProfileEvidence a,
        SocialScoringProfileEvidence b)
    {
        if (a.ConfirmedSocialInterestIds is null || a.ConfirmedSocialInterestIds.Count == 0 ||
            b.ConfirmedSocialInterestIds is null || b.ConfirmedSocialInterestIds.Count == 0)
        {
            return (false, null);
        }

        int intersection = 0;
        foreach (var id in a.ConfirmedSocialInterestIds)
        {
            if (b.ConfirmedSocialInterestIds.Contains(id))
            {
                intersection++;
            }
        }

        var union = new HashSet<Guid>(a.ConfirmedSocialInterestIds);
        union.UnionWith(b.ConfirmedSocialInterestIds);

        if (union.Count == 0)
        {
            return (false, null);
        }

        double jaccard = (double)intersection / union.Count;
        return (true, Math.Clamp(jaccard, 0.0, 1.0));
    }
}
