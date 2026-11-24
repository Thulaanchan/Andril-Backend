using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Matching;

namespace Andril.Social.Interfaces;

/// <summary>
/// State classification for a pairwise Social dating relationship.
/// </summary>
public enum SocialRelationshipState
{
    /// <summary>
    /// No interaction or interest decision recorded by either participant.
    /// </summary>
    None = 0,

    /// <summary>
    /// Exactly one participant has recorded an interest decision. Directional intent remains strictly confidential.
    /// </summary>
    PendingUnilateral = 1,

    /// <summary>
    /// Both participants have recorded reciprocal affirmative interest and an active mutual match exists.
    /// </summary>
    Matched = 2,

    /// <summary>
    /// At least one participant recorded a negative decision (pass), preventing mutual match creation.
    /// </summary>
    Passed = 3,

    /// <summary>
    /// An established mutual match was revoked (unmatched) by one of the participants.
    /// </summary>
    Revoked = 4
}

/// <summary>
/// Point-in-time snapshot of the relationship state between two Social profiles within a canonical pair.
/// </summary>
public sealed class SocialRelationshipSnapshot : IEquatable<SocialRelationshipSnapshot>
{
    /// <summary>
    /// Gets the canonical profile pair key.
    /// </summary>
    [JsonIgnore]
    public SocialProfilePairKey PairKey { get; }

    /// <summary>
    /// Gets the classified relationship state.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipState State { get; }

    /// <summary>
    /// Gets the active mutual match entity if state is <see cref="SocialRelationshipState.Matched"/>; otherwise <see langword="null"/>.
    /// </summary>
    [JsonIgnore]
    public SocialMatchEntity? ActiveMatch { get; }

    /// <summary>
    /// Gets the UTC timestamp when the relationship state was last modified, if known.
    /// </summary>
    [JsonIgnore]
    public DateTime? LastUpdatedUtc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialRelationshipSnapshot"/> class.
    /// </summary>
    public SocialRelationshipSnapshot(
        SocialProfilePairKey pairKey,
        SocialRelationshipState state,
        SocialMatchEntity? activeMatch = null,
        DateTime? lastUpdatedUtc = null)
    {
        PairKey = pairKey ?? throw new ArgumentNullException(nameof(pairKey));
        State = state;
        ActiveMatch = activeMatch;

        if (lastUpdatedUtc.HasValue && lastUpdatedUtc.Value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Last updated timestamp must have UTC DateTimeKind.", nameof(lastUpdatedUtc));
        }

        LastUpdatedUtc = lastUpdatedUtc;
    }

    /// <inheritdoc />
    public bool Equals(SocialRelationshipSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return PairKey.Equals(other.PairKey)
            && State == other.State
            && Equals(ActiveMatch, other.ActiveMatch)
            && Nullable.Equals(LastUpdatedUtc, other.LastUpdatedUtc);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is SocialRelationshipSnapshot other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(PairKey, (int)State, ActiveMatch, LastUpdatedUtc);

    /// <inheritdoc />
    public override string ToString() => "SocialRelationshipSnapshot [private]";
}

/// <summary>
/// Result of an atomic interest recording and conditional match creation operation.
/// </summary>
public sealed class SocialInterestTransactionResult
{
    /// <summary>
    /// Gets a value indicating whether the transaction was committed successfully.
    /// </summary>
    [JsonIgnore]
    public bool Success { get; }

    /// <summary>
    /// Gets a value indicating whether this transaction resulted in the creation of a new mutual match.
    /// </summary>
    [JsonIgnore]
    public bool MatchCreated { get; }

    /// <summary>
    /// Gets the newly created or existing mutual match entity, or <see langword="null"/> if no match exists.
    /// </summary>
    [JsonIgnore]
    public SocialMatchEntity? Match { get; }

    /// <summary>
    /// Gets the resulting relationship state after the transaction commit.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipState ResultingState { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialInterestTransactionResult"/> class.
    /// </summary>
    public SocialInterestTransactionResult(
        bool success,
        bool matchCreated,
        SocialMatchEntity? match,
        SocialRelationshipState resultingState)
    {
        Success = success;
        MatchCreated = matchCreated;
        Match = match;
        ResultingState = resultingState;
    }

    /// <summary>
    /// Creates a successful interest recording result where no mutual match was formed.
    /// </summary>
    public static SocialInterestTransactionResult InterestRecorded(SocialRelationshipState state) =>
        new(success: true, matchCreated: false, match: null, resultingState: state);

