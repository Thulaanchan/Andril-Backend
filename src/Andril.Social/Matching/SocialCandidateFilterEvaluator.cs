using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Andril.Social.Matching;

/// <summary>
/// Status of candidate matching evaluation.
/// </summary>
public enum SocialCandidateMatchStatus
{
    /// <summary>All confirmed hard filters are satisfied with unambiguous evidence.</summary>
    Eligible,

    /// <summary>One or more confirmed hard filters are explicitly not satisfied.</summary>
    Excluded,

    /// <summary>Required evidence or approved matching policy is unavailable (e.g., missing distance or unresolved gender demographic).</summary>
    Indeterminate
}

/// <summary>
/// Confirmed hard-filter exclusion reasons.
/// </summary>
public enum SocialCandidateExclusionReason
{
    /// <summary>Caller is under 18 years of age.</summary>
    CallerUnderage,

    /// <summary>Candidate is under 18 years of age.</summary>
    CandidateUnderage,

    /// <summary>Candidate's age does not satisfy the caller's minimum or maximum age preferences.</summary>
    CallerAgePreferenceMismatch,

    /// <summary>Caller's age does not satisfy the candidate's minimum or maximum age preferences.</summary>
    CandidateAgePreferenceMismatch,

    /// <summary>Candidate's gender does not satisfy the caller's Show Me preference.</summary>
    CallerGenderPreferenceMismatch,

    /// <summary>Caller's gender does not satisfy the candidate's Show Me preference.</summary>
    CandidateGenderPreferenceMismatch,

    /// <summary>The approximate distance between users is outside the caller's distance range preference.</summary>
    CallerDistancePreferenceMismatch,

    /// <summary>The approximate distance between users is outside the candidate's distance range preference.</summary>
    CandidateDistancePreferenceMismatch
}

/// <summary>
/// Reasons why candidate evaluation outcome remains indeterminate.
/// </summary>
public enum SocialCandidateIndeterminateReason
{
    /// <summary>Distance evidence is missing or unknown.</summary>
    DistanceUnavailable,

    /// <summary>Caller demographic gender semantics are unresolved (e.g., other, prefer_not_to_say, or unrecorded).</summary>
    CallerGenderUnresolved,

    /// <summary>Candidate demographic gender semantics are unresolved (e.g., other, prefer_not_to_say, or unrecorded).</summary>
    CandidateGenderUnresolved
}

/// <summary>
/// Immutable input bundle for pure Social candidate filter evaluation.
/// </summary>
public sealed record SocialCandidateEvaluationInput
{
    public int CallerAge { get; init; }
    public string? CallerGender { get; init; }
    public SocialExplorePreferences CallerPreferences { get; init; }
    public int CandidateAge { get; init; }
    public string? CandidateGender { get; init; }
    public SocialExplorePreferences CandidatePreferences { get; init; }
    public int? ApproximateDistanceKm { get; init; }

    public SocialCandidateEvaluationInput(
        int callerAge,
        string? callerGender,
        SocialExplorePreferences callerPreferences,
        int candidateAge,
        string? candidateGender,
        SocialExplorePreferences candidatePreferences,
        int? approximateDistanceKm = null)
    {
        CallerAge = callerAge;
        CallerGender = callerGender;
        CallerPreferences = callerPreferences ?? throw new ArgumentNullException(nameof(callerPreferences));
        CandidateAge = candidateAge;
        CandidateGender = candidateGender;
        CandidatePreferences = candidatePreferences ?? throw new ArgumentNullException(nameof(candidatePreferences));

        if (approximateDistanceKm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approximateDistanceKm),
                approximateDistanceKm,
                "Approximate distance cannot be negative.");
        }

        ApproximateDistanceKm = approximateDistanceKm;
    }
}

/// <summary>
/// Immutable result of pure Social candidate filter evaluation.
/// </summary>
public sealed class SocialCandidateEvaluationResult : IEquatable<SocialCandidateEvaluationResult>
{
    public SocialCandidateMatchStatus Status { get; }

    public bool IsEligible => Status == SocialCandidateMatchStatus.Eligible;
    public bool IsExcluded => Status == SocialCandidateMatchStatus.Excluded;
    public bool IsIndeterminate => Status == SocialCandidateMatchStatus.Indeterminate;

    public IReadOnlyList<SocialCandidateExclusionReason> ExclusionReasons { get; }
    public IReadOnlyList<SocialCandidateIndeterminateReason> IndeterminateReasons { get; }

    public IReadOnlyList<string> SharedLanguages { get; }
    public IReadOnlyList<string> SharedConnectionGoals { get; }

