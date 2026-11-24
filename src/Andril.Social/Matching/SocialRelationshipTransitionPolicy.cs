using System;
using System.Text.Json.Serialization;
using Andril.Social.Entities;
using Andril.Social.Interfaces;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of the deterministic Social relationship transition evaluation.
/// </summary>
public enum SocialRelationshipTransitionStatus
{
    /// <summary>
    /// Both participants have expressed affirmative interest and satisfy all eligibility criteria; mutual match can be created.
    /// </summary>
    EligibleForMatch,

    /// <summary>
    /// An active mutual match already exists for this canonical pair; repeat affirmative signals are safely idempotent.
    /// </summary>
    AlreadyMatched,

    /// <summary>
    /// Acting profile expressed positive interest, but counterpart has not recorded affirmative interest; stays strictly confidential.
    /// </summary>
    PendingUnilateral,

    /// <summary>
    /// At least one participant expressed negative interest (pass); no mutual match can be formed.
    /// </summary>
    Passed,

    /// <summary>
    /// Transition denied because one or both participants are blocked.
    /// </summary>
    Blocked,

    /// <summary>
    /// Transition denied because one or both participants are underage (&lt;18).
    /// </summary>
    Underage,

    /// <summary>
    /// Transition denied because one or both participants have not opted into Social discovery.
    /// </summary>
    NotOptedIn,

    /// <summary>
    /// Transition denied because one or both participant profiles are inactive, suspended, or deactivated.
    /// </summary>
    NotActive,

    /// <summary>
    /// Transition denied because the target candidate has disabled discovery or enabled hidden mode.
    /// </summary>
    CandidateHidden,

    /// <summary>
    /// Transition denied because authoritative verification evidence is missing or indeterminate (fails closed).
    /// </summary>
    MissingEvidence,

    /// <summary>
    /// Pair key, participant identity, or evidence binding is invalid or mismatched.
    /// </summary>
    InvalidPair
}

/// <summary>
/// Immutable, privacy-preserving outcome of a Social relationship transition evaluation.
/// </summary>
public sealed class SocialRelationshipTransitionResult : IEquatable<SocialRelationshipTransitionResult>
{
    /// <summary>
    /// Gets the status outcome of the transition evaluation.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipTransitionStatus Status { get; }

    /// <summary>
    /// Gets the resulting pairwise relationship state.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipState ResultingState { get; }

    /// <summary>
    /// Gets a value indicating whether a new mutual match can be established from this transition.
    /// </summary>
    [JsonIgnore]
    public bool CanFormMatch => Status == SocialRelationshipTransitionStatus.EligibleForMatch;

    /// <summary>
    /// Gets a value indicating whether the transition represents an idempotent repeat of an existing match state.
    /// </summary>
    [JsonIgnore]
    public bool IsIdempotent => Status == SocialRelationshipTransitionStatus.AlreadyMatched;

