using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, privacy-protected offline input representation of a candidate evaluated during Social Explore matching.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Privacy Boundaries:</b><br />
/// This class is a private offline domain input model for matching pipelines. It is not a client-facing DTO,
/// an entity model, or a transport payload. All data properties are annotated with <see cref="JsonIgnoreAttribute"/>
/// to prevent accidental serialization, and diagnostic <see cref="ToString"/> returns a constant non-sensitive value.
/// </para>
/// <para>
/// <b>Demographic and Identity Invariants:</b><br />
/// <list type="bullet">
/// <item><description>No account identifiers, Supabase user subjects, emails, or phone numbers are accepted.</description></item>
/// <item><description>No raw questionnaire answers, answer IDs, or option selections are accepted.</description></item>
/// <item><description>No birthdates, GPS coordinates, or exact locations are accepted; only approximate distance in kilometres.</description></item>
/// <item><description>Underage candidate ages are accepted at input construction so the downstream reciprocal evaluator can deterministically exclude them.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialCandidateMatchingInput : IEquatable<SocialCandidateMatchingInput>
{
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
    /// Gets the candidate's age in whole years.
    /// </summary>
    [JsonIgnore]
    public int CandidateAge { get; }

    /// <summary>
    /// Gets the candidate's demographic gender string (e.g. "male", "female"), or <see langword="null"/> if unrecorded.
    /// </summary>
    [JsonIgnore]
    public string? CandidateGender { get; }

    /// <summary>
    /// Gets the candidate's validated Social Explore preferences.
    /// </summary>
    [JsonIgnore]
    public SocialExplorePreferences CandidatePreferences { get; }

    /// <summary>
    /// Gets the approximate distance in kilometres between the caller and candidate, or <see langword="null"/> if unavailable.
    /// </summary>
    [JsonIgnore]
    public int? ApproximateDistanceKm { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialCandidateMatchingInput"/> class.
    /// </summary>
    /// <param name="candidateProfileId">The unique identifier of the candidate's Social profile. Must not be empty.</param>
    /// <param name="displayName">The candidate's display name. Must not be null, empty, or whitespace-only.</param>
    /// <param name="candidateAge">The candidate's age in whole years.</param>
    /// <param name="candidateGender">The candidate's demographic gender string, or null.</param>
    /// <param name="candidatePreferences">The candidate's validated explore preferences. Must not be null.</param>
    /// <param name="approximateDistanceKm">The optional approximate distance in kilometres. If specified, must be non-negative.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="candidateProfileId"/> is empty or <paramref name="displayName"/> is whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="displayName"/> or <paramref name="candidatePreferences"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="approximateDistanceKm"/> is negative.</exception>
    public SocialCandidateMatchingInput(
        Guid candidateProfileId,
        string displayName,
        int candidateAge,
        string? candidateGender,
        SocialExplorePreferences candidatePreferences,
        int? approximateDistanceKm = null)
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

        ArgumentNullException.ThrowIfNull(candidatePreferences);

        if (approximateDistanceKm < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approximateDistanceKm),
                approximateDistanceKm,
                "Approximate distance cannot be negative.");
        }

        CandidateProfileId = candidateProfileId;
        DisplayName = displayName.Trim();
        CandidateAge = candidateAge;
        CandidateGender = candidateGender;
        CandidatePreferences = candidatePreferences;
        ApproximateDistanceKm = approximateDistanceKm;
    }

    /// <summary>
    /// Determines whether this instance and another specified <see cref="SocialCandidateMatchingInput"/> have the same values.
    /// </summary>
    public bool Equals(SocialCandidateMatchingInput? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return CandidateProfileId == other.CandidateProfileId &&
               string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
               CandidateAge == other.CandidateAge &&
               string.Equals(CandidateGender, other.CandidateGender, StringComparison.OrdinalIgnoreCase) &&
               CandidatePreferences.Equals(other.CandidatePreferences) &&
               ApproximateDistanceKm == other.ApproximateDistanceKm;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is SocialCandidateMatchingInput other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CandidateProfileId);
        hash.Add(DisplayName, StringComparer.Ordinal);
        hash.Add(CandidateAge);
        hash.Add(CandidateGender, StringComparer.OrdinalIgnoreCase);
        hash.Add(CandidatePreferences);
        hash.Add(ApproximateDistanceKm);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares two <see cref="SocialCandidateMatchingInput"/> instances for equality.
    /// </summary>
    public static bool operator ==(SocialCandidateMatchingInput? left, SocialCandidateMatchingInput? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="SocialCandidateMatchingInput"/> instances for inequality.
    /// </summary>
    public static bool operator !=(SocialCandidateMatchingInput? left, SocialCandidateMatchingInput? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential candidate information.
    /// </summary>
    public override string ToString() =>
        "SocialCandidateMatchingInput [private]";
}

/// <summary>
/// Pure, deterministic offline domain pipeline orchestrating candidate eligibility evaluation,
/// snapshot projection, and sequence rotation for Social Explore.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic Offline Transformation:</b><br />
/// This service provides an offline, in-memory pipeline connecting three verified domain components:
/// <see cref="SocialCandidateFilterEvaluator"/>, <see cref="SocialExploreCandidateCard"/>, and <see cref="SocialCandidateRotator"/>.
/// It evaluates caller and candidate demographics and preferences, filters out excluded and indeterminate candidates,
/// converts eligible candidates into immutable private snapshots, and rotates the resulting sequence deterministically.
/// </para>
/// <para>
/// <b>Strict Security, Access, and Architectural Boundaries:</b><br />
/// This pipeline is strictly an offline domain transformation utility. It does <b>not</b> establish:
/// <list type="bullet">
/// <item><description>Authenticated caller identity or session validity.</description></item>
/// <item><description>Social profile ownership or profile verification.</description></item>
/// <item><description>Dating module opt-in or active module membership.</description></item>
/// <item><description>Profile completion or current policy consent.</description></item>
/// <item><description>Account standing, suspension status, or trust score.</description></item>
/// <item><description>Candidate visibility, discoverability flags, or ghost mode settings.</description></item>
/// <item><description>Bidirectional block-list clearance or mutual unmatch history.</description></item>
/// <item><description>Self-exclusion, existing relationship state, or active connection status.</description></item>
/// <item><description>Mutual match authorization, match notifications, or like/pass recording.</description></item>
/// <item><description>Chat, voice call, or messaging permissions.</description></item>
/// <item><description>Authorization to fetch candidate profile data from repositories.</description></item>
/// <item><description>Authorization to transmit or expose candidate snapshots to clients.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Production Integration Prerequisites:</b><br />
/// The caller must satisfy separately approved M2 (Account/Auth) and M6 (Security/Moderation) contracts
/// before any candidate matching pipeline can be integrated into production flows.
/// Serializing a candidate collection directly into public API responses could disclose cardinality or confidential
/// candidate existence; therefore, the returned collection must <b>never</b> be published directly through an API.
/// This service must not be registered in dependency injection containers, exposed via controllers, or mapped to public DTOs.
/// </para>
/// <para>
/// <b>Prohibited Features:</b><br />
/// In accordance with Project Decisions and M5 Scope:
/// <list type="bullet">
/// <item><description>No compatibility scoring, percentages, or personality weights (Decision P-04 remains pending).</description></item>
/// <item><description>No questionnaire answer comparison or participation badge attachment.</description></item>
/// <item><description>No database queries, EF Core contexts, or PostgreSQL migrations.</description></item>
/// <item><description>No algorithmic candidate ranking, AI recommendations, or machine-learning ordering.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialCandidateMatchingService
{
    /// <summary>
    /// Evaluates candidate eligibility, constructs private snapshots for eligible candidates,
    /// and deterministically rotates the resulting sequence.
    /// </summary>
    /// <param name="callerAge">The caller's age in whole years.</param>
    /// <param name="callerGender">The caller's demographic gender string (e.g. "male", "female"), or <see langword="null"/>.</param>
    /// <param name="callerPreferences">The caller's validated Social Explore preferences. Must not be <see langword="null"/>.</param>
    /// <param name="candidates">The batch of candidate input records to evaluate. Must not be <see langword="null"/> or contain <see langword="null"/> elements.</param>
    /// <param name="rotationOffset">The non-negative sequence rotation offset applied to eligible candidates.</param>
    /// <returns>A defensive, immutable read-only list of rotated <see cref="SocialExploreCandidateCard"/> snapshots.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callerPreferences"/> or <paramref name="candidates"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="rotationOffset"/> is negative.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="candidates"/> contains a <see langword="null"/> element.</exception>
    public static IReadOnlyList<SocialExploreCandidateCard> BuildSnapshots(
        int callerAge,
        string? callerGender,
        SocialExplorePreferences callerPreferences,
        IReadOnlyList<SocialCandidateMatchingInput> candidates,
        int rotationOffset)
    {
        // Step 1: Validate batch inputs
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

        // Step 2 & 3: Evaluate candidates with fail-closed eligibility
        var eligibleCards = new List<SocialExploreCandidateCard>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            var evaluation = SocialCandidateFilterEvaluator.Evaluate(
                callerAge,
                callerGender,
                callerPreferences,
                candidate.CandidateAge,
                candidate.CandidateGender,
                candidate.CandidatePreferences,
                candidate.ApproximateDistanceKm);

            // Step 3: Apply fail-closed eligibility (Eligible only; omit Excluded and Indeterminate)
            if (evaluation.Status == SocialCandidateMatchStatus.Eligible)
            {
                // Step 4: Create private snapshot
                var card = SocialExploreCandidateCard.Create(
                    candidate.CandidateProfileId,
                    candidate.DisplayName,
                    candidate.CandidateAge,
                    candidate.ApproximateDistanceKm,
                    evaluation);

                eligibleCards.Add(card);
            }
        }

        // Step 5 & 6: Rotate after filtering and return defensive read-only collection
        return SocialCandidateRotator.Rotate(eligibleCards, rotationOffset);
    }
}
