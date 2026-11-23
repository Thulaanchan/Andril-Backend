using System;

namespace Andril.Social.Matching;

/// <summary>
/// Status outcome of validating a retrieved directional interest record against the requested pair and acting profile scope.
/// </summary>
public enum SocialInterestResultScopeStatus
{
    /// <summary>Returned interest record strictly belongs to the requested pair and acting profile.</summary>
    Valid,

    /// <summary>No interest record was returned (valid absent decision state).</summary>
    NullResult,

    /// <summary>Returned interest record does not belong to the requested canonical profile pair.</summary>
    PairMismatch,

    /// <summary>Returned interest record belongs to a different participant than the requested acting profile.</summary>
    ActingProfileMismatch,

    /// <summary>Acting profile is not a member of the canonical pair key (unrelated profile).</summary>
    UnrelatedProfile,

    /// <summary>One or more requested profile identifiers are empty or malformed.</summary>
    InvalidIdentity
}

/// <summary>
/// Immutable, privacy-safe result of directional interest result scope validation.
/// </summary>
public sealed class SocialInterestResultScopeValidation : IEquatable<SocialInterestResultScopeValidation>
{
    /// <summary>Gets the scope validation status outcome.</summary>
    public SocialInterestResultScopeStatus Status { get; }

    /// <summary>
    /// Gets a value indicating whether the result is scope-valid (either a matching record or a valid null result).
    /// </summary>
    public bool IsValid => Status == SocialInterestResultScopeStatus.Valid || Status == SocialInterestResultScopeStatus.NullResult;

    /// <summary>
    /// Gets a value indicating whether an actual directional interest decision was verified within scope.
    /// </summary>
    public bool HasDecision => Status == SocialInterestResultScopeStatus.Valid;

    private SocialInterestResultScopeValidation(SocialInterestResultScopeStatus status)
    {
        Status = status;
    }

    /// <summary>Creates a successful scope validation result where a matching decision is present.</summary>
    public static SocialInterestResultScopeValidation Success() =>
        new(SocialInterestResultScopeStatus.Valid);

    /// <summary>Creates a successful scope validation result where no decision was recorded (null result).</summary>
    public static SocialInterestResultScopeValidation None() =>
        new(SocialInterestResultScopeStatus.NullResult);

    /// <summary>Creates a failed scope validation result with the specified error status.</summary>
    public static SocialInterestResultScopeValidation Fail(SocialInterestResultScopeStatus status) =>
        new(status);

    public bool Equals(SocialInterestResultScopeValidation? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Status == other.Status;
    }

    public override bool Equals(object? obj) =>
        obj is SocialInterestResultScopeValidation other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Status);

    public override string ToString() => "SocialInterestResultScopeValidation [private]";
}

/// <summary>
/// Deterministic offline policy validating that directional interest records returned from repositories
/// strictly belong to the requested canonical pair and acting Social profile scope (CF-20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// This policy performs pure result-shape validation. It does NOT establish:
/// <list type="bullet">
/// <item><description>Authenticated caller session or identity ownership.</description></item>
/// <item><description>Reciprocal interest or mutual-match authorization.</description></item>
/// <item><description>Permission to initiate chat, voice call, or messaging (e.g. M4 authorization).</description></item>
/// <item><description>Active profile standing, age eligibility, or moderation block clearance.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialInterestResultScopePolicy
{
    /// <summary>
    /// Validates that a retrieved directional interest record matches the requested canonical pair and acting profile.
    /// </summary>
    /// <param name="requestedPair">The requested canonical pair key.</param>
    /// <param name="requestedActingProfileId">The expected acting profile identity.</param>
    /// <param name="result">The retrieved interest record, or <see langword="null"/> if none recorded.</param>
    /// <returns>A deterministic <see cref="SocialInterestResultScopeValidation"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestedPair"/> is null.</exception>
    public static SocialInterestResultScopeValidation Validate(
        SocialProfilePairKey requestedPair,
        Guid requestedActingProfileId,
        SocialDirectionalInterest? result)
    {
        ArgumentNullException.ThrowIfNull(requestedPair);

        if (requestedActingProfileId == Guid.Empty)
        {
            return SocialInterestResultScopeValidation.Fail(SocialInterestResultScopeStatus.InvalidIdentity);
        }

        if (!requestedPair.Contains(requestedActingProfileId))
        {
            return SocialInterestResultScopeValidation.Fail(SocialInterestResultScopeStatus.UnrelatedProfile);
        }

        if (result is null)
        {
            return SocialInterestResultScopeValidation.None();
        }

        if (!requestedPair.Equals(result.PairKey))
        {
            return SocialInterestResultScopeValidation.Fail(SocialInterestResultScopeStatus.PairMismatch);
        }

        if (result.ActingProfileId != requestedActingProfileId)
        {
            return SocialInterestResultScopeValidation.Fail(SocialInterestResultScopeStatus.ActingProfileMismatch);
        }

        return SocialInterestResultScopeValidation.Success();
    }

    /// <summary>
    /// Validates that both records in a pair interest query strictly belong to the requested canonical pair.
    /// </summary>
    /// <param name="requestedPair">The requested canonical pair key.</param>
    /// <param name="firstInterest">The decision for the pair's first profile, or null.</param>
    /// <param name="secondInterest">The decision for the pair's second profile, or null.</param>
    /// <returns><c>true</c> if both records are scope-valid; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestedPair"/> is null.</exception>
    public static bool ValidatePairResult(
        SocialProfilePairKey requestedPair,
        SocialDirectionalInterest? firstInterest,
        SocialDirectionalInterest? secondInterest)
    {
        ArgumentNullException.ThrowIfNull(requestedPair);

        var firstValidation = Validate(requestedPair, requestedPair.FirstProfileId, firstInterest);
        if (!firstValidation.IsValid)
        {
            return false;
        }

        var secondValidation = Validate(requestedPair, requestedPair.SecondProfileId, secondInterest);
        return secondValidation.IsValid;
    }
}
