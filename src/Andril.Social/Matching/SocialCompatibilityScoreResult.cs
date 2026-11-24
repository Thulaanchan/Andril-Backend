using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of the compatibility scoring evaluation.
/// </summary>
public enum SocialCompatibilityEvaluationStatus
{
    /// <summary>Compatibility score was successfully calculated.</summary>
    Scored,

    /// <summary>Pair is eligible, but available factor weight sum is zero (no calculable score).</summary>
    Unscored,

    /// <summary>Pair is ineligible due to baseline opt-in, activity, or block-list restrictions.</summary>
    Ineligible,

    /// <summary>Pair failed hard demographic filter criteria (reciprocal age, gender, distance).</summary>
    Excluded,

    /// <summary>Required baseline evidence is missing or indeterminate.</summary>
    Indeterminate
}

/// <summary>
/// Immutable, privacy-preserving domain result of pair compatibility evaluation (P-04 / Tasks 149-150).
/// </summary>
public sealed class SocialCompatibilityScoreResult : IEquatable<SocialCompatibilityScoreResult>
{
    public const string DefaultPolicyVersion = "P-04-1.0";

    [JsonIgnore]
    public string PolicyVersion { get; }

    [JsonIgnore]
    public SocialCompatibilityEvaluationStatus Status { get; }

    [JsonIgnore]
    public bool IsScored => Status == SocialCompatibilityEvaluationStatus.Scored;

    [JsonIgnore]
    public double? CompatibilityScore { get; }

    [JsonIgnore]
    public double? RoundedScore =>
        CompatibilityScore.HasValue
            ? Math.Round(CompatibilityScore.Value, 1, MidpointRounding.AwayFromZero)
            : null;

    [JsonIgnore]
    public double TotalAvailableWeight { get; }

    [JsonIgnore]
    public double EvidenceCoveragePercentage { get; }

    [JsonIgnore]
    public double ApplicableCoveragePercentage { get; }

    [JsonIgnore]
    public IReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown> Factors { get; }

    [JsonIgnore]
    public IReadOnlyList<string> IneligibilityReasons { get; }

    [JsonIgnore]
    public SocialCandidateExclusionReason? ExclusionReason { get; }

    private SocialCompatibilityScoreResult(
        string policyVersion,
        SocialCompatibilityEvaluationStatus status,
        double? compatibilityScore,
        double totalAvailableWeight,
        double evidenceCoveragePercentage,
        double applicableCoveragePercentage,
        IReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>? factors = null,
        IEnumerable<string>? ineligibilityReasons = null,
        SocialCandidateExclusionReason? exclusionReason = null)
    {
        PolicyVersion = policyVersion ?? DefaultPolicyVersion;
        Status = status;
        CompatibilityScore = compatibilityScore;
        TotalAvailableWeight = totalAvailableWeight;
        EvidenceCoveragePercentage = evidenceCoveragePercentage;
        ApplicableCoveragePercentage = applicableCoveragePercentage;
        Factors = factors ?? new ReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>(
            new Dictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>());
        IneligibilityReasons = Array.AsReadOnly(ineligibilityReasons is not null ? new List<string>(ineligibilityReasons).ToArray() : Array.Empty<string>());
        ExclusionReason = exclusionReason;
    }

    /// <summary>Creates a successfully calculated compatibility score result.</summary>
    public static SocialCompatibilityScoreResult Scored(
        double score,
        double totalAvailableWeight,
        double evidenceCoverage,
        double applicableCoverage,
        IReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown> factors,
        string policyVersion = DefaultPolicyVersion)
    {
        if (score < 0.0 || score > 100.0)
        {
            throw new ArgumentOutOfRangeException(nameof(score), score, "Compatibility score must be between 0.0 and 100.0.");
        }

        return new SocialCompatibilityScoreResult(
            policyVersion,
            SocialCompatibilityEvaluationStatus.Scored,
            score,
            totalAvailableWeight,
            evidenceCoverage,
            applicableCoverage,
            factors);
    }

    /// <summary>Creates an unscored result when total available weight W is zero.</summary>
    public static SocialCompatibilityScoreResult Unscored(
        IReadOnlyDictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown> factors,
        string policyVersion = DefaultPolicyVersion) =>
        new(
            policyVersion,
            SocialCompatibilityEvaluationStatus.Unscored,
            null,
            0.0,
            0.0,
            0.0,
            factors);

    /// <summary>Creates an ineligible result when baseline eligibility or privacy checks fail.</summary>
    public static SocialCompatibilityScoreResult Ineligible(
        IEnumerable<string> reasons,
        string policyVersion = DefaultPolicyVersion) =>
        new(
            policyVersion,
            SocialCompatibilityEvaluationStatus.Ineligible,
            null,
            0.0,
            0.0,
            0.0,
            ineligibilityReasons: reasons);

    /// <summary>Creates an excluded result when hard demographic filters fail.</summary>
    public static SocialCompatibilityScoreResult Excluded(
        SocialCandidateExclusionReason reason,
        string policyVersion = DefaultPolicyVersion) =>
        new(
            policyVersion,
            SocialCompatibilityEvaluationStatus.Excluded,
            null,
            0.0,
            0.0,
            0.0,
            ineligibilityReasons: new[] { reason.ToString() },
            exclusionReason: reason);

    /// <summary>Creates an indeterminate result when required evidence is missing.</summary>
    public static SocialCompatibilityScoreResult Indeterminate(
        string reason,
        string policyVersion = DefaultPolicyVersion) =>
        new(
            policyVersion,
            SocialCompatibilityEvaluationStatus.Indeterminate,
            null,
            0.0,
            0.0,
            0.0,
            ineligibilityReasons: new[] { reason });

    public bool Equals(SocialCompatibilityScoreResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(PolicyVersion, other.PolicyVersion, StringComparison.Ordinal) &&
               Status == other.Status &&
               Nullable.Equals(CompatibilityScore, other.CompatibilityScore) &&
               Math.Abs(TotalAvailableWeight - other.TotalAvailableWeight) < 1e-9 &&
               Math.Abs(EvidenceCoveragePercentage - other.EvidenceCoveragePercentage) < 1e-9 &&
               Math.Abs(ApplicableCoveragePercentage - other.ApplicableCoveragePercentage) < 1e-9 &&
               ExclusionReason == other.ExclusionReason;
    }

    public override bool Equals(object? obj) => obj is SocialCompatibilityScoreResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(PolicyVersion, Status, CompatibilityScore, TotalAvailableWeight, ExclusionReason);

    public override string ToString() => "SocialCompatibilityScoreResult [private]";
}
