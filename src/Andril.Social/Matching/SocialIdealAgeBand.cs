using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, validated domain value object representing an optional ideal age band [p, q]
/// within an accepted age range [lo, hi] for Social matching compatibility evaluation (P-04).
/// </summary>
public sealed class SocialIdealAgeBand : IEquatable<SocialIdealAgeBand>
{
    [JsonIgnore]
    public int MinAge { get; }

    [JsonIgnore]
    public int MaxAge { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialIdealAgeBand"/> class.
    /// </summary>
    /// <param name="minAge">The ideal minimum age (p). Must be between 18 and 99.</param>
    /// <param name="maxAge">The ideal maximum age (q). Must be between 18 and 99, and >= minAge.</param>
    public SocialIdealAgeBand(int minAge, int maxAge)
    {
        if (minAge < SocialExploreRangeFilters.AbsoluteMinAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minAge),
                minAge,
                $"Ideal minimum age cannot be less than {SocialExploreRangeFilters.AbsoluteMinAge}.");
        }

        if (maxAge > SocialExploreRangeFilters.AbsoluteMaxAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                $"Ideal maximum age cannot exceed {SocialExploreRangeFilters.AbsoluteMaxAge}.");
        }

        if (minAge > maxAge)
        {
            throw new ArgumentException(
                $"Ideal minimum age ({minAge}) cannot be greater than ideal maximum age ({maxAge}).",
                nameof(minAge));
        }

        MinAge = minAge;
        MaxAge = maxAge;
    }

    /// <summary>
    /// Determines whether this ideal age band is fully within the specified accepted age range [lo, hi].
    /// </summary>
    public bool IsWithinAcceptedRange(int acceptedMinAge, int acceptedMaxAge) =>
        MinAge >= acceptedMinAge && MaxAge <= acceptedMaxAge;

    /// <summary>
    /// Evaluates candidate age x against this ideal age band [p, q] and accepted range [lo, hi].
    /// Returns 1.0 inside ideal band, (x - lo)/(p - lo) below, and (hi - x)/(hi - q) above.
    /// </summary>
    public double EvaluateCandidateAge(int candidateAge, int acceptedMinAge, int acceptedMaxAge)
    {
        if (candidateAge >= MinAge && candidateAge <= MaxAge)
        {
            return 1.0;
        }

        if (candidateAge < MinAge)
        {
            if (MinAge == acceptedMinAge)
            {
                return 1.0;
            }

            var score = (double)(candidateAge - acceptedMinAge) / (MinAge - acceptedMinAge);
            return Math.Clamp(score, 0.0, 1.0);
        }
        else // candidateAge > MaxAge
        {
            if (acceptedMaxAge == MaxAge)
            {
                return 1.0;
            }

            var score = (double)(acceptedMaxAge - candidateAge) / (acceptedMaxAge - MaxAge);
            return Math.Clamp(score, 0.0, 1.0);
        }
    }

    public bool Equals(SocialIdealAgeBand? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MinAge == other.MinAge && MaxAge == other.MaxAge;
    }

    public override bool Equals(object? obj) => obj is SocialIdealAgeBand other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(MinAge, MaxAge);

    public override string ToString() => "SocialIdealAgeBand [private]";
}
