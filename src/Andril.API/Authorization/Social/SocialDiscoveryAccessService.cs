using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Matching;

namespace Andril.API.Authorization.Social;

/// <summary>
/// Authoritative bundle containing the discovering caller's scoring profile evidence and revision snapshots.
/// </summary>
public sealed class SocialDiscoveryCallerBundle
{
    /// <summary>Gets the discovering caller's authoritative scoring profile evidence.</summary>
    public SocialScoringProfileEvidence Evidence { get; }

    /// <summary>Gets the discovering caller's cached evidence revision snapshot.</summary>
    public SocialEvidenceRevisionSnapshot CachedRevision { get; }

    /// <summary>Gets the discovering caller's committed evidence revision snapshot.</summary>
    public SocialEvidenceRevisionSnapshot CommittedRevision { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SocialDiscoveryCallerBundle"/> class.
    /// </summary>
    public SocialDiscoveryCallerBundle(
        SocialScoringProfileEvidence evidence,
        SocialEvidenceRevisionSnapshot cachedRevision,
        SocialEvidenceRevisionSnapshot committedRevision)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        CachedRevision = cachedRevision ?? throw new ArgumentNullException(nameof(cachedRevision));
        CommittedRevision = committedRevision ?? throw new ArgumentNullException(nameof(committedRevision));
    }
}

/// <summary>
/// Provider contract for retrieving authoritative participant evidence and candidate batches for Social discovery.
/// </summary>
public interface ISocialDiscoveryEvidenceProvider
{
    /// <summary>
    /// Retrieves authoritative discovery evidence and revision snapshots for the discovering caller profile.
    /// </summary>
    Task<SocialDiscoveryCallerBundle?> GetCallerBundleAsync(
        Guid callerProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a bounded collection of candidate items to be evaluated and ranked for the discovering caller.
    /// </summary>
    Task<IReadOnlyList<SocialAuthoritativeCandidateItem>> GetCandidateBatchAsync(
        Guid callerProfileId,
        int maxCandidateBatchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract for caller-scoped Social ranked discovery access boundary (SOC-02).
/// </summary>
public interface ISocialDiscoveryAccessService
{
    /// <summary>
    /// Evaluates, ranks, and returns privacy-safe candidate discovery cards for the authenticated caller.
    /// </summary>
    Task<IReadOnlyList<SocialExploreCandidateCard>> DiscoverCandidatesAsync(
        int rotationOffset = 0,
        int limit = SocialDiscoveryAccessService.DefaultCandidateLimit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements the trusted caller-scoped Social discovery access boundary.
/// Enforces profile ownership, active module standing, legal adult eligibility,
/// bidirectional blocking, privacy visibility, and bounded processing via <see cref="SocialRankedDiscoveryService"/>.
/// </summary>
public sealed class SocialDiscoveryAccessService : ISocialDiscoveryAccessService
{
    public const string SocialModule = "social";
    public const int DefaultCandidateLimit = 20;
    public const int MaxCandidateLimit = SocialRankedDiscoveryService.DefaultMaxCandidateBatchSize;

    private readonly IPrivateProfileService _profileService;
    private readonly ISocialDiscoveryEvidenceProvider _evidenceProvider;

    public SocialDiscoveryAccessService(
        IPrivateProfileService profileService,
        ISocialDiscoveryEvidenceProvider evidenceProvider)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _evidenceProvider = evidenceProvider ?? throw new ArgumentNullException(nameof(evidenceProvider));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SocialExploreCandidateCard>> DiscoverCandidatesAsync(
        int rotationOffset = 0,
        int limit = DefaultCandidateLimit,
        CancellationToken cancellationToken = default)
    {
        if (rotationOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationOffset),
                rotationOffset,
                "Rotation offset cannot be negative.");
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Candidate limit must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var boundedLimit = Math.Min(limit, MaxCandidateLimit);

        return await _profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProfileScope(profile);

            // 1. Retrieve authoritative caller evidence bundle
            var callerBundle = await _evidenceProvider.GetCallerBundleAsync(profile.ProfileId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (callerBundle is null || callerBundle.Evidence is null ||
                callerBundle.CachedRevision is null || callerBundle.CommittedRevision is null)
            {
                // Fail closed when caller evidence is missing
                return Array.Empty<SocialExploreCandidateCard>();
            }

            if (callerBundle.Evidence.ProfileId != profile.ProfileId ||
                callerBundle.CachedRevision.ProfileId != profile.ProfileId ||
                callerBundle.CommittedRevision.ProfileId != profile.ProfileId)
            {
                throw new ProfileAccessDeniedException();
            }

            // Adult eligibility check: caller must be at least 18 years old
            if (callerBundle.Evidence.Age < 18 ||
                !callerBundle.Evidence.IsOptedIn ||
                !callerBundle.Evidence.IsActive ||
                !callerBundle.Evidence.HasAuthoritativeEvidence)
            {
                // Fail closed returning empty discovery feed for ineligible callers
                return Array.Empty<SocialExploreCandidateCard>();
            }

            // Matching engine policy version validation
            if (!string.Equals(callerBundle.CommittedRevision.PolicyVersion, SocialCompatibilityScoringEngine.PolicyVersion, StringComparison.Ordinal))
            {
                // Fail closed on outdated or incompatible matching policy version
                return Array.Empty<SocialExploreCandidateCard>();
            }

            // Caller evidence currency check: reject stale revisions, restriction, or deactivation before candidate retrieval
            var callerCurrency = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(callerBundle.CachedRevision, callerBundle.CommittedRevision);
            if (!callerCurrency.IsCurrent)
            {
                // Fail closed without invoking candidate provider when caller evidence is stale, restricted, or deactivated
                return Array.Empty<SocialExploreCandidateCard>();
            }

            // 2. Retrieve candidate batch from evidence provider bounded to server limit
            var candidateItems = await _evidenceProvider.GetCandidateBatchAsync(profile.ProfileId, boundedLimit, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (candidateItems is null || candidateItems.Count == 0)
            {
                return Array.Empty<SocialExploreCandidateCard>();
            }

            // 3. Delegate to approved deterministic ranking service with freshness, blocking, and contradictory duplicate checks
            var rankedCards = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                callerBundle.Evidence,
                callerBundle.CachedRevision,
                callerBundle.CommittedRevision,
                candidateItems,
                rotationOffset,
                boundedLimit);

            if (rankedCards.Count > boundedLimit)
            {
                return rankedCards.Take(boundedLimit).ToArray();
            }

            return rankedCards;
        }, cancellationToken);
    }

    private static void ValidateProfileScope(PrivateProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.ProfileId == Guid.Empty ||
            !string.Equals(profile.Module, SocialModule, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileAccessDeniedException();
        }
    }
}