    public SocialCandidateEvaluationResult(
        SocialCandidateMatchStatus status,
        IEnumerable<SocialCandidateExclusionReason>? exclusionReasons = null,
        IEnumerable<SocialCandidateIndeterminateReason>? indeterminateReasons = null,
        IEnumerable<string>? sharedLanguages = null,
        IEnumerable<string>? sharedConnectionGoals = null)
    {
        Status = status;

        ExclusionReasons = Array.AsReadOnly(exclusionReasons?.ToArray() ?? Array.Empty<SocialCandidateExclusionReason>());
        IndeterminateReasons = Array.AsReadOnly(indeterminateReasons?.ToArray() ?? Array.Empty<SocialCandidateIndeterminateReason>());
        SharedLanguages = Array.AsReadOnly(sharedLanguages?.ToArray() ?? Array.Empty<string>());
        SharedConnectionGoals = Array.AsReadOnly(sharedConnectionGoals?.ToArray() ?? Array.Empty<string>());
    }

    public bool Equals(SocialCandidateEvaluationResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status &&
               ExclusionReasons.SequenceEqual(other.ExclusionReasons) &&
               IndeterminateReasons.SequenceEqual(other.IndeterminateReasons) &&
               SharedLanguages.SequenceEqual(other.SharedLanguages, StringComparer.Ordinal) &&
               SharedConnectionGoals.SequenceEqual(other.SharedConnectionGoals, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj) =>
        obj is SocialCandidateEvaluationResult other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Status);
        foreach (var r in ExclusionReasons) hash.Add(r);
        foreach (var r in IndeterminateReasons) hash.Add(r);
        foreach (var l in SharedLanguages) hash.Add(l, StringComparer.Ordinal);
        foreach (var g in SharedConnectionGoals) hash.Add(g, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public static bool operator ==(SocialCandidateEvaluationResult? left, SocialCandidateEvaluationResult? right) =>
        Equals(left, right);

    public static bool operator !=(SocialCandidateEvaluationResult? left, SocialCandidateEvaluationResult? right) =>
        !Equals(left, right);

    public override string ToString()
    {
        var exclusions = ExclusionReasons.Count > 0 ? $" Exclusions: [{string.Join(", ", ExclusionReasons)}]" : string.Empty;
        var indeterminates = IndeterminateReasons.Count > 0 ? $" Indeterminate: [{string.Join(", ", IndeterminateReasons)}]" : string.Empty;
        var languages = $" Languages: [{string.Join(", ", SharedLanguages)}]";
        var goals = $" Goals: [{string.Join(", ", SharedConnectionGoals)}]";

        return $"Status: {Status};{exclusions}{indeterminates}{languages}{goals}";
    }
}

/// <summary>
/// Pure, deterministic in-memory domain evaluator for Social candidate filtering rules.
/// </summary>
public static class SocialCandidateFilterEvaluator
{
    private const string DemographicMale = "male";
    private const string DemographicFemale = "female";

    /// <summary>
    /// Evaluates candidate eligibility using a structured input bundle.
    /// </summary>
    public static SocialCandidateEvaluationResult Evaluate(SocialCandidateEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Evaluate(
            input.CallerAge,
            input.CallerGender,
            input.CallerPreferences,
            input.CandidateAge,
            input.CandidateGender,
            input.CandidatePreferences,
            input.ApproximateDistanceKm);
    }

    /// <summary>
    /// Evaluates candidate eligibility using separate parameters.
    /// </summary>
    public static SocialCandidateEvaluationResult Evaluate(
        int callerAge,
        string? callerGender,
        SocialExplorePreferences callerPreferences,
        int candidateAge,
        string? candidateGender,
        SocialExplorePreferences candidatePreferences,
        int? approximateDistanceKm = null)
    {
        ArgumentNullException.ThrowIfNull(callerPreferences);
        ArgumentNullException.ThrowIfNull(candidatePreferences);

        if (approximateDistanceKm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approximateDistanceKm),
                approximateDistanceKm,
                "Approximate distance cannot be negative.");
        }

        var exclusions = new List<SocialCandidateExclusionReason>();
        var indeterminates = new List<SocialCandidateIndeterminateReason>();

        // 1. Reciprocal Age Evaluation
        EvaluateAge(callerAge, candidateAge, callerPreferences, candidatePreferences, exclusions);

        // 2. Reciprocal Gender Evaluation
        EvaluateGender(callerGender, candidateGender, callerPreferences, candidatePreferences, exclusions, indeterminates);

        // 3. Reciprocal Distance Evaluation
        EvaluateDistance(approximateDistanceKm, callerPreferences, candidatePreferences, exclusions, indeterminates);

        // 4. Overlapping Communication Languages (Information / Ranking Signal)
        var sharedLanguages = CalculateSharedLanguages(callerPreferences.Languages, candidatePreferences.Languages);

        // 5. Overlapping Connection Goals (Information / Ranking Signal)
        var sharedGoals = CalculateSharedConnectionGoals(callerPreferences.ConnectionGoals, candidatePreferences.ConnectionGoals);

        // Determine final matching status
        var status = exclusions.Count > 0
            ? SocialCandidateMatchStatus.Excluded
            : indeterminates.Count > 0
                ? SocialCandidateMatchStatus.Indeterminate
                : SocialCandidateMatchStatus.Eligible;

