using System;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, validated domain value object representing dual-handle Age and Distance range filters for Social Explore.
/// </summary>
public sealed class SocialExploreRangeFilters : IEquatable<SocialExploreRangeFilters>
{
    public const int AbsoluteMinAge = 18;
    public const int AbsoluteMaxAge = 99;
    public const int DefaultMinAge = 18;
    public const int DefaultMaxAge = 35;

    public const int AbsoluteMinDistanceKm = 0;
    public const int AbsoluteMaxDistanceKm = 999;
    public const int DefaultMinDistanceKm = 0;
    public const int DefaultMaxDistanceKm = 100;

    public static SocialExploreRangeFilters Default { get; } = new(
        DefaultMinAge,
        DefaultMaxAge,
        DefaultMinDistanceKm,
        DefaultMaxDistanceKm);

    public int MinAge { get; }
    public int MaxAge { get; }
    public int MinDistanceKm { get; }
    public int MaxDistanceKm { get; }

    public SocialExploreRangeFilters(
        int minAge = DefaultMinAge,
        int maxAge = DefaultMaxAge,
        int minDistanceKm = DefaultMinDistanceKm,
        int maxDistanceKm = DefaultMaxDistanceKm)
    {
        ValidateAge(minAge, maxAge);
        ValidateDistance(minDistanceKm, maxDistanceKm);

        MinAge = minAge;
        MaxAge = maxAge;
        MinDistanceKm = minDistanceKm;
        MaxDistanceKm = maxDistanceKm;
    }

    public static SocialExploreRangeFilters Create(
        int minAge,
        int maxAge,
        int minDistanceKm,
        int maxDistanceKm) =>
        new(minAge, maxAge, minDistanceKm, maxDistanceKm);

    private static void ValidateAge(int minAge, int maxAge)
    {
        if (minAge < AbsoluteMinAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minAge),
                minAge,
                $"Minimum age cannot be less than {AbsoluteMinAge}.");
        }

        if (maxAge > AbsoluteMaxAge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                $"Maximum age cannot exceed {AbsoluteMaxAge}.");
        }

        if (minAge > maxAge)
        {
            throw new ArgumentException(
                $"Minimum age ({minAge}) cannot be greater than maximum age ({maxAge}).",
                nameof(minAge));
        }
    }

    private static void ValidateDistance(int minDistanceKm, int maxDistanceKm)
    {
        if (minDistanceKm < AbsoluteMinDistanceKm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minDistanceKm),
                minDistanceKm,
                $"Minimum distance cannot be less than {AbsoluteMinDistanceKm} km.");
        }

        if (maxDistanceKm > AbsoluteMaxDistanceKm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDistanceKm),
                maxDistanceKm,
                $"Maximum distance cannot exceed {AbsoluteMaxDistanceKm} km.");
        }

        if (minDistanceKm > maxDistanceKm)
        {
            throw new ArgumentException(
                $"Minimum distance ({minDistanceKm} km) cannot be greater than maximum distance ({maxDistanceKm} km).",
                nameof(minDistanceKm));
        }
    }

    public bool Equals(SocialExploreRangeFilters? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return MinAge == other.MinAge &&
               MaxAge == other.MaxAge &&
               MinDistanceKm == other.MinDistanceKm &&
               MaxDistanceKm == other.MaxDistanceKm;
    }

    public override bool Equals(object? obj) =>
        obj is SocialExploreRangeFilters other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(MinAge, MaxAge, MinDistanceKm, MaxDistanceKm);

    public static bool operator ==(SocialExploreRangeFilters? left, SocialExploreRangeFilters? right) =>
        Equals(left, right);

    public static bool operator !=(SocialExploreRangeFilters? left, SocialExploreRangeFilters? right) =>
        !Equals(left, right);

    public override string ToString() =>
        $"Age: {MinAge}–{MaxAge}, Distance: {MinDistanceKm}–{MaxDistanceKm} km";
}
