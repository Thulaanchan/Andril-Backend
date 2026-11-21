using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Andril.Social.Matching;

/// <summary>
/// Approved gender filter options for Social Explore.
/// </summary>
public enum SocialGenderPreference
{
    All,
    Men,
    Women
}

/// <summary>
/// Well-known constant definitions for Social Explore Show Me choices.
/// </summary>
public static class SocialGenderPreferences
{
    public const string Men = "Men";
    public const string Women = "Women";
    public const string All = "All";
    public const string Default = All;

    public static readonly IReadOnlyList<string> AllOptions =
        Array.AsReadOnly(new[]
        {
            Men,
            Women,
            All
        });

    public static bool IsValid(string? value) =>
        string.Equals(value, Men, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Women, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, All, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Men, StringComparison.OrdinalIgnoreCase)) return Men;
        if (string.Equals(value, Women, StringComparison.OrdinalIgnoreCase)) return Women;
        if (string.Equals(value, All, StringComparison.OrdinalIgnoreCase)) return All;

        throw new ArgumentException(
            $"Unsupported Show Me option '{value}'. Valid options are: {string.Join(", ", AllOptions)}.",
            nameof(value));
    }
}

/// <summary>
/// The eight approved communication languages for Social Explore matching.
/// </summary>
public static class SocialLanguages
{
    public const string English = "English";
    public const string Tamil = "Tamil";
    public const string Hindi = "Hindi";
    public const string Arabic = "Arabic";
    public const string French = "French";
    public const string Spanish = "Spanish";
    public const string German = "German";
    public const string MandarinChinese = "Mandarin Chinese";

    public static readonly IReadOnlyList<string> All =
        Array.AsReadOnly(new[]
        {
            English,
            Tamil,
            Hindi,
            Arabic,
            French,
            Spanish,
            German,
            MandarinChinese
        });