    /// <summary>
    /// Gets a value indicating whether the transition was denied due to policy or eligibility failures.
    /// </summary>
    [JsonIgnore]
    public bool IsDenied => Status switch
    {
        SocialRelationshipTransitionStatus.Blocked => true,
        SocialRelationshipTransitionStatus.Underage => true,
        SocialRelationshipTransitionStatus.NotOptedIn => true,
        SocialRelationshipTransitionStatus.NotActive => true,
        SocialRelationshipTransitionStatus.CandidateHidden => true,
        SocialRelationshipTransitionStatus.MissingEvidence => true,
        SocialRelationshipTransitionStatus.InvalidPair => true,
        _ => false
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialRelationshipTransitionResult"/> class.
    /// </summary>
    public SocialRelationshipTransitionResult(
        SocialRelationshipTransitionStatus status,
        SocialRelationshipState resultingState)
    {
        Status = status;
        ResultingState = resultingState;
    }

    /// <inheritdoc />
    public bool Equals(SocialRelationshipTransitionResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status && ResultingState == other.ResultingState;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is SocialRelationshipTransitionResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine((int)Status, (int)ResultingState);

    /// <inheritdoc />
    public override string ToString() => "SocialRelationshipTransitionResult [private]";
}

/// <summary>
/// Pure, deterministic domain policy governing relationship state transitions, mutual match eligibility,
/// idempotency, and fail-closed security invariants for Social dating pairs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Core Domain and Security Rules:</b>
/// <list type="bullet">
/// <item><description><b>Unilateral Privacy:</b> One-sided positive interest never forms a match and remains strictly confidential.</description></item>
/// <item><description><b>Mutual Consent Prerequisite:</b> Two reciprocal affirmative decisions are required to establish a mutual match.</description></item>
/// <item><description><b>Eligibility Gating:</b> Blocked, opted-out, inactive, or underage participants are strictly denied transition.</description></item>
/// <item><description><b>Compatibility Independence:</b> Compatibility score is NOT a prerequisite for match formation (consent alone governs transitions).</description></item>
/// <item><description><b>Idempotency:</b> Repeat affirmative decisions for already-matched pairs return <see cref="SocialRelationshipTransitionStatus.AlreadyMatched"/> safely.</description></item>
/// <item><description><b>Fail-Closed:</b> Missing, indeterminate, or malformed participant evidence fails closed with denial.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialRelationshipTransitionPolicy
{
    /// <summary>
    /// Evaluates a proposed interest signal and participant evidence against current pairwise state to determine the valid transition.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The identity of the participant recording interest.</param>
    /// <param name="actingInterestPositive">True for like (affirmative); false for pass (negative).</param>
    /// <param name="existingActingInterest">The acting participant's prior recorded interest, if any.</param>
    /// <param name="reciprocalInterest">The counterpart participant's recorded interest, if any.</param>
    /// <param name="existingMatch">The existing active match entity, if any.</param>
    /// <param name="actorEvidence">Authoritative discovery evidence for the acting profile.</param>
    /// <param name="candidateEvidence">Authoritative discovery evidence for the counterpart profile.</param>
    /// <returns>A deterministic <see cref="SocialRelationshipTransitionResult"/> indicating the transition outcome.</returns>
    public static SocialRelationshipTransitionResult EvaluateTransition(
        SocialProfilePairKey? pairKey,
        Guid actingProfileId,
        bool actingInterestPositive,
        SocialDirectionalInterest? existingActingInterest,
        SocialDirectionalInterest? reciprocalInterest,
        SocialMatchEntity? existingMatch,
        SocialDiscoveryCallerEvidence? actorEvidence,
        SocialDiscoveryCandidateEvidence? candidateEvidence)
    {
        // 1. Structural and identity validation
        if (pairKey is null || actingProfileId == Guid.Empty || !pairKey.Contains(actingProfileId))
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.InvalidPair,
                SocialRelationshipState.None);
        }

        Guid targetProfileId = pairKey.GetOtherProfileId(actingProfileId);

        // 2. Authoritative evidence null-check and binding
        if (actorEvidence is null || candidateEvidence is null)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.MissingEvidence,
                SocialRelationshipState.None);
        }

        if (actorEvidence.CallerProfileId != actingProfileId || candidateEvidence.CandidateProfileId != targetProfileId)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.InvalidPair,
                SocialRelationshipState.None);
        }

        // 3. Authoritative evidence presence
        if (!actorEvidence.HasAuthoritativeEvidence || !candidateEvidence.HasAuthoritativeEvidence)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.MissingEvidence,
                SocialRelationshipState.None);
        }

        // 4. Baseline eligibility and privacy preflight evaluation
        var discoveryResult = SocialDiscoveryEligibilityPreFilter.Evaluate(actorEvidence, candidateEvidence);

        if (discoveryResult.Status == SocialDiscoveryEligibilityStatus.Indeterminate)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.MissingEvidence,
                SocialRelationshipState.None);
        }

        if (discoveryResult.Status == SocialDiscoveryEligibilityStatus.Ineligible)
        {
            var reasons = discoveryResult.Reasons;

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.MissingEvidence,
                    SocialRelationshipState.None);
            }

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller) ||
                reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.Blocked,
                    SocialRelationshipState.None);
            }

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage) ||
                reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.Underage,
                    SocialRelationshipState.None);
            }

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotOptedIn) ||
                reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotOptedIn))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.NotOptedIn,
                    SocialRelationshipState.None);
            }

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotActive) ||
                reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotActive))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.NotActive,
                    SocialRelationshipState.None);
            }

            if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateHidden))
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.CandidateHidden,
                    SocialRelationshipState.None);
            }

            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.MissingEvidence,
                SocialRelationshipState.None);
        }

        // 5. Existing match idempotency
        if (existingMatch is not null)
        {
            if (actingInterestPositive)
            {
                return new SocialRelationshipTransitionResult(
                    SocialRelationshipTransitionStatus.AlreadyMatched,
                    SocialRelationshipState.Matched);
            }

            // Passing on an active match acts as a negative transition
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.Passed,
                SocialRelationshipState.Passed);
        }

        // 6. Negative decision (Pass)
        if (!actingInterestPositive)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.Passed,
                SocialRelationshipState.Passed);
        }

        // 7. Positive decision with no reciprocal interest yet (Unilateral)
        if (reciprocalInterest is null)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.PendingUnilateral,
                SocialRelationshipState.PendingUnilateral);
        }

        // 8. Positive decision with reciprocal interest present
        if (reciprocalInterest.IsPositive)
        {
            return new SocialRelationshipTransitionResult(
                SocialRelationshipTransitionStatus.EligibleForMatch,
                SocialRelationshipState.Matched);
        }

        // Reciprocal interest was negative (counterpart passed)
        return new SocialRelationshipTransitionResult(
            SocialRelationshipTransitionStatus.Passed,
            SocialRelationshipState.Passed);
    }
}
