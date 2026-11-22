using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, privacy-protected internal domain projection representing an eligible Social Explore candidate snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Architectural Boundaries:</b><br />
/// This class is an internal-use domain projection designed for candidate sequencing and matching pipelines.
/// Despite its public C# accessibility, it is <b>not</b> a client-facing DTO, a verified discoverability result,
/// or an authorization decision. It must never be exposed directly through an API controller or serialized into
/// client responses without separately approved visibility rules, M2 authorization, and privacy-safe projection mapping.
/// </para>
/// <para>
/// <b>Evaluation Prerequisites:</b><br />
/// Instances can only be constructed from a <see cref="SocialCandidateEvaluationResult"/> with <see cref="SocialCandidateMatchStatus.Eligible"/>.
/// An <c>Eligible</c> status verifies only that the evaluator's currently implemented reciprocal hard filters passed.
/// It does <b>not</b> establish account standing, active membership, opt-in consent, discoverability, block-list clearance,
/// M2 profile authorization, or permission to transmit candidate data to a client.
/// </para>
/// <para>
/// <b>Privacy and Confidentiality Safeguards:</b><br />
/// All public data properties are annotated with <see cref="JsonIgnoreAttribute"/> to prevent accidental public JSON serialization.
/// The class strictly conceals sensitive demographic and identity information:
/// <list type="bullet">
/// <item><description>No raw questionnaire responses, answer IDs, or option selections are accepted or retained.</description></item>
/// <item><description>No questionnaire participation badges, completion statuses, or skipped flags are exposed.</description></item>
/// <item><description>No compatibility scores, percentages, ranking weights, or synthetic metrics are computed or stored.</description></item>
/// <item><description>No internal account IDs, Supabase subjects, email addresses, or phone numbers are accepted.</description></item>
/// <item><description>Age is stored as an integer (18+); exact dates of birth and precise GPS coordinates are excluded.</description></item>
/// <item><description>Diagnostic <see cref="ToString"/> returns a constant non-sensitive string.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialExploreCandidateCard : IEquatable<SocialExploreCandidateCard>
{
    private SocialExploreCandidateCard(
        Guid candidateProfileId,
        string displayName,
        int age,
        int? approximateDistanceKm,
        IReadOnlyList<string> sharedLanguages,
        IReadOnlyList<string> sharedConnectionGoals)
    {
        CandidateProfileId = candidateProfileId;
        DisplayName = displayName;
        Age = age;
        ApproximateDistanceKm = approximateDistanceKm;
        SharedLanguages = sharedLanguages;
        SharedConnectionGoals = sharedConnectionGoals;
    }

    /// <summary>
    /// Gets the unique identifier of the candidate's private Social profile.
    /// </summary>
    [JsonIgnore]
    public Guid CandidateProfileId { get; }

    /// <summary>
    /// Gets the candidate's validated Social display name.
    /// </summary>
    [JsonIgnore]
    public string DisplayName { get; }

    /// <summary>
    /// Gets the candidate's age in whole years. Must be 18 or older.
    /// </summary>
    [JsonIgnore]
    public int Age { get; }

    /// <summary>
    /// Gets the approximate distance in kilometres between users, or <see langword="null"/> if distance is unrecorded.
    /// </summary>
    [JsonIgnore]
    public int? ApproximateDistanceKm { get; }

    /// <summary>
    /// Gets the defensive read-only collection of shared communication languages evaluated between users.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> SharedLanguages { get; }

    /// <summary>
    /// Gets the defensive read-only collection of shared relationship and connection goals evaluated between users.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> SharedConnectionGoals { get; }

    /// <summary>
    /// Creates a validated, immutable <see cref="SocialExploreCandidateCard"/> snapshot for an eligible candidate.
    /// </summary>
    /// <param name="candidateProfileId">The unique identifier of the candidate's private Social profile. Must not be empty.</param>
    /// <param name="displayName">The candidate's validated display name. Must not be null or whitespace.</param>
    /// <param name="age">The candidate's age in whole years. Must be 18 or older.</param>
    /// <param name="approximateDistanceKm">The optional approximate distance in kilometres. If specified, must be non-negative.</param>
    /// <param name="evaluationResult">The candidate filter evaluation result. Must be non-null and have <see cref="SocialCandidateMatchStatus.Eligible"/> status.</param>
    /// <returns>A validated, immutable <see cref="SocialExploreCandidateCard"/> instance.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="candidateProfileId"/> is empty, <paramref name="displayName"/> is whitespace, or <paramref name="evaluationResult"/> is not <see cref="SocialCandidateMatchStatus.Eligible"/>.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="displayName"/> or <paramref name="evaluationResult"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="age"/> is less than 18, or <paramref name="approximateDistanceKm"/> is negative.</exception>
    public static SocialExploreCandidateCard Create(
        Guid candidateProfileId,
        string displayName,
        int age,
        int? approximateDistanceKm,
        SocialCandidateEvaluationResult evaluationResult)
    {
        if (candidateProfileId == Guid.Empty)
        {
            throw new ArgumentException("Candidate profile identity cannot be empty.", nameof(candidateProfileId));
        }

        ArgumentNullException.ThrowIfNull(displayName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Candidate display name cannot be whitespace.", nameof(displayName));
        }

        if (age < 18)
        {
            throw new ArgumentOutOfRangeException(
                nameof(age),
                age,
                "Candidate age must be 18 or older.");
        }

        if (approximateDistanceKm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approximateDistanceKm),
                approximateDistanceKm,
                "Approximate distance cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(evaluationResult);

        if (evaluationResult.Status != SocialCandidateMatchStatus.Eligible)
        {
            throw new ArgumentException(
                $"A candidate card snapshot can only be created for an eligible evaluation result. Actual status: {evaluationResult.Status}.",
                nameof(evaluationResult));
        }

        var sharedLanguagesSnapshot = Array.AsReadOnly(
            (evaluationResult.SharedLanguages ?? Array.Empty<string>()).ToArray());

        var sharedConnectionGoalsSnapshot = Array.AsReadOnly(
            (evaluationResult.SharedConnectionGoals ?? Array.Empty<string>()).ToArray());

        return new SocialExploreCandidateCard(
            candidateProfileId,
            displayName.Trim(),
            age,
            approximateDistanceKm,
            sharedLanguagesSnapshot,
            sharedConnectionGoalsSnapshot);
    }

    /// <summary>
    /// Determines whether this instance and another specified <see cref="SocialExploreCandidateCard"/> have the same values.
    /// </summary>
    public bool Equals(SocialExploreCandidateCard? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return CandidateProfileId == other.CandidateProfileId &&
               string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
               Age == other.Age &&
               ApproximateDistanceKm == other.ApproximateDistanceKm &&
               SharedLanguages.SequenceEqual(other.SharedLanguages, StringComparer.Ordinal) &&
               SharedConnectionGoals.SequenceEqual(other.SharedConnectionGoals, StringComparer.Ordinal);
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is SocialExploreCandidateCard other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CandidateProfileId);
        hash.Add(DisplayName, StringComparer.Ordinal);
        hash.Add(Age);
        hash.Add(ApproximateDistanceKm);
        foreach (var lang in SharedLanguages) hash.Add(lang, StringComparer.Ordinal);
        foreach (var goal in SharedConnectionGoals) hash.Add(goal, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares two <see cref="SocialExploreCandidateCard"/> instances for equality.
    /// </summary>
    public static bool operator ==(SocialExploreCandidateCard? left, SocialExploreCandidateCard? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="SocialExploreCandidateCard"/> instances for inequality.
    /// </summary>
    public static bool operator !=(SocialExploreCandidateCard? left, SocialExploreCandidateCard? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential candidate information.
    /// </summary>
    public override string ToString() =>
        "SocialExploreCandidateCard [private]";
}
