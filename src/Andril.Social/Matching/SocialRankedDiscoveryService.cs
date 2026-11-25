using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Andril.Social.Personality;

namespace Andril.Social.Matching;

/// <summary>
/// Authoritative candidate item bundling profile scoring evidence, revision snapshots, display name, and approximate distance.
/// </summary>
public sealed class SocialAuthoritativeCandidateItem : IEquatable<SocialAuthoritativeCandidateItem>
{
    [JsonIgnore]
    public SocialScoringProfileEvidence Evidence { get; }

    [JsonIgnore]
    public SocialEvidenceRevisionSnapshot CachedRevision { get; }

    [JsonIgnore]
    public SocialEvidenceRevisionSnapshot CommittedRevision { get; }

    [JsonIgnore]
    public string DisplayName { get; }

    [JsonIgnore]
    public int? ApproximateDistanceKm { get; }

    public SocialAuthoritativeCandidateItem(
        SocialScoringProfileEvidence evidence,
        SocialEvidenceRevisionSnapshot cachedRevision,
        SocialEvidenceRevisionSnapshot committedRevision,
        string displayName,
        int? approximateDistanceKm = null)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        CachedRevision = cachedRevision ?? throw new ArgumentNullException(nameof(cachedRevision));
        CommittedRevision = committedRevision ?? throw new ArgumentNullException(nameof(committedRevision));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        if (approximateDistanceKm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(approximateDistanceKm), approximateDistanceKm, "Approximate distance cannot be negative.");
        }

        ApproximateDistanceKm = approximateDistanceKm;
    }

    public bool Equals(SocialAuthoritativeCandidateItem? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Evidence.Equals(other.Evidence) &&
               CachedRevision.Equals(other.CachedRevision) &&
               CommittedRevision.Equals(other.CommittedRevision) &&
               string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
               ApproximateDistanceKm == other.ApproximateDistanceKm;
    }

    public override bool Equals(object? obj) => obj is SocialAuthoritativeCandidateItem other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Evidence, CachedRevision, CommittedRevision, DisplayName, ApproximateDistanceKm);

    public override string ToString() => "SocialAuthoritativeCandidateItem [private]";
}

/// <summary>
/// Deterministic, eligibility-gated recommendation ranking service.
/// Composes discovery eligibility pre-filtering, reciprocal hard-filter evaluation,
/// multi-factor compatibility scoring, and deterministic recommendation tier ranking.
/// </summary>
public static class SocialRankedDiscoveryService
{
    public const int DefaultMaxCandidateBatchSize = 100;

    /// <summary>
    /// Performs strict, authoritative ranked discovery with freshness verification, bounded candidate batching,
    /// and privacy-safe projection to <see cref="SocialExploreCandidateCard"/> snapshots (Task 168).
    /// </summary>
    /// <param name="caller">The discovering caller's authoritative evidence bundle.</param>
    /// <param name="callerCachedRevision">The caller's cached evidence revision snapshot.</param>
    /// <param name="callerCommittedRevision">The caller's latest committed evidence revision snapshot.</param>
    /// <param name="candidates">The collection of authoritative candidate items.</param>
    /// <param name="rotationOffset">Deterministic offset applied for exploratory candidate rotation within ranking tie groups.</param>
    /// <param name="maxCandidateBatchSize">Server-supplied technical limit bounding the candidate processing batch.</param>
    /// <returns>A defensively ranked, immutable list of eligible, privacy-safe <see cref="SocialExploreCandidateCard"/> instances.</returns>
    public static IReadOnlyList<SocialExploreCandidateCard> DiscoverAuthoritativeRankedCards(
        SocialScoringProfileEvidence caller,
        SocialEvidenceRevisionSnapshot callerCachedRevision,
        SocialEvidenceRevisionSnapshot callerCommittedRevision,
        IEnumerable<SocialAuthoritativeCandidateItem> candidates,
        int rotationOffset = 0,
        int maxCandidateBatchSize = DefaultMaxCandidateBatchSize)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(callerCachedRevision);
        ArgumentNullException.ThrowIfNull(callerCommittedRevision);
        ArgumentNullException.ThrowIfNull(candidates);

