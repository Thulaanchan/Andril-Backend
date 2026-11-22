using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, canonicalized value object representing an unordered pair of distinct private Social profile identities.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Canonicalization:</b><br />
/// This value object represents an unordered pair of Social profile identities. To ensure order-independent equality,
/// hashing, and comparison, the two profile identifiers are automatically canonicalized into sorted order upon creation.
/// As a result, <c>new SocialProfilePairKey(a, b)</c> and <c>new SocialProfilePairKey(b, a)</c> represent the exact same key.
/// </para>
/// <para>
/// <b>Security and Privacy Boundaries:</b><br />
/// <list type="bullet">
/// <item><description>This object is strictly an offline identity-pair projection.</description></item>
/// <item><description>It does NOT verify that either GUID belongs to an active, verified, or authorized Social profile.</description></item>
/// <item><description>It does NOT establish a mutual match, directional interest, relationship consent, or block-list clearance.</description></item>
/// <item><description>It does NOT grant chat, messaging, or communication authorization (e.g. M4's access evidence).</description></item>
/// <item><description>It does NOT accept or store directional likes, timestamps, internal account IDs, or cross-module identities.</description></item>
/// <item><description>All identifier properties are annotated with <see cref="JsonIgnoreAttribute"/>, and diagnostic <see cref="ToString"/> returns a constant non-sensitive value.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialProfilePairKey : IEquatable<SocialProfilePairKey>, IComparable<SocialProfilePairKey>
{
    /// <summary>
    /// Gets the canonical first Social profile identity (lesser of the two GUIDs).
    /// </summary>
    [JsonIgnore]
    public Guid FirstProfileId { get; }

    /// <summary>
    /// Gets the canonical second Social profile identity (greater of the two GUIDs).
    /// </summary>
    [JsonIgnore]
    public Guid SecondProfileId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialProfilePairKey"/> class, canonicalizing the pair order.
    /// </summary>
    /// <param name="profileIdA">The first Social profile identity. Must not be empty.</param>
    /// <param name="profileIdB">The second Social profile identity. Must not be empty and must be distinct from <paramref name="profileIdA"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="profileIdA"/> is <see cref="Guid.Empty"/>,
    /// <paramref name="profileIdB"/> is <see cref="Guid.Empty"/>,
    /// or <paramref name="profileIdA"/> and <paramref name="profileIdB"/> are equal.
    /// </exception>
    public SocialProfilePairKey(Guid profileIdA, Guid profileIdB)
    {
        if (profileIdA == Guid.Empty)
        {
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileIdA));
        }

        if (profileIdB == Guid.Empty)
        {
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileIdB));
        }

        if (profileIdA == profileIdB)
        {
            throw new ArgumentException("Profile identities in a pair key must be distinct.", nameof(profileIdB));
        }

        if (profileIdA.CompareTo(profileIdB) <= 0)
        {
            FirstProfileId = profileIdA;
            SecondProfileId = profileIdB;
        }
        else
        {
            FirstProfileId = profileIdB;
            SecondProfileId = profileIdA;
        }
    }

    /// <summary>
    /// Creates a validated, canonicalized <see cref="SocialProfilePairKey"/> instance.
    /// </summary>
    /// <param name="profileIdA">The first Social profile identity. Must not be empty.</param>
    /// <param name="profileIdB">The second Social profile identity. Must not be empty and must be distinct from <paramref name="profileIdA"/>.</param>
    /// <returns>A validated <see cref="SocialProfilePairKey"/> instance.</returns>
    public static SocialProfilePairKey Create(Guid profileIdA, Guid profileIdB) =>
        new(profileIdA, profileIdB);

    /// <summary>
    /// Determines whether the specified profile identity is one of the members of this pair key.
    /// </summary>
    /// <param name="profileId">The profile identity to check.</param>
    /// <returns><see langword="true"/> if the identity is present in this pair; otherwise, <see langword="false"/>.</returns>
    public bool Contains(Guid profileId) =>
        FirstProfileId == profileId || SecondProfileId == profileId;

    /// <summary>
    /// Gets the opposing profile identity in the pair given one of its member identities.
    /// </summary>
    /// <param name="profileId">One of the member profile identities in this pair.</param>
    /// <returns>The other member profile identity in this pair.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="profileId"/> is not a member of this pair key.</exception>
    public Guid GetOtherProfileId(Guid profileId)
    {
        if (FirstProfileId == profileId)
        {
            return SecondProfileId;
        }

        if (SecondProfileId == profileId)
        {
            return FirstProfileId;
        }

        throw new ArgumentException("The specified profile identity is not a member of this pair key.", nameof(profileId));
    }

    /// <summary>
    /// Determines whether this instance and another specified <see cref="SocialProfilePairKey"/> have the same canonical values.
    /// </summary>
    public bool Equals(SocialProfilePairKey? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return FirstProfileId == other.FirstProfileId && SecondProfileId == other.SecondProfileId;
    }

    /// <summary>
    /// Determines whether the specified object is equal to the current instance.
    /// </summary>
    public override bool Equals(object? obj) =>
        obj is SocialProfilePairKey other && Equals(other);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    public override int GetHashCode() =>
        HashCode.Combine(FirstProfileId, SecondProfileId);

    /// <summary>
    /// Compares two <see cref="SocialProfilePairKey"/> instances for canonical order.
    /// </summary>
    public int CompareTo(SocialProfilePairKey? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;

        var firstComparison = FirstProfileId.CompareTo(other.FirstProfileId);
        if (firstComparison != 0) return firstComparison;

        return SecondProfileId.CompareTo(other.SecondProfileId);
    }

    /// <summary>
    /// Compares two <see cref="SocialProfilePairKey"/> instances for equality.
    /// </summary>
    public static bool operator ==(SocialProfilePairKey? left, SocialProfilePairKey? right) =>
        Equals(left, right);

    /// <summary>
    /// Compares two <see cref="SocialProfilePairKey"/> instances for inequality.
    /// </summary>
    public static bool operator !=(SocialProfilePairKey? left, SocialProfilePairKey? right) =>
        !Equals(left, right);

    /// <summary>
    /// Returns a non-sensitive diagnostic string representation concealing confidential profile identifiers.
    /// </summary>
    public override string ToString() =>
        "SocialProfilePairKey [private]";
}
