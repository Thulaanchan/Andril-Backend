using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Interfaces;
using Andril.Social.Matching;

namespace Andril.API.Authorization.Social;

/// <summary>
/// Provider contract for retrieving authoritative participant discovery and standing evidence for relationship operations.
/// </summary>
public interface ISocialRelationshipEvidenceProvider
{
    /// <summary>
    /// Retrieves authoritative discovery eligibility evidence for the discovering caller profile.
    /// </summary>
    Task<SocialDiscoveryCallerEvidence?> GetCallerEvidenceAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves authoritative discovery eligibility evidence for the target candidate profile relative to the caller.
    /// </summary>
    Task<SocialDiscoveryCandidateEvidence?> GetCandidateEvidenceAsync(
        Guid candidateProfileId,
        Guid callerProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an authorized directional interest operation.
/// </summary>
public sealed class SocialRelationshipOperationResult
{
    /// <summary>
    /// Gets a value indicating whether the interest operation succeeded.
    /// </summary>
    [JsonIgnore]
    public bool Success { get; }

    /// <summary>
    /// Gets a value indicating whether a mutual match was established.
    /// </summary>
    [JsonIgnore]
    public bool MatchCreated { get; }

    /// <summary>
    /// Gets the unique identifier of the mutual match, revealed ONLY if a mutual match was established.
    /// </summary>
    [JsonIgnore]
    public Guid? MatchId { get; }

    /// <summary>
    /// Gets the visible relationship state. Never reveals unreciprocated likes or candidate rejection.
    /// </summary>
    [JsonIgnore]
    public SocialRelationshipState VisibleState { get; }

    private SocialRelationshipOperationResult(
        bool success,
        bool matchCreated,
        Guid? matchId,
        SocialRelationshipState visibleState)
    {
        Success = success;
        MatchCreated = matchCreated;
        MatchId = matchId;
        VisibleState = visibleState;
    }

    /// <summary>
    /// Creates a result representing a recorded interest where no mutual match was formed.
    /// </summary>
    public static SocialRelationshipOperationResult InterestRecorded(SocialRelationshipState state) =>
        new(success: true, matchCreated: false, matchId: null, visibleState: state);

    /// <summary>
    /// Creates a result representing an established mutual match.
    /// </summary>
    public static SocialRelationshipOperationResult MatchEstablished(Guid matchId) =>
        new(success: true, matchCreated: true, matchId: matchId, visibleState: SocialRelationshipState.Matched);

    /// <summary>
    /// Creates a result representing a failed operation.
    /// </summary>
    public static SocialRelationshipOperationResult Failed(SocialRelationshipState state = SocialRelationshipState.None) =>
        new(success: false, matchCreated: false, matchId: null, visibleState: state);

    /// <inheritdoc />
    public override string ToString() => "SocialRelationshipOperationResult [private]";
}

/// <summary>
/// Public, privacy-preserving snapshot of relationship status visible to the authenticated caller.
/// </summary>
public sealed class SocialRelationshipStatusResult
{
    /// <summary>
    /// Gets a value indicating whether an active mutual match exists with the target profile.
    /// </summary>
    [JsonIgnore]
    public bool HasActiveMatch { get; }

    /// <summary>
    /// Gets the unique match identifier if an active mutual match exists; otherwise <see langword="null"/>.
    /// </summary>
    [JsonIgnore]
    public Guid? MatchId { get; }

    /// <summary>
    /// Gets the UTC timestamp of match formation, if matched.
    /// </summary>
    [JsonIgnore]
    public DateTime? MatchedAtUtc { get; }

    private SocialRelationshipStatusResult(bool hasActiveMatch, Guid? matchId, DateTime? matchedAtUtc)
    {
        HasActiveMatch = hasActiveMatch;
        MatchId = matchId;
        MatchedAtUtc = matchedAtUtc;
    }

    /// <summary>Creates a matched status result.</summary>
    public static SocialRelationshipStatusResult Matched(Guid matchId, DateTime matchedAtUtc) =>
        new(hasActiveMatch: true, matchId: matchId, matchedAtUtc: matchedAtUtc);

    /// <summary>Creates a non-matched status result, concealing unreciprocated directional decisions.</summary>
    public static SocialRelationshipStatusResult NotMatched() =>
        new(hasActiveMatch: false, matchId: null, matchedAtUtc: null);

    /// <inheritdoc />
    public override string ToString() => "SocialRelationshipStatusResult [private]";
}

/// <summary>
/// Result of an authorized match revocation (unmatch) operation.
/// </summary>
public sealed class SocialRevocationOperationResult
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

    public SocialRevocationOperationResult(bool success, bool matchRevoked)
    {
        Success = success;
        MatchRevoked = matchRevoked;
    }

    /// <inheritdoc />
    public override string ToString() => "SocialRevocationOperationResult [private]";
}

/// <summary>
/// Contract for authorized pairwise Social dating interest, status, and match lifecycle operations.
/// </summary>
public interface ISocialRelationshipAccessService
{
    /// <summary>
    /// Records the authenticated caller's directional interest toward a target candidate and conditionally creates a match.
    /// </summary>
    Task<SocialRelationshipOperationResult> RecordInterestAsync(
        Guid targetProfileId,
        bool isPositive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the privacy-preserving relationship status between the authenticated caller and a target candidate.
    /// </summary>
    Task<SocialRelationshipStatusResult> GetRelationshipStatusAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes an existing mutual match between the authenticated caller and a target candidate.
    /// </summary>
    Task<SocialRevocationOperationResult> RevokeMatchAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates owner-authorized Social interest recording, reciprocal evaluation, mutual match formation,
/// and unmatch operations while enforcing module boundary isolation, legal age, target visibility,
/// bidirectional blocking, and strict unilateral interest privacy.
/// </summary>
public sealed class SocialRelationshipAccessService : ISocialRelationshipAccessService
{
    private const string SocialModule = "social";

    private readonly IPrivateProfileService _profileService;
    private readonly ISocialRelationshipTransactionRepository _transactionRepository;
    private readonly ISocialRelationshipEvidenceProvider _evidenceProvider;

    public SocialRelationshipAccessService(
        IPrivateProfileService profileService,
        ISocialRelationshipTransactionRepository transactionRepository,
        ISocialRelationshipEvidenceProvider evidenceProvider)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _transactionRepository = transactionRepository ?? throw new ArgumentNullException(nameof(transactionRepository));
        _evidenceProvider = evidenceProvider ?? throw new ArgumentNullException(nameof(evidenceProvider));
    }

    /// <inheritdoc />
    public async Task<SocialRelationshipOperationResult> RecordInterestAsync(
        Guid targetProfileId,
        bool isPositive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            if (targetProfileId == Guid.Empty || targetProfileId == profile.ProfileId)
            {
                throw new ProfileAccessDeniedException();
            }

            // 1. Retrieve authoritative evidence for both participants
            var actorEvidence = await _evidenceProvider.GetCallerEvidenceAsync(profile.ProfileId, cancellationToken);
            var candidateEvidence = await _evidenceProvider.GetCandidateEvidenceAsync(targetProfileId, profile.ProfileId, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (actorEvidence is null || candidateEvidence is null)
            {
                throw new ProfileAccessDeniedException();
            }

            var pairKey = SocialProfilePairKey.Create(profile.ProfileId, targetProfileId);

            // 2. Evaluate deterministic transition policy preflight
            var transitionResult = SocialRelationshipTransitionPolicy.EvaluateTransition(
                pairKey,
                profile.ProfileId,
                isPositive,
                existingActingInterest: null,
                reciprocalInterest: null,
                existingMatch: null,
                actorEvidence,
                candidateEvidence);

            if (transitionResult.IsDenied)
            {
                throw new ProfileAccessDeniedException();
            }

            // 3. Execute atomic transaction
            var txResult = await _transactionRepository.RecordInterestAndTryMatchAsync(
                pairKey,
                profile.ProfileId,
                isPositive,
                DateTime.UtcNow,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (txResult is null || !txResult.Success)
            {
                return SocialRelationshipOperationResult.Failed();
            }

            if (txResult.MatchCreated)
            {
                if (txResult.Match is null || txResult.Match.MatchId == Guid.Empty)
                {
                    return SocialRelationshipOperationResult.Failed();
                }

                if (!pairKey.Equals(txResult.Match.PairKey))
                {
                    return SocialRelationshipOperationResult.Failed();
                }

                if (txResult.ResultingState != SocialRelationshipState.Matched)
                {
                    return SocialRelationshipOperationResult.Failed();
                }

                return SocialRelationshipOperationResult.MatchEstablished(txResult.Match.MatchId);
            }

            if (txResult.Match is not null || txResult.ResultingState == SocialRelationshipState.Matched)
            {
                return SocialRelationshipOperationResult.Failed();
            }

            if (txResult.ResultingState is not (SocialRelationshipState.PendingUnilateral or SocialRelationshipState.Passed))
            {
                return SocialRelationshipOperationResult.Failed();
            }

            // Unilateral privacy: never reveal whether the counterpart liked or passed
            var visibleState = isPositive ? SocialRelationshipState.PendingUnilateral : SocialRelationshipState.Passed;
            return SocialRelationshipOperationResult.InterestRecorded(visibleState);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SocialRelationshipStatusResult> GetRelationshipStatusAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            if (targetProfileId == Guid.Empty || targetProfileId == profile.ProfileId)
            {
                throw new ProfileAccessDeniedException();
            }

            var pairKey = SocialProfilePairKey.Create(profile.ProfileId, targetProfileId);
            var snapshot = await _transactionRepository.GetRelationshipStateAsync(pairKey, profile.ProfileId, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (snapshot is null || !pairKey.Equals(snapshot.PairKey))
            {
                return SocialRelationshipStatusResult.NotMatched();
            }

            if (snapshot.State == SocialRelationshipState.Matched && snapshot.ActiveMatch is not null)
            {
                if (snapshot.ActiveMatch.MatchId == Guid.Empty || !pairKey.Equals(snapshot.ActiveMatch.PairKey))
                {
                    return SocialRelationshipStatusResult.NotMatched();
                }

                return SocialRelationshipStatusResult.Matched(snapshot.ActiveMatch.MatchId, snapshot.ActiveMatch.CreatedAtUtc);
            }

            // Privacy: do not reveal unilateral interest or unreciprocated decisions to the caller
            return SocialRelationshipStatusResult.NotMatched();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SocialRevocationOperationResult> RevokeMatchAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            if (targetProfileId == Guid.Empty || targetProfileId == profile.ProfileId)
            {
                throw new ProfileAccessDeniedException();
            }

            var pairKey = SocialProfilePairKey.Create(profile.ProfileId, targetProfileId);
            var revokeResult = await _transactionRepository.RevokeMatchAsync(pairKey, profile.ProfileId, DateTime.UtcNow, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            if (revokeResult is null || !revokeResult.Success || (!revokeResult.Success && revokeResult.MatchRevoked))
            {
                return new SocialRevocationOperationResult(success: false, matchRevoked: false);
            }

            return new SocialRevocationOperationResult(revokeResult.Success, revokeResult.MatchRevoked);
        }, cancellationToken);
    }

    private static void ValidateProfileScope(PrivateProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.ProfileId == Guid.Empty || !string.Equals(profile.Module, SocialModule, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileAccessDeniedException();
        }
    }
}