        if (rotationOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationOffset),
                rotationOffset,
                "Rotation offset cannot be negative.");
        }

        if (maxCandidateBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCandidateBatchSize),
                maxCandidateBatchSize,
                "Maximum candidate batch size must be greater than zero.");
        }

        // 1. Caller identity consistency and authoritative evidence check
        if (caller.ProfileId == Guid.Empty ||
            callerCachedRevision.ProfileId != caller.ProfileId ||
            callerCommittedRevision.ProfileId != caller.ProfileId)
        {
            return Array.Empty<SocialExploreCandidateCard>();
        }

        if (!caller.HasAuthoritativeEvidence || caller.Age < 18 || !caller.IsOptedIn || !caller.IsActive)
        {
            return Array.Empty<SocialExploreCandidateCard>();
        }

        // 2. Matching policy version check
        if (!string.Equals(callerCommittedRevision.PolicyVersion, SocialCompatibilityScoringEngine.PolicyVersion, StringComparison.Ordinal))
        {
            return Array.Empty<SocialExploreCandidateCard>();
        }

        // 3. Caller evidence currency check (fails closed on stale revisions, restriction, or deactivation)
        var callerCurrency = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(callerCachedRevision, callerCommittedRevision);
        if (!callerCurrency.IsCurrent)
        {
            return Array.Empty<SocialExploreCandidateCard>();
        }

        var eligibleCandidatesWithCards = new List<(SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm, SocialCandidateEvaluationResult EvaluationResult, string DisplayName)>();
        var candidateEvidenceByProfileId = new Dictionary<Guid, SocialAuthoritativeCandidateItem>();
        var contradictoryProfileIds = new HashSet<Guid>();
        var processedCount = 0;

        foreach (var item in candidates)
        {
            if (processedCount >= maxCandidateBatchSize)
            {
                break;
            }

            processedCount++;

            if (item is null)
            {
                continue;
            }

            var candidate = item.Evidence;
            if (candidate is null || item.CachedRevision is null || item.CommittedRevision is null)
            {
                continue;
            }

            // 4. Reject empty, self-discovery, or contradictory candidate identities
            if (candidate.ProfileId == Guid.Empty ||
                candidate.ProfileId == caller.ProfileId ||
                item.CachedRevision.ProfileId != candidate.ProfileId ||
                item.CommittedRevision.ProfileId != candidate.ProfileId)
            {
                continue;
            }

            if (contradictoryProfileIds.Contains(candidate.ProfileId))
            {
                continue;
            }

            if (candidateEvidenceByProfileId.TryGetValue(candidate.ProfileId, out var existingItem))
            {
                if (HasContradictoryCandidateItem(item, existingItem))
                {
                    contradictoryProfileIds.Add(candidate.ProfileId);
                    eligibleCandidatesWithCards.RemoveAll(x => x.Candidate.ProfileId == candidate.ProfileId);
                }
                continue;
            }

            candidateEvidenceByProfileId[candidate.ProfileId] = item;

            if (string.IsNullOrWhiteSpace(item.DisplayName) || (item.ApproximateDistanceKm.HasValue && item.ApproximateDistanceKm.Value < 0))
            {
                continue;
            }

            // 5. Candidate authoritative provenance and eligibility checks
            if (!candidate.HasAuthoritativeEvidence ||
                candidate.Age < 18 ||
                !candidate.IsOptedIn ||
                !candidate.IsActive ||
                !candidate.IsDiscoverable)
            {
                continue;
            }

            // 6. Candidate matching policy version check
            if (!string.Equals(item.CommittedRevision.PolicyVersion, SocialCompatibilityScoringEngine.PolicyVersion, StringComparison.Ordinal))
            {
                continue;
            }

            // 7. Candidate evidence currency check
            var candidateCurrency = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(item.CachedRevision, item.CommittedRevision);
            if (!candidateCurrency.IsCurrent)
            {
                continue;
            }

            // 8. Bidirectional blocking check
            var isBlockedByCaller = caller.BlockedProfileIds.Contains(candidate.ProfileId);
            var isCallerBlockedByCandidate = candidate.BlockedProfileIds.Contains(caller.ProfileId);
            if (isBlockedByCaller || isCallerBlockedByCandidate)
            {
                continue;
            }

            // 9. Stage 1: Discovery eligibility pre-filter
            var callerPreFilterEvidence = new SocialDiscoveryCallerEvidence(
                caller.ProfileId,
                caller.Age,
                caller.IsOptedIn,
                caller.IsActive,
                caller.HasAuthoritativeEvidence);

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

            // 10. Stage 2: Reciprocal hard demographic filters (rejects Excluded and Indeterminate)
            var filterInput = new SocialCandidateEvaluationInput(
                caller.Age,
                caller.DemographicGender,
                caller.Preferences,
                candidate.Age,
                candidate.DemographicGender,
                candidate.Preferences,
                item.ApproximateDistanceKm);

            var filterResult = SocialCandidateFilterEvaluator.Evaluate(filterInput);
            if (filterResult.Status != SocialCandidateMatchStatus.Eligible)
            {
                continue;
            }

            eligibleCandidatesWithCards.Add((candidate, item.ApproximateDistanceKm, filterResult, item.DisplayName));
        }

        if (eligibleCandidatesWithCards.Count == 0)
        {
            return Array.Empty<SocialExploreCandidateCard>();
        }

        // 11. Multi-factor compatibility scoring & tier ranking preserving priority
        var rankingInput = eligibleCandidatesWithCards.Select(x => (x.Candidate, x.ApproximateDistanceKm));
        var rankedCandidates = SocialRecommendationRankingPolicy.RankCandidates(caller, rankingInput, rotationOffset);

        // 12. Privacy-safe projection to approved SocialExploreCandidateCard
        var cardLookup = eligibleCandidatesWithCards.ToDictionary(
            x => x.Candidate.ProfileId,
            x => (x.DisplayName, x.ApproximateDistanceKm, x.EvaluationResult));

        var projectedCards = new List<SocialExploreCandidateCard>(rankedCandidates.Count);
        foreach (var ranked in rankedCandidates)
        {
            if (cardLookup.TryGetValue(ranked.CandidateProfileId, out var info))
            {
                var card = SocialExploreCandidateCard.Create(
                    ranked.CandidateProfileId,
                    info.DisplayName,
                    ranked.CandidateEvidence.Age,
                    info.ApproximateDistanceKm,
                    info.EvaluationResult);

                projectedCards.Add(card);
            }
        }

        return projectedCards.AsReadOnly();
    }
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
        var candidateEvidenceByProfileId = new Dictionary<Guid, (SocialScoringProfileEvidence Evidence, int? DistanceKm)>();
        var contradictoryProfileIds = new HashSet<Guid>();

        foreach (var (candidate, distanceKm) in candidates)
        {
            if (candidate is null) continue;

            if (contradictoryProfileIds.Contains(candidate.ProfileId))
            {
                continue;
            }

            if (candidateEvidenceByProfileId.TryGetValue(candidate.ProfileId, out var existing))
            {
                if (HasContradictoryEvidence(candidate, existing.Evidence) || distanceKm != existing.DistanceKm)
                {
                    contradictoryProfileIds.Add(candidate.ProfileId);
                    eligibleCandidates.RemoveAll(x => x.Candidate.ProfileId == candidate.ProfileId);
                }
                continue;
            }

            candidateEvidenceByProfileId[candidate.ProfileId] = (candidate, distanceKm);

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

    private static bool HasContradictoryCandidateItem(SocialAuthoritativeCandidateItem a, SocialAuthoritativeCandidateItem b)
    {
        if (a.ApproximateDistanceKm != b.ApproximateDistanceKm) return true;
        if (!a.CachedRevision.Equals(b.CachedRevision)) return true;
        if (!a.CommittedRevision.Equals(b.CommittedRevision)) return true;
        if (!AreDisplayNamesSemanticallyEquivalent(a.DisplayName, b.DisplayName)) return true;
        return HasContradictoryEvidence(a.Evidence, b.Evidence);
    }

    private static bool AreDisplayNamesSemanticallyEquivalent(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        var baseA = a.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '_', '-');
        var baseB = b.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '_', '-');

        if (!string.IsNullOrWhiteSpace(baseA) && string.Equals(baseA, baseB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HasContradictoryEvidence(SocialScoringProfileEvidence a, SocialScoringProfileEvidence b)
    {
        if (!a.Equals(b)) return true;

        if (!a.BlockedProfileIds.SetEquals(b.BlockedProfileIds)) return true;

        if (!AreNullableSetsEqual(a.ConfirmedLanguages, b.ConfirmedLanguages)) return true;
        if (!AreNullableSetsEqual(a.ConfirmedConnectionGoals, b.ConfirmedConnectionGoals)) return true;
        if (!AreNullableGuidSetsEqual(a.ConfirmedSocialInterestIds, b.ConfirmedSocialInterestIds)) return true;

        if (!AreAnswersEqual(a.QuestionnaireAnswers, b.QuestionnaireAnswers)) return true;

        if (!string.Equals(a.QuestionnaireVersion, b.QuestionnaireVersion, StringComparison.Ordinal) ||
            !string.Equals(a.QuestionnaireRevision, b.QuestionnaireRevision, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool AreNullableSetsEqual(IReadOnlySet<string>? setA, IReadOnlySet<string>? setB)
    {
        if (setA is null && setB is null) return true;
        if (setA is null || setB is null) return false;
        return setA.SetEquals(setB);
    }

    private static bool AreNullableGuidSetsEqual(IReadOnlySet<Guid>? setA, IReadOnlySet<Guid>? setB)
    {
        if (setA is null && setB is null) return true;
        if (setA is null || setB is null) return false;
        return setA.SetEquals(setB);
    }

    private static bool AreAnswersEqual(IReadOnlyList<QuestionnaireAnswer>? listA, IReadOnlyList<QuestionnaireAnswer>? listB)
    {
        if (listA is null && listB is null) return true;
        if (listA is null || listB is null) return false;
        if (listA.Count != listB.Count) return false;
        for (var i = 0; i < listA.Count; i++)
        {
            var a = listA[i];
            var b = listB[i];
            if (a is null && b is null) continue;
            if (a is null || b is null) return false;
            if (!string.Equals(a.QuestionId, b.QuestionId, StringComparison.Ordinal) ||
                !string.Equals(a.OptionId, b.OptionId, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
