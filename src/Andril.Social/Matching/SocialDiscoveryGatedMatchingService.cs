using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable bundled offline candidate item pairing privacy/eligibility evidence with matching criteria.
/// </summary>
public sealed class SocialGatedCandidateItem : IEquatable<SocialGatedCandidateItem>
{
    /// <summary>
    /// Gets the authoritative discovery eligibility and privacy evidence for the candidate.
    /// </summary>
    [JsonIgnore]
    public SocialDiscoveryCandidateEvidence Evidence { get; }

    /// <summary>
    /// Gets the candidate matching input criteria (display name, age, gender, explore preferences, approximate distance).
    /// </summary>
    [JsonIgnore]
    public SocialCandidateMatchingInput MatchingInput { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialGatedCandidateItem"/> class.
    /// </summary>
    /// <param name="evidence">The authoritative eligibility and privacy evidence. Must not be null.</param>
    /// <param name="matchingInput">The matching input demographic and preference criteria. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="evidence"/> or <paramref name="matchingInput"/> is null.</exception>
    public SocialGatedCandidateItem(
        SocialDiscoveryCandidateEvidence evidence,
        SocialCandidateMatchingInput matchingInput)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        MatchingInput = matchingInput ?? throw new ArgumentNullException(nameof(matchingInput));
    }

    public bool Equals(SocialGatedCandidateItem? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Evidence.Equals(other.Evidence) && MatchingInput.Equals(other.MatchingInput);
    }

    public override bool Equals(object? obj) => obj is SocialGatedCandidateItem other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Evidence, MatchingInput);

    public override string ToString() => "SocialGatedCandidateItem [private]";
}

/// <summary>
/// Pure, deterministic offline domain pipeline composing discovery eligibility pre-filtering
/// and candidate hard-filter matching evaluation for Social Explore (SOC-02 / CF-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-Stage Gated Architecture:</b><br />
/// 1. <b>Stage 1 (Eligibility &amp; Privacy Pre-Filter):</b> Evaluates authoritative caller and candidate evidence
/// (<see cref="SocialDiscoveryEligibilityPreFilter"/>). Ineligible, inactive, underage, hidden, blocked, or missing-evidence
/// candidates are strictly omitted before any matching logic is executed.
/// 2. <b>Stage 2 (Hard-Filter Reciprocal Matching):</b> Evaluates mutually approved age, gender, distance, and preference
/// criteria (<see cref="SocialCandidateFilterEvaluator"/>) only for candidates that passed Stage 1.
/// </para>
/// <para>
/// <b>Strict Security and Privacy Invariants:</b><br />
/// <list type="bullet">
/// <item><description>No candidate excluded at Stage 1 or Stage 2 ever reaches result snapshots.</description></item>
/// <item><description>No compatibility scores, ranking weights, percentages, or AI recommendations are computed.</description></item>
/// <item><description>No database queries, external API calls, or identity authorization claims are performed.</description></item>
/// <item><description>All private profile identifiers and sensitive properties are concealed from serialization and public logging.</description></item>
/// <item><description>This service must not be registered in production dependency injection or exposed via public API controllers.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialDiscoveryGatedMatchingService
{
    /// <summary>
    /// Evaluates caller and candidate evidence through the two-stage gated pipeline, constructing
    /// rotated <see cref="SocialExploreCandidateCard"/> snapshots for candidates meeting all criteria.
    /// </summary>
    /// <param name="callerEvidence">The authoritative discovery eligibility evidence for the discovering caller.</param>
    /// <param name="callerGender">The caller's demographic gender string, or <see langword="null"/>.</param>
    /// <param name="callerPreferences">The caller's validated Social Explore preferences.</param>
    /// <param name="candidates">The batch of bundled candidate items to evaluate.</param>
    /// <param name="rotationOffset">The non-negative rotation offset applied to eligible candidates.</param>
    /// <returns>A defensive, immutable read-only list of rotated <see cref="SocialExploreCandidateCard"/> snapshots.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callerEvidence"/>, <paramref name="callerPreferences"/>, or <paramref name="candidates"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rotationOffset"/> is negative.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="candidates"/> contains a null element.</exception>
    public static IReadOnlyList<SocialExploreCandidateCard> BuildGatedSnapshots(
        SocialDiscoveryCallerEvidence callerEvidence,
        string? callerGender,
        SocialExplorePreferences callerPreferences,
        IReadOnlyList<SocialGatedCandidateItem> candidates,
        int rotationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(callerEvidence);
        ArgumentNullException.ThrowIfNull(callerPreferences);
        ArgumentNullException.ThrowIfNull(candidates);

        if (rotationOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationOffset),
                rotationOffset,
                "Rotation offset cannot be negative.");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] is null)
            {
                throw new ArgumentException("Candidate collection cannot contain null elements.", nameof(candidates));
            }
        }

        if (candidates.Count == 0)
        {
            return Array.AsReadOnly(Array.Empty<SocialExploreCandidateCard>());
        }

        // Caller-level pre-validation: if caller lacks authoritative evidence, is underage, not opted in, or inactive,
        // no candidates may be discovered (fails closed).
        if (!callerEvidence.HasAuthoritativeEvidence ||
            callerEvidence.CallerAge < 18 ||
            !callerEvidence.IsOptedIn ||
            !callerEvidence.IsActive)
        {
            return Array.AsReadOnly(Array.Empty<SocialExploreCandidateCard>());
        }

        var eligibleCards = new List<SocialExploreCandidateCard>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            var evidence = item.Evidence;
            var matchingInput = item.MatchingInput;

            // Identity consistency: candidate evidence and matching input must refer to the same private profile
            if (evidence.CandidateProfileId != matchingInput.CandidateProfileId)
            {
                continue;
            }

            // Age consistency: candidate evidence age must match candidate matching input age
            if (evidence.CandidateAge != matchingInput.CandidateAge)
            {
                continue;
            }

            // Stage 1: Discovery eligibility and privacy pre-filter
            var preFilterResult = SocialDiscoveryEligibilityPreFilter.Evaluate(callerEvidence, evidence);
            if (!preFilterResult.IsEligible)
            {
                continue;
            }

            // Stage 2: Hard-filter reciprocal matching evaluation
            var evaluation = SocialCandidateFilterEvaluator.Evaluate(
                callerEvidence.CallerAge,
                callerGender,
                callerPreferences,
                matchingInput.CandidateAge,
                matchingInput.CandidateGender,
                matchingInput.CandidatePreferences,
                matchingInput.ApproximateDistanceKm);

            if (evaluation.Status == SocialCandidateMatchStatus.Eligible)
            {
                var card = SocialExploreCandidateCard.Create(
                    matchingInput.CandidateProfileId,
                    matchingInput.DisplayName,
                    matchingInput.CandidateAge,
                    matchingInput.ApproximateDistanceKm,
                    evaluation);

                eligibleCards.Add(card);
            }
        }

        return SocialCandidateRotator.Rotate(eligibleCards, rotationOffset);
    }
}
