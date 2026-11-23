using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of the offline Social directional interest write preflight evaluation (CF-20 / SOC-02).
/// </summary>
public enum SocialInterestWritePreflightStatus
{
    /// <summary>Proposed interest decision satisfies all structural, identity, age, opt-in, activity, visibility, and block criteria.</summary>
    Eligible,

    /// <summary>One or more participant identities are empty or malformed.</summary>
    InvalidIdentity,

    /// <summary>Proposed interest record does not belong to the expected canonical profile pair.</summary>
    PairMismatch,

    /// <summary>Acting profile does not belong to the pair or does not match caller evidence.</summary>
    ActingProfileMismatch,

    /// <summary>Target candidate does not belong to the pair or does not match candidate evidence.</summary>
    TargetProfileMismatch,

    /// <summary>Authoritative verification evidence is missing or unconfirmed (fails closed).</summary>
    MissingEvidence,

    /// <summary>Acting profile is under 18 years of age.</summary>
    ActorUnderage,

    /// <summary>Target candidate is under 18 years of age.</summary>
    CandidateUnderage,

    /// <summary>Acting profile has not explicitly opted into Social discovery.</summary>
    ActorNotOptedIn,

    /// <summary>Target candidate has not explicitly opted into Social discovery.</summary>
    CandidateNotOptedIn,

    /// <summary>Acting profile is not in active standing.</summary>
    ActorNotActive,

    /// <summary>Target candidate is not in active standing.</summary>
    CandidateNotActive,

    /// <summary>Target candidate has disabled discovery or is hidden.</summary>
    CandidateHidden,

    /// <summary>Target candidate is blocked by the acting profile.</summary>
    BlockedByActor,

    /// <summary>Acting profile is blocked by the target candidate.</summary>
    BlockedByCandidate
}

/// <summary>
/// Immutable, privacy-preserving evaluation result from the Social directional interest write preflight policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Privacy Invariants:</b><br />
/// <list type="bullet">
/// <item><description>Represents offline preflight structural/policy eligibility only.</description></item>
/// <item><description>Does NOT represent mutual match authorization or chat/messaging permission (e.g., M4 communication tokens).</description></item>
/// <item><description>Conceals confidential identifiers in diagnostics and JSON representation.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialInterestWritePreflightResult : IEquatable<SocialInterestWritePreflightResult>
{
    /// <summary>
    /// Gets the status outcome of the interest write preflight evaluation.
    /// </summary>
    [JsonIgnore]
    public SocialInterestWritePreflightStatus Status { get; }

    /// <summary>
    /// Gets the underlying discovery eligibility evaluation result, if evaluated.
    /// </summary>
    [JsonIgnore]
    public SocialDiscoveryEligibilityResult? DiscoveryResult { get; }

    /// <summary>
    /// Gets a value indicating whether the proposed interest record is eligible for further processing.
    /// </summary>
    public bool IsEligible => Status == SocialInterestWritePreflightStatus.Eligible;

    private SocialInterestWritePreflightResult(
        SocialInterestWritePreflightStatus status,
        SocialDiscoveryEligibilityResult? discoveryResult)
    {
        Status = status;
        DiscoveryResult = discoveryResult;
    }

    /// <summary>Creates a successful, eligible preflight result.</summary>
    public static SocialInterestWritePreflightResult Success(SocialDiscoveryEligibilityResult? discoveryResult = null) =>
        new(SocialInterestWritePreflightStatus.Eligible, discoveryResult);

    /// <summary>Creates a failed preflight result with the specified error status.</summary>
    public static SocialInterestWritePreflightResult Fail(
        SocialInterestWritePreflightStatus status,
        SocialDiscoveryEligibilityResult? discoveryResult = null) =>
        new(status, discoveryResult);

    public bool Equals(SocialInterestWritePreflightResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status && Equals(DiscoveryResult, other.DiscoveryResult);
    }

    public override bool Equals(object? obj) =>
        obj is SocialInterestWritePreflightResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Status, DiscoveryResult);

    public override string ToString() =>
        "SocialInterestWritePreflightResult [private]";
}