        return new SocialCandidateEvaluationResult(
            status,
            exclusions,
            indeterminates,
            sharedLanguages,
            sharedGoals);
    }

    private static void EvaluateAge(
        int callerAge,
        int candidateAge,
        SocialExplorePreferences callerPrefs,
        SocialExplorePreferences candidatePrefs,
        List<SocialCandidateExclusionReason> exclusions)
    {
        // 18+ requirement for both participants
        if (callerAge < 18)
        {
            exclusions.Add(SocialCandidateExclusionReason.CallerUnderage);
        }

        if (candidateAge < 18)
        {
            exclusions.Add(SocialCandidateExclusionReason.CandidateUnderage);
        }

        // Caller's preferred age range applied to candidate age
        if (candidateAge < callerPrefs.MinAge || candidateAge > callerPrefs.MaxAge)
        {
            exclusions.Add(SocialCandidateExclusionReason.CallerAgePreferenceMismatch);
        }

        // Candidate's preferred age range applied to caller age
        if (callerAge < candidatePrefs.MinAge || callerAge > candidatePrefs.MaxAge)
        {
            exclusions.Add(SocialCandidateExclusionReason.CandidateAgePreferenceMismatch);
        }
    }

    private static void EvaluateGender(
        string? callerGender,
        string? candidateGender,
        SocialExplorePreferences callerPrefs,
        SocialExplorePreferences candidatePrefs,
        List<SocialCandidateExclusionReason> exclusions,
        List<SocialCandidateIndeterminateReason> indeterminates)
    {
        var callerNormalized = NormalizeDemographicGender(callerGender);
        var candidateNormalized = NormalizeDemographicGender(candidateGender);

        var callerUnresolved = callerNormalized is null;
        var candidateUnresolved = candidateNormalized is null;

        if (callerUnresolved)
        {
            indeterminates.Add(SocialCandidateIndeterminateReason.CallerGenderUnresolved);
        }

        if (candidateUnresolved)
        {
            indeterminates.Add(SocialCandidateIndeterminateReason.CandidateGenderUnresolved);
        }

        // Reciprocal matching is evaluated across unambiguous male/female demographics
        if (!candidateUnresolved)
        {
            if (!MatchesShowMe(callerPrefs.GenderPreference, candidateNormalized!))
            {
                exclusions.Add(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch);
            }
        }

        if (!callerUnresolved)
        {
            if (!MatchesShowMe(candidatePrefs.GenderPreference, callerNormalized!))
            {
                exclusions.Add(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch);
            }
        }
    }

    private static void EvaluateDistance(
        int? distanceKm,
        SocialExplorePreferences callerPrefs,
        SocialExplorePreferences candidatePrefs,
        List<SocialCandidateExclusionReason> exclusions,
        List<SocialCandidateIndeterminateReason> indeterminates)
    {
        if (distanceKm is null)
        {
            indeterminates.Add(SocialCandidateIndeterminateReason.DistanceUnavailable);
            return;
        }

        var distance = distanceKm.Value;

        // Caller's preferred distance range applied to approximate distance
        if (distance < callerPrefs.MinDistanceKm || distance > callerPrefs.MaxDistanceKm)
        {
            exclusions.Add(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch);
        }

        // Candidate's preferred distance range applied to approximate distance
        if (distance < candidatePrefs.MinDistanceKm || distance > candidatePrefs.MaxDistanceKm)
        {
            exclusions.Add(SocialCandidateExclusionReason.CandidateDistancePreferenceMismatch);
        }
    }

    private static string? NormalizeDemographicGender(string? gender)
    {
        var trimmed = gender?.Trim();

        if (string.Equals(trimmed, DemographicMale, StringComparison.OrdinalIgnoreCase))
        {
            return DemographicMale;
        }

        if (string.Equals(trimmed, DemographicFemale, StringComparison.OrdinalIgnoreCase))
        {
            return DemographicFemale;
        }

        return null;
    }

    private static bool MatchesShowMe(SocialGenderPreference preference, string demographicGender) =>
        preference switch
        {
            SocialGenderPreference.All => true,
            SocialGenderPreference.Men => demographicGender == DemographicMale,
            SocialGenderPreference.Women => demographicGender == DemographicFemale,
            _ => false
        };

    private static IReadOnlyList<string> CalculateSharedLanguages(
        IReadOnlyList<string> callerLanguages,
        IReadOnlyList<string> candidateLanguages)
    {
        if (callerLanguages.Count == 0 || candidateLanguages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var candidateSet = new HashSet<string>(candidateLanguages, StringComparer.OrdinalIgnoreCase);
        var shared = callerLanguages
            .Where(candidateSet.Contains)
            .ToList();

        return Array.AsReadOnly(shared.ToArray());
    }

    private static IReadOnlyList<string> CalculateSharedConnectionGoals(
        IReadOnlyList<string> callerGoals,
        IReadOnlyList<string> candidateGoals)
    {
        if (callerGoals.Count == 0 || candidateGoals.Count == 0)
        {
            return Array.Empty<string>();
        }

        var candidateSet = new HashSet<string>(candidateGoals, StringComparer.OrdinalIgnoreCase);
        var shared = callerGoals
            .Where(candidateSet.Contains)
            .ToList();

        return Array.AsReadOnly(shared.ToArray());
    }
}