    public static bool IsValid(string? value) =>
        All.Any(lang => string.Equals(lang, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string value)
    {
        var trimmed = value?.Trim();
        var match = All.FirstOrDefault(lang => string.Equals(lang, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        throw new ArgumentException(
            $"Unsupported language '{value}'. Valid options are: {string.Join(", ", All)}.",
            nameof(value));
    }
}

/// <summary>
/// The eight approved relationship and connection goals for Social Explore matching.
/// </summary>
public static class SocialConnectionGoals
{
    public const string SeriousRelationship = "Serious Relationship";
    public const string LongTermPartner = "Long-term Partner";
    public const string LifePartner = "Life Partner";
    public const string Dating = "Dating";
    public const string Friendship = "Friendship";
    public const string MeaningfulConnection = "Meaningful Connection";
    public const string Companionship = "Companionship";
    public const string OpenToExplore = "Open to Explore";

    public static readonly IReadOnlyList<string> All =
        Array.AsReadOnly(new[]
        {
            SeriousRelationship,
            LongTermPartner,
            LifePartner,
            Dating,
            Friendship,
            MeaningfulConnection,
            Companionship,
            OpenToExplore
        });

    public static bool IsValid(string? value) =>
        All.Any(goal => string.Equals(goal, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string value)
    {
        var trimmed = value?.Trim();
        var match = All.FirstOrDefault(goal => string.Equals(goal, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        throw new ArgumentException(
            $"Unsupported connection goal '{value}'. Valid options are: {string.Join(", ", All)}.",
            nameof(value));
    }
}

/// <summary>
/// Immutable, validated domain model representing user preferences and search filters for Social Explore.
/// </summary>
public sealed class SocialExplorePreferences : IEquatable<SocialExplorePreferences>
{
    public static SocialExplorePreferences Default { get; } = new();

    public SocialExploreRangeFilters RangeFilters { get; }

    public int MinAge => RangeFilters.MinAge;
    public int MaxAge => RangeFilters.MaxAge;
    public int MinDistanceKm => RangeFilters.MinDistanceKm;
    public int MaxDistanceKm => RangeFilters.MaxDistanceKm;

    public string ShowMe { get; }
    public SocialGenderPreference GenderPreference { get; }

    public IReadOnlyList<string> Languages { get; }
    public IReadOnlyList<string> ConnectionGoals { get; }

    public SocialExplorePreferences(
        SocialExploreRangeFilters? rangeFilters = null,
        string? showMe = null,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null)
    {
        RangeFilters = rangeFilters ?? SocialExploreRangeFilters.Default;

        var normalizedShowMe = showMe is null
            ? SocialGenderPreferences.Default
            : SocialGenderPreferences.Normalize(showMe);

        ShowMe = normalizedShowMe;
        GenderPreference = Enum.Parse<SocialGenderPreference>(normalizedShowMe, ignoreCase: true);

        Languages = ValidateAndCopyLanguages(languages);
        ConnectionGoals = ValidateAndCopyConnectionGoals(connectionGoals);
    }

    public SocialExplorePreferences(
        SocialExploreRangeFilters? rangeFilters,
        SocialGenderPreference showMe,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null)
    {
        RangeFilters = rangeFilters ?? SocialExploreRangeFilters.Default;

        if (!Enum.IsDefined(typeof(SocialGenderPreference), showMe))
        {
            throw new ArgumentOutOfRangeException(nameof(showMe), showMe, "Invalid gender preference value.");
        }

        GenderPreference = showMe;
        ShowMe = showMe.ToString();

        Languages = ValidateAndCopyLanguages(languages);
        ConnectionGoals = ValidateAndCopyConnectionGoals(connectionGoals);
    }

    public static SocialExplorePreferences Create(
        SocialExploreRangeFilters? rangeFilters = null,
        string? showMe = null,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null) =>
        new(rangeFilters, showMe, languages, connectionGoals);

    public static SocialExplorePreferences Create(
        SocialExploreRangeFilters? rangeFilters,
        SocialGenderPreference showMe,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null) =>
        new(rangeFilters, showMe, languages, connectionGoals);

    private static IReadOnlyList<string> ValidateAndCopyLanguages(IEnumerable<string>? languages)
    {
        if (languages is null)
        {
            return Array.AsReadOnly(new[] { SocialLanguages.English });
        }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lang in languages)
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                throw new ArgumentException("Language entry cannot be null or blank.", nameof(languages));
            }

            var normalized = SocialLanguages.Normalize(lang);
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Duplicate language entry '{lang}'.", nameof(languages));
            }

            list.Add(normalized);
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("At least one language must always remain selected.", nameof(languages));
        }

        return Array.AsReadOnly(list.ToArray());
    }

    private static IReadOnlyList<string> ValidateAndCopyConnectionGoals(IEnumerable<string>? goals)
    {
        if (goals is null)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var goal in goals)
        {
            if (string.IsNullOrWhiteSpace(goal))
            {
                throw new ArgumentException("Connection goal cannot be null or blank.", nameof(goals));
            }

            var normalized = SocialConnectionGoals.Normalize(goal);
            if (!seen.Add(normalized))
            {
                throw new ArgumentException($"Duplicate connection goal '{goal}'.", nameof(goals));
            }

            list.Add(normalized);
        }

        return Array.AsReadOnly(list.ToArray());
    }

    public bool Equals(SocialExplorePreferences? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return RangeFilters.Equals(other.RangeFilters) &&
               string.Equals(ShowMe, other.ShowMe, StringComparison.Ordinal) &&
               Languages.SequenceEqual(other.Languages, StringComparer.Ordinal) &&
               ConnectionGoals.SequenceEqual(other.ConnectionGoals, StringComparer.Ordinal);
    }

    public override bool Equals(object? obj) =>
        obj is SocialExplorePreferences other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RangeFilters);
        hash.Add(ShowMe, StringComparer.Ordinal);
        foreach (var lang in Languages)
        {
            hash.Add(lang, StringComparer.Ordinal);
        }
        foreach (var goal in ConnectionGoals)
        {
            hash.Add(goal, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(SocialExplorePreferences? left, SocialExplorePreferences? right) =>
        Equals(left, right);

    public static bool operator !=(SocialExplorePreferences? left, SocialExplorePreferences? right) =>
        !Equals(left, right);

    public override string ToString() =>
        $"{RangeFilters}, ShowMe: {ShowMe}, Languages: [{string.Join(", ", Languages)}], Goals: [{string.Join(", ", ConnectionGoals)}]";
}
