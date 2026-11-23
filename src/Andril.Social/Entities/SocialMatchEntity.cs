using System;
using System.Text.Json.Serialization;
using Andril.Social.Matching;

namespace Andril.Social.Entities;

/// <summary>
/// Minimal immutable, persistence-neutral offline representation of Social match identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Security Boundaries:</b><br />
/// <list type="bullet">
/// <item><description>This entity is strictly an offline structural candidate representation of match identity.</description></item>
/// <item><description>It does NOT prove that an authoritative or valid match currently exists in persistent storage.</description></item>
/// <item><description>It does NOT grant chat, messaging, voice/video calls, or communication authorization (e.g. M4 access evidence).</description></item>
/// <item><description>It does NOT verify upstream M2/M6 profile standing, adult eligibility (18+), explicit opt-in, or block-list clearance.</description></item>
/// <item><description>All properties are annotated with <see cref="JsonIgnoreAttribute"/>, and diagnostic <see cref="ToString"/> returns a non-sensitive constant.</description></item>
/// <item><description>Contains no lifecycle status enums, match activation logic, unmatch tombstones, or compatibility weights.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialMatchEntity : IEquatable<SocialMatchEntity>
{
    /// <summary>
    /// Gets the unique match identifier.
    /// </summary>
    [JsonIgnore]
    public Guid MatchId { get; }

    /// <summary>
    /// Gets the canonical unordered profile pair key.
    /// </summary>
    [JsonIgnore]
    public SocialProfilePairKey PairKey { get; }

    /// <summary>
    /// Gets the explicit UTC timestamp recording when the match identity was created.
    /// </summary>
    [JsonIgnore]
    public DateTime CreatedAtUtc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialMatchEntity"/> class with an explicit UTC timestamp.
    /// </summary>
    /// <param name="matchId">The unique match identifier. Must not be empty.</param>
    /// <param name="pairKey">The canonical profile pair key. Must not be null.</param>
    /// <param name="createdAtUtc">The explicit UTC timestamp of creation.</param>
    public SocialMatchEntity(Guid matchId, SocialProfilePairKey pairKey, DateTime createdAtUtc)
    {
        if (matchId == Guid.Empty)
        {
            throw new ArgumentException("Match identifier cannot be empty.", nameof(matchId));
        }

        PairKey = pairKey ?? throw new ArgumentNullException(nameof(pairKey));

        if (createdAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Created timestamp must have UTC DateTimeKind.", nameof(createdAtUtc));
        }

        if (createdAtUtc == default || createdAtUtc == DateTime.MinValue || createdAtUtc == DateTime.MaxValue)
        {
            throw new ArgumentException("Created timestamp must be a valid UTC timestamp.", nameof(createdAtUtc));
        }

        MatchId = matchId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Creates a validated <see cref="SocialMatchEntity"/> instance with an explicit UTC timestamp.
    /// </summary>
    /// <param name="matchId">The unique match identifier. Must not be empty.</param>
    /// <param name="pairKey">The canonical profile pair key. Must not be null.</param>
    /// <param name="createdAtUtc">The explicit UTC timestamp of creation.</param>
    public static SocialMatchEntity Create(Guid matchId, SocialProfilePairKey pairKey, DateTime createdAtUtc) =>
        new(matchId, pairKey, createdAtUtc);

    /// <summary>
    /// Creates a validated <see cref="SocialMatchEntity"/> instance stamped with current UTC time.
    /// </summary>
    /// <param name="matchId">The unique match identifier. Must not be empty.</param>
    /// <param name="pairKey">The canonical profile pair key. Must not be null.</param>
    public static SocialMatchEntity Create(Guid matchId, SocialProfilePairKey pairKey) =>
        new(matchId, pairKey, DateTime.UtcNow);

    /// <summary>
    /// Determines whether this instance and another specified <see cref="SocialMatchEntity"/> have the same structural values.
    /// </summary>
    public bool Equals(SocialMatchEntity? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return MatchId == other.MatchId
            && PairKey.Equals(other.PairKey)
            && CreatedAtUtc == other.CreatedAtUtc;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is SocialMatchEntity other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode() =>
        HashCode.Combine(MatchId, PairKey, CreatedAtUtc);

    /// <summary>
    /// Compares two <see cref="SocialMatchEntity"/> instances for equality.
    /// </summary>
    public static bool operator ==(SocialMatchEntity? left, SocialMatchEntity? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="SocialMatchEntity"/> instances for inequality.
    /// </summary>
    public static bool operator !=(SocialMatchEntity? left, SocialMatchEntity? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing private identities.
    /// </summary>
    public override string ToString() => "SocialMatchEntity [private]";
}
