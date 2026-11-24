using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable representation of an individual Social participant's directional interest decision toward a target candidate within a canonical profile pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// <list type="bullet">
/// <item><description>This model represents a single directional interest record (e.g., like or pass).</description></item>
/// <item><description>The acting profile must be a validated member of the canonical <see cref="SocialProfilePairKey"/>.</description></item>
/// <item><description>It does NOT constitute a mutual match or grant chat/messaging authorization (e.g., M4 access evidence).</description></item>
/// <item><description>It does NOT authorize caller identity or verify upstream M2/M6 eligibility or block-list standing.</description></item>
/// <item><description>All identifier properties, pair references, and directional interest decisions are annotated with <see cref="JsonIgnoreAttribute"/>, and diagnostic <see cref="ToString"/> returns a non-sensitive constant.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialDirectionalInterest : IEquatable<SocialDirectionalInterest>
{
    /// <summary>
    /// Gets the canonical unordered profile pair key.
    /// </summary>
    [JsonIgnore]
    public SocialProfilePairKey PairKey { get; }

    /// <summary>
    /// Gets the profile identity of the participant expressing directional interest.
    /// </summary>
    [JsonIgnore]
    public Guid ActingProfileId { get; }

    /// <summary>
    /// Gets the profile identity of the target candidate.
    /// </summary>
    [JsonIgnore]
    public Guid TargetProfileId => PairKey.GetOtherProfileId(ActingProfileId);

    /// <summary>
    /// Gets a value indicating whether the directional interest is positive (e.g., like) or negative (e.g., pass).
    /// </summary>
    [JsonIgnore]
    public bool IsPositive { get; }

    /// <summary>
    /// Gets the UTC timestamp at which the interest decision was recorded.
    /// </summary>
    [JsonIgnore]
    public DateTime RecordedAtUtc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialDirectionalInterest"/> class.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key. Must not be null.</param>
    /// <param name="actingProfileId">The acting profile identity expressing interest. Must belong to <paramref name="pairKey"/>.</param>
    /// <param name="isPositive">The explicit directional interest decision (<c>true</c> for positive/like, <c>false</c> for negative/pass).</param>
    /// <param name="recordedAtUtc">The UTC timestamp of the decision.</param>
    public SocialDirectionalInterest(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive,
        DateTime recordedAtUtc)
    {
        PairKey = pairKey ?? throw new ArgumentNullException(nameof(pairKey));

        if (actingProfileId == Guid.Empty)
        {
            throw new ArgumentException("Acting profile identity cannot be empty.", nameof(actingProfileId));
        }

        if (!pairKey.Contains(actingProfileId))
        {
            throw new ArgumentException("Acting profile identity must belong to the specified profile pair key.", nameof(actingProfileId));
        }

        if (recordedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Recorded timestamp must have UTC DateTimeKind.", nameof(recordedAtUtc));
        }

        if (recordedAtUtc == default || recordedAtUtc == DateTime.MinValue || recordedAtUtc == DateTime.MaxValue)
        {
            throw new ArgumentException("Recorded timestamp must be a valid UTC timestamp.", nameof(recordedAtUtc));
        }

        ActingProfileId = actingProfileId;
        IsPositive = isPositive;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>
    /// Creates a validated <see cref="SocialDirectionalInterest"/> instance with an explicit timestamp.
    /// </summary>
    public static SocialDirectionalInterest Create(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive,
        DateTime recordedAtUtc) =>
        new(pairKey, actingProfileId, isPositive, recordedAtUtc);

    /// <summary>
    /// Creates a validated <see cref="SocialDirectionalInterest"/> instance stamped with current UTC time.
    /// </summary>
    public static SocialDirectionalInterest Create(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive) =>
        new(pairKey, actingProfileId, isPositive, DateTime.UtcNow);

    /// <summary>
    /// Determines whether this instance and another specified <see cref="SocialDirectionalInterest"/> have the same values.
    /// </summary>
    public bool Equals(SocialDirectionalInterest? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return PairKey.Equals(other.PairKey)
            && ActingProfileId == other.ActingProfileId
            && IsPositive == other.IsPositive
            && RecordedAtUtc == other.RecordedAtUtc;
    }

    public override bool Equals(object? obj) =>
        obj is SocialDirectionalInterest other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(PairKey, ActingProfileId, IsPositive, RecordedAtUtc);

    public static bool operator ==(SocialDirectionalInterest? left, SocialDirectionalInterest? right) =>
        Equals(left, right);

    public static bool operator !=(SocialDirectionalInterest? left, SocialDirectionalInterest? right) =>
        !Equals(left, right);

    public override string ToString() =>
        "SocialDirectionalInterest [private]";
}
