using System;
using System.Text.Json.Serialization;
using Andril.Social.Interfaces;

namespace Andril.Social.Matching;

/// <summary>
/// Classified internal reason for revoking an established match or relationship standing.
/// Kept strictly confidential and never disclosed to the counterpart.
/// </summary>
public enum SocialRevocationReason
{
    /// <summary>Voluntary unmatch initiated directly by a participant.</summary>
    UserInitiated,

    /// <summary>Unmatch triggered by a participant blocking the counterpart.</summary>
    Blocked,

    /// <summary>Unmatch triggered by participant opt-out from Social discovery.</summary>
    OptedOut,

    /// <summary>Unmatch triggered by account deactivation, closure, or suspension.</summary>
    AccountDeactivated,

    /// <summary>Unmatch triggered by age invalidation or legal age revocation.</summary>
    UnderageDetected
}

/// <summary>
/// Status outcome of a relationship revocation evaluation.
/// </summary>
public enum SocialRevocationStatus
{
    /// <summary>Active mutual match was valid and has been successfully terminated.</summary>
    Success,

    /// <summary>Match was already previously revoked; repeated action is safely idempotent.</summary>
    AlreadyRevoked,

    /// <summary>No active match existed between participants; safe idempotent outcome.</summary>
    NotMatched,

    /// <summary>Initiating actor is an outsider not belonging to the canonical pair; operation is denied.</summary>
    UnauthorizedOutsider,

    /// <summary>Pair key or participant identity is invalid, empty, or mismatched.</summary>
    InvalidPair
}

/// <summary>
/// Immutable, privacy-preserving outcome of a relationship revocation evaluation.
/// </summary>
public sealed class SocialRevocationDecision : IEquatable<SocialRevocationDecision>
{
    /// <summary>
    /// Gets the status outcome of the revocation evaluation.
    /// </summary>
    [JsonIgnore]
    public SocialRevocationStatus Status { get; }

    /// <summary>
    /// Gets the resulting relationship state after revocation.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipState ResultingState { get; }

    /// <summary>
    /// Gets the internal reason code. Never disclosed externally.
    /// </summary>
    [JsonIgnore]
    public SocialRevocationReason Reason { get; }

    /// <summary>
    /// Gets a value indicating whether the revocation request is permissible and valid.
    /// </summary>
    [JsonIgnore]
    public bool IsAllowed => Status is SocialRevocationStatus.Success or SocialRevocationStatus.AlreadyRevoked or SocialRevocationStatus.NotMatched;

    /// <summary>
    /// Gets a value indicating whether an active match was actually terminated by this operation.
    /// </summary>
    [JsonIgnore]
    public bool MatchTerminated => Status == SocialRevocationStatus.Success;

    /// <summary>
    /// Gets a value indicating whether the request was denied due to authorization or structural failure.
    /// </summary>
    [JsonIgnore]
    public bool IsDenied => Status is SocialRevocationStatus.UnauthorizedOutsider or SocialRevocationStatus.InvalidPair;

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialRevocationDecision"/> class.
    /// </summary>
    public SocialRevocationDecision(
        SocialRevocationStatus status,
        SocialRelationshipState resultingState,
        SocialRevocationReason reason)
    {
        Status = status;
        ResultingState = resultingState;
        Reason = reason;
    }

    /// <inheritdoc />
    public bool Equals(SocialRevocationDecision? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status
            && ResultingState == other.ResultingState
            && Reason == other.Reason;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is SocialRevocationDecision other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine((int)Status, (int)ResultingState, (int)Reason);

    /// <inheritdoc />
    public override string ToString() => "SocialRevocationDecision [private]";
}

/// <summary>
/// Deterministic domain policy governing match revocation, unilateral unmatching, outsider denial,
/// blocking invalidation, and reason confidentiality.
/// </summary>
/// <remarks>
/// <para>
/// <b>Core Invariants:</b>
/// <list type="bullet">
/// <item><description><b>Bilateral Agency:</b> Either participant in an active match has the unilateral authority to unmatch at any time.</description></item>
/// <item><description><b>Third-Party Denial:</b> Non-participants are strictly denied any authority to revoke or alter a pair's relationship standing.</description></item>
/// <item><description><b>Immediate Invalidation:</b> Blocking, deactivation, or opt-out immediately terminates active match standing.</description></item>
/// <item><description><b>Idempotency:</b> Repeated unmatch requests for already-revoked or non-matched pairs succeed safely without error.</description></item>
/// <item><description><b>Reason Confidentiality:</b> Specific revocation reasons (e.g. block or report) are recorded internally and never disclosed to counterpart.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialRelationshipRevocationPolicy
{
    /// <summary>
    /// Evaluates an unmatch or relationship revocation request against current pairwise relationship state.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key.</param>
    /// <param name="initiatingProfileId">The identity of the actor initiating revocation.</param>
    /// <param name="currentSnapshot">The point-in-time snapshot of the relationship between the profiles.</param>
    /// <param name="reason">The underlying reason for revocation.</param>
    /// <returns>A deterministic <see cref="SocialRevocationDecision"/>.</returns>
    public static SocialRevocationDecision EvaluateRevocation(
        SocialProfilePairKey? pairKey,
        Guid initiatingProfileId,
        SocialRelationshipSnapshot? currentSnapshot,
        SocialRevocationReason reason = SocialRevocationReason.UserInitiated)
    {
        // 1. Structural and identity validation
        if (pairKey is null || initiatingProfileId == Guid.Empty)
        {
            return new SocialRevocationDecision(
                SocialRevocationStatus.InvalidPair,
                SocialRelationshipState.None,
                reason);
        }

        // 2. Third-party outsider denial: initiating actor MUST belong to the canonical pair
        if (!pairKey.Contains(initiatingProfileId))
        {
            var fallbackState = currentSnapshot?.State ?? SocialRelationshipState.None;
            return new SocialRevocationDecision(
                SocialRevocationStatus.UnauthorizedOutsider,
                fallbackState,
                reason);
        }

        // 3. Current snapshot pair alignment check
        if (currentSnapshot is not null && !pairKey.Equals(currentSnapshot.PairKey))
        {
            return new SocialRevocationDecision(
                SocialRevocationStatus.InvalidPair,
                SocialRelationshipState.None,
                reason);
        }

        var currentState = currentSnapshot?.State ?? SocialRelationshipState.None;

        // 4. Active match revocation
        if (currentState == SocialRelationshipState.Matched)
        {
            return new SocialRevocationDecision(
                SocialRevocationStatus.Success,
                SocialRelationshipState.Revoked,
                reason);
        }

        // 5. Already revoked (idempotent repeat)
        if (currentState == SocialRelationshipState.Revoked)
        {
            return new SocialRevocationDecision(
                SocialRevocationStatus.AlreadyRevoked,
                SocialRelationshipState.Revoked,
                reason);
        }

        // 6. Non-matched state (None, PendingUnilateral, Passed)
        return new SocialRevocationDecision(
            SocialRevocationStatus.NotMatched,
            currentState,
            reason);
    }
}