/// <summary>
/// Deterministic offline policy for evaluating whether a proposed directional interest record is
/// structurally and privacy eligible for further processing (CF-20 / SOC-02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// <list type="bullet">
/// <item><description>Evaluates canonical pair identity, participant evidence binding, legal age, opt-in status, profile standing, visibility, and bidirectional blocks.</description></item>
/// <item><description>A positive or negative interest decision MUST NOT imply communication or mutual-match authorization.</description></item>
/// <item><description>Does NOT persist, queue, or publish directional interest records.</description></item>
/// <item><description>Does NOT issue M4 conversation permissions or tokens.</description></item>
/// <item><description>Does NOT compute matchmaking weights, rate limits, or like quotas.</description></item>
/// <item><description>Fails closed if authoritative evidence is missing, unverified, or mismatched.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialInterestWritePreflightPolicy
{
    /// <summary>
    /// Evaluates a proposed directional interest record against participant evidence and privacy rules.
    /// </summary>
    /// <param name="proposedInterest">The proposed directional interest record.</param>
    /// <param name="actorEvidence">The authoritative discovery evidence for the acting profile.</param>
    /// <param name="candidateEvidence">The authoritative discovery evidence for the target candidate profile.</param>
    /// <returns>A deterministic <see cref="SocialInterestWritePreflightResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    public static SocialInterestWritePreflightResult Evaluate(
        SocialDirectionalInterest proposedInterest,
        SocialDiscoveryCallerEvidence actorEvidence,
        SocialDiscoveryCandidateEvidence candidateEvidence)
    {
        ArgumentNullException.ThrowIfNull(proposedInterest);
        ArgumentNullException.ThrowIfNull(actorEvidence);
        ArgumentNullException.ThrowIfNull(candidateEvidence);

        return EvaluateInternal(null, proposedInterest, actorEvidence, candidateEvidence);
    }

    /// <summary>
    /// Evaluates a proposed directional interest record against an expected canonical pair key, participant evidence, and privacy rules.
    /// </summary>
    /// <param name="expectedPairKey">The expected canonical profile pair key.</param>
    /// <param name="proposedInterest">The proposed directional interest record.</param>
    /// <param name="actorEvidence">The authoritative discovery evidence for the acting profile.</param>
    /// <param name="candidateEvidence">The authoritative discovery evidence for the target candidate profile.</param>
    /// <returns>A deterministic <see cref="SocialInterestWritePreflightResult"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any required parameter is null.</exception>
    public static SocialInterestWritePreflightResult Evaluate(
        SocialProfilePairKey expectedPairKey,
        SocialDirectionalInterest proposedInterest,
        SocialDiscoveryCallerEvidence actorEvidence,
        SocialDiscoveryCandidateEvidence candidateEvidence)
    {
        ArgumentNullException.ThrowIfNull(expectedPairKey);
        ArgumentNullException.ThrowIfNull(proposedInterest);
        ArgumentNullException.ThrowIfNull(actorEvidence);
        ArgumentNullException.ThrowIfNull(candidateEvidence);

        return EvaluateInternal(expectedPairKey, proposedInterest, actorEvidence, candidateEvidence);
    }

    private static SocialInterestWritePreflightResult EvaluateInternal(
        SocialProfilePairKey? expectedPairKey,
        SocialDirectionalInterest proposedInterest,
        SocialDiscoveryCallerEvidence actorEvidence,
        SocialDiscoveryCandidateEvidence candidateEvidence)
    {
        // 1. Verify expected canonical pair key if explicitly specified
        if (expectedPairKey is not null && !expectedPairKey.Equals(proposedInterest.PairKey))
        {
            return SocialInterestWritePreflightResult.Fail(SocialInterestWritePreflightStatus.PairMismatch);
        }

        // 2. Validate proposed interest record scope against its own pair key
        var scopeValidation = SocialInterestResultScopePolicy.Validate(
            proposedInterest.PairKey,
            proposedInterest.ActingProfileId,
            proposedInterest);

        if (!scopeValidation.IsValid)
        {
            return SocialInterestWritePreflightResult.Fail(SocialInterestWritePreflightStatus.PairMismatch);
        }

        // 3. Verify acting profile matches actor evidence
        if (actorEvidence.CallerProfileId != proposedInterest.ActingProfileId)
        {
            return SocialInterestWritePreflightResult.Fail(SocialInterestWritePreflightStatus.ActingProfileMismatch);
        }

        // 4. Verify candidate profile matches target profile
        if (candidateEvidence.CandidateProfileId != proposedInterest.TargetProfileId)
        {
            return SocialInterestWritePreflightResult.Fail(SocialInterestWritePreflightStatus.TargetProfileMismatch);
        }

        // 5. Evaluate discovery eligibility and privacy pre-filter
        var discoveryResult = SocialDiscoveryEligibilityPreFilter.Evaluate(actorEvidence, candidateEvidence);

        if (!discoveryResult.IsEligible)
        {
            var status = MapIneligibilityStatus(discoveryResult.Reasons);
            return SocialInterestWritePreflightResult.Fail(status, discoveryResult);
        }

        return SocialInterestWritePreflightResult.Success(discoveryResult);
    }

    private static SocialInterestWritePreflightStatus MapIneligibilityStatus(SocialDiscoveryIneligibilityReason reasons)
    {
        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence))
        {
            return SocialInterestWritePreflightStatus.MissingEvidence;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.SameProfile))
        {
            return SocialInterestWritePreflightStatus.InvalidIdentity;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage))
        {
            return SocialInterestWritePreflightStatus.ActorUnderage;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage))
        {
            return SocialInterestWritePreflightStatus.CandidateUnderage;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotOptedIn))
        {
            return SocialInterestWritePreflightStatus.ActorNotOptedIn;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotOptedIn))
        {
            return SocialInterestWritePreflightStatus.CandidateNotOptedIn;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotActive))
        {
            return SocialInterestWritePreflightStatus.ActorNotActive;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotActive))
        {
            return SocialInterestWritePreflightStatus.CandidateNotActive;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateHidden))
        {
            return SocialInterestWritePreflightStatus.CandidateHidden;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller))
        {
            return SocialInterestWritePreflightStatus.BlockedByActor;
        }

        if (reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate))
        {
            return SocialInterestWritePreflightStatus.BlockedByCandidate;
        }

        return SocialInterestWritePreflightStatus.MissingEvidence;
    }
}
