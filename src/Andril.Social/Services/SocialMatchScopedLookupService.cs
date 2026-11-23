using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;

namespace Andril.Social.Services;

/// <summary>
/// Status outcome of a scoped Social match lookup.
/// </summary>
public enum SocialMatchLookupStatus
{
    /// <summary>Matching Social match found and verified strictly within requesting profile scope.</summary>
    Found,

    /// <summary>No matching match record exists within the requesting profile scope.</summary>
    NotFound,

    /// <summary>Returned match pair does not contain the requesting profile identity.</summary>
    ProfileIdMismatch,

    /// <summary>Returned match identity does not match the requested match ID.</summary>
    MatchIdMismatch,

    /// <summary>Requesting profile ID, match ID, or pair key is empty or invalid.</summary>
    InvalidIdentity,

    /// <summary>A repository or persistence dependency failure occurred.</summary>
    DependencyFailure
}

/// <summary>
/// Immutable, privacy-safe result of a scoped Social match lookup.
/// Conceals identities, pair keys, and timestamps from diagnostics and JSON serialization.
/// Does not grant caller authorization, prove profile ownership, or grant chat permissions.
/// </summary>
public sealed class SocialMatchLookupResult : IEquatable<SocialMatchLookupResult>
{
    /// <summary>Gets the validated Social match entity, or <see langword="null"/> if not found or rejected.</summary>
    [JsonIgnore]
    public SocialMatchEntity? Match { get; }

    /// <summary>Gets the lookup outcome status.</summary>
    [JsonIgnore]
    public SocialMatchLookupStatus Status { get; }

    /// <summary>Gets a safe, non-disclosing diagnostic message.</summary>
    [JsonIgnore]
    public string DiagnosticMessage { get; }

    /// <summary>Gets a value indicating whether a valid match was found within authorized scope.</summary>
    [JsonIgnore]
    public bool IsFound => Status == SocialMatchLookupStatus.Found && Match is not null;

    /// <summary>Gets a value indicating whether the record was absent within authorized scope.</summary>
    [JsonIgnore]
    public bool IsNotFound => Status == SocialMatchLookupStatus.NotFound;

    private SocialMatchLookupResult(SocialMatchEntity? match, SocialMatchLookupStatus status, string diagnosticMessage)
    {
        Match = match;
        Status = status;
        DiagnosticMessage = diagnosticMessage ?? string.Empty;
    }

    /// <summary>Creates a successful lookup result for an authorized match.</summary>
    public static SocialMatchLookupResult Success(SocialMatchEntity match) =>
        new(match ?? throw new ArgumentNullException(nameof(match)), SocialMatchLookupStatus.Found, "Match record verified within scope.");

    /// <summary>Creates a non-disclosing absence result.</summary>
    public static SocialMatchLookupResult NotFound() =>
        new(null, SocialMatchLookupStatus.NotFound, "No match record exists within scope.");

    /// <summary>Creates a result indicating the match pair does not correlate with the requesting profile.</summary>
    public static SocialMatchLookupResult ProfileIdMismatch() =>
        new(null, SocialMatchLookupStatus.ProfileIdMismatch, "Match profile scope mismatch.");

    /// <summary>Creates a result indicating the returned match ID does not match the requested ID.</summary>
    public static SocialMatchLookupResult MatchIdMismatch() =>
        new(null, SocialMatchLookupStatus.MatchIdMismatch, "Match identity mismatch.");

    /// <summary>Creates an invalid identity result.</summary>
    public static SocialMatchLookupResult InvalidIdentity(string message = "Invalid or empty identity supplied.") =>
        new(null, SocialMatchLookupStatus.InvalidIdentity, message);

    /// <summary>Creates a dependency failure result.</summary>
    public static SocialMatchLookupResult DependencyFailure(string message = "Persistence dependency failure.") =>
        new(null, SocialMatchLookupStatus.DependencyFailure, message);

    /// <summary>Determines whether this instance equals another.</summary>
    public bool Equals(SocialMatchLookupResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Status == other.Status
            && Equals(Match, other.Match)
            && string.Equals(DiagnosticMessage, other.DiagnosticMessage, StringComparison.Ordinal);
    }

    /// <summary>Determines whether this instance equals an object.</summary>
    public override bool Equals(object? obj) =>
        obj is SocialMatchLookupResult other && Equals(other);