    /// <summary>
    /// Creates a successful interest recording result where a mutual match was established.
    /// </summary>
    public static SocialInterestTransactionResult MatchEstablished(SocialMatchEntity match) =>
        new(success: true, matchCreated: true, match: match ?? throw new ArgumentNullException(nameof(match)), resultingState: SocialRelationshipState.Matched);

    /// <inheritdoc />
    public override string ToString() => "SocialInterestTransactionResult [private]";
}

/// <summary>
/// Result of an atomic match revocation (unmatch) operation.
/// </summary>
public sealed class SocialRevocationTransactionResult
{
    /// <summary>
    /// Gets a value indicating whether the revocation operation succeeded.
    /// </summary>
    [JsonIgnore]
    public bool Success { get; }

    /// <summary>
    /// Gets a value indicating whether an active match was found and revoked.
    /// </summary>
    [JsonIgnore]
    public bool MatchRevoked { get; }

    /// <summary>
    /// Gets the identifier of the revoked match, if applicable.
    /// </summary>
    [JsonIgnore]
    public Guid? RevokedMatchId { get; }

    /// <summary>
    /// Gets the UTC timestamp when the revocation was executed.
    /// </summary>
    [JsonIgnore]
    public DateTime RevokedAtUtc { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialRevocationTransactionResult"/> class.
    /// </summary>
    public SocialRevocationTransactionResult(
        bool success,
        bool matchRevoked,
        Guid? revokedMatchId,
        DateTime revokedAtUtc)
    {
        Success = success;
        MatchRevoked = matchRevoked;
        RevokedMatchId = revokedMatchId;
        RevokedAtUtc = revokedAtUtc;
    }

    /// <inheritdoc />
    public override string ToString() => "SocialRevocationTransactionResult [private]";
}

/// <summary>
/// Internal backend persistence repository contract for atomic Social interest transactions,
/// reciprocal queries, mutual match formation, and revocation boundaries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Architectural and Security Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Atomic Transaction Boundary:</b> Interest recording and mutual match formation execute within a single
/// atomic transaction to prevent race conditions (e.g. concurrent mutual likes creating duplicate matches).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Reciprocal Affirmative Prerequisite:</b> A mutual match cannot be formed unless both participants
/// have recorded affirmative (positive) interest.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Strict Unilateral Privacy:</b> Directional interest decisions remain strictly confidential.
/// A participant is never informed of another participant's interest unless a mutual match is formed.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Mandatory Upstream Authorization:</b> Upstream services must verify profile identity,
/// age eligibility (18+), active Dating opt-in, and bidirectional blocking standing before invoking repository methods.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Pair-Scoped Isolation:</b> All operations are strictly scoped to a canonical <see cref="SocialProfilePairKey"/>.
/// Cross-pair queries and multi-tenant bulk modifications are prohibited.
/// </description>
/// </item>
/// </list>
/// </para>
/// </remarks>
public interface ISocialRelationshipTransactionRepository
{
    /// <summary>
    /// Asynchronously retrieves the deterministic active relationship snapshot for a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="requestingProfileId">The authenticated profile identity requesting the state.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A snapshot of the pairwise relationship state.</returns>
    Task<SocialRelationshipSnapshot> GetRelationshipStateAsync(
        SocialProfilePairKey pairKey,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the directional interest recorded by the acting profile within a canonical profile pair.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The acting profile identity.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The recorded directional interest, or <see langword="null"/> if none has been recorded.</returns>
    Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the reciprocal interest recorded by the other participant within a canonical profile pair.
    /// Used internally by matching transactions; must never be exposed directly to unreciprocated callers.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The acting profile identity whose counterpart's interest is to be inspected.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The reciprocal directional interest, or <see langword="null"/> if none has been recorded.</returns>
    Task<SocialDirectionalInterest?> GetReciprocalInterestAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously records an acting profile's directional interest and, within the same atomic transaction,
    /// evaluates reciprocal interest to form a mutual match if both parties have expressed affirmative interest.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The acting profile identity recording interest.</param>
    /// <param name="isPositive">True for positive interest (like); false for negative (pass).</param>
    /// <param name="timestampUtc">The UTC timestamp of the interest decision.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The atomic transaction outcome, indicating whether a match was created.</returns>
    Task<SocialInterestTransactionResult> RecordInterestAndTryMatchAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive,
        DateTime timestampUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously revokes (unmatches) an active mutual match within an atomic transaction.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="actingProfileId">The participant initiating the revocation.</param>
    /// <param name="timestampUtc">The UTC timestamp of the revocation.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>The outcome of the revocation transaction.</returns>
    Task<SocialRevocationTransactionResult> RevokeMatchAsync(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        DateTime timestampUtc,
        CancellationToken cancellationToken = default);
}