    /// <summary>Returns the hash code for this instance.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(Status, Match, DiagnosticMessage);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SocialMatchLookupResult? left, SocialMatchLookupResult? right) =>
        Equals(left, right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SocialMatchLookupResult? left, SocialMatchLookupResult? right) =>
        !Equals(left, right);

    /// <summary>Returns a non-sensitive diagnostic string representation concealing private identities.</summary>
    public override string ToString() => $"SocialMatchLookupResult [{Status}]";
}

/// <summary>
/// Internal, read-only coordinator for retrieving Social match records within validated profile scopes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Scope Invariants:</b>
/// <list type="bullet">
/// <item><description>The requesting profile ID is strictly an entity correlation scope, NOT proof of authentication.</description></item>
/// <item><description>Does NOT verify caller identity, adult eligibility (18+), explicit opt-in, or active profile standing.</description></item>
/// <item><description>Does NOT determine whether a match is currently active, unmatch status, or revocation standing.</description></item>
/// <item><description>Does NOT grant chat, voice, video, or communication access tokens (CF-20 State 4).</description></item>
/// <item><description>Fails closed on repository exceptions and preserves operation cancellation.</description></item>
/// <item><description>Does not conflate absent records (NotFound) with authorization failures or dependency errors.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class SocialMatchScopedLookupService
{
    private readonly ISocialMatchRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialMatchScopedLookupService"/> class.
    /// </summary>
    /// <param name="repository">The match persistence repository contract.</param>
    public SocialMatchScopedLookupService(ISocialMatchRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Looks up a Social match record by match ID within an authorized requesting profile scope.
    /// </summary>
    /// <param name="matchId">The match identifier to look up.</param>
    /// <param name="requestingProfileId">The requesting Social profile scope identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A validated, privacy-safe <see cref="SocialMatchLookupResult"/>.</returns>
    public async Task<SocialMatchLookupResult> GetMatchByIdAsync(
        Guid matchId,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (matchId == Guid.Empty || requestingProfileId == Guid.Empty)
        {
            return SocialMatchLookupResult.InvalidIdentity();
        }

        SocialMatchEntity? match;
        try
        {
            match = await _repository.GetMatchByIdAsync(matchId, requestingProfileId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SocialMatchLookupResult.DependencyFailure();
        }

        if (match is null)
        {
            return SocialMatchLookupResult.NotFound();
        }

        if (match.MatchId != matchId)
        {
            return SocialMatchLookupResult.MatchIdMismatch();
        }

        if (match.PairKey.FirstProfileId != requestingProfileId && match.PairKey.SecondProfileId != requestingProfileId)
        {
            return SocialMatchLookupResult.ProfileIdMismatch();
        }

        return SocialMatchLookupResult.Success(match);
    }

    /// <summary>
    /// Looks up a Social match record by canonical pair key within an authorized requesting profile scope.
    /// </summary>
    /// <param name="pairKey">The canonical profile pair key to look up.</param>
    /// <param name="requestingProfileId">The requesting Social profile scope identifier.</param>
    /// <param name="cancellationToken">A token to observe for operation cancellation.</param>
    /// <returns>A validated, privacy-safe <see cref="SocialMatchLookupResult"/>.</returns>
    public async Task<SocialMatchLookupResult> GetMatchByPairKeyAsync(
        SocialProfilePairKey pairKey,
        Guid requestingProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (pairKey is null || requestingProfileId == Guid.Empty)
        {
            return SocialMatchLookupResult.InvalidIdentity();
        }

        if (pairKey.FirstProfileId != requestingProfileId && pairKey.SecondProfileId != requestingProfileId)
        {
            return SocialMatchLookupResult.ProfileIdMismatch();
        }

        SocialMatchEntity? match;
        try
        {
            match = await _repository.GetMatchByPairKeyAsync(pairKey, requestingProfileId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SocialMatchLookupResult.DependencyFailure();
        }

        if (match is null)
        {
            return SocialMatchLookupResult.NotFound();
        }

        if (!match.PairKey.Equals(pairKey))
        {
            return SocialMatchLookupResult.ProfileIdMismatch();
        }

        if (match.PairKey.FirstProfileId != requestingProfileId && match.PairKey.SecondProfileId != requestingProfileId)
        {
            return SocialMatchLookupResult.ProfileIdMismatch();
        }

        return SocialMatchLookupResult.Success(match);
    }
}
