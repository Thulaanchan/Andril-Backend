using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Policies;
using Andril.Groups.Services;

namespace Andril.API.Authorization.Groups;

/// <summary>
/// Trusted Group actor context adapter resolving authenticated caller identity
/// and authoritative standing evidence across Learning, Hobbies, and Social modules.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Architectural Invariants:</b>
/// <list type="bullet">
/// <item><description>Derives actor profile identity exclusively from trusted server-side authentication (<see cref="IPrivateProfileService"/>).</description></item>
/// <item><description>Never accepts or trusts client-supplied actor or profile identifiers.</description></item>
/// <item><description>Correlates authorized caller profile with authoritative standing evidence (<see cref="IGroupAuthoritativeEvidenceProvider"/>).</description></item>
/// <item><description>Enforces strict module consistency, active standing, and unrestricted status.</description></item>
/// <item><description>Enforces verified adult age (18+) for Social Zone actors without manufacturing or synthesizing age values.</description></item>
/// <item><description>Validates evidence freshness strictly rejecting future-dated evidence and stale evidence exceeding <see cref="GroupParticipationAuthorizationPolicy.MaximumEvidenceAge"/> (5 minutes) without modifying original timestamps.</description></item>
/// <item><description>Fails closed (returns <see langword="null"/>) on missing, mismatched, restricted, or stale evidence.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GroupActorContext : IGroupActorContext
{
    private readonly IPrivateProfileService _profileService;
    private readonly IGroupAuthoritativeEvidenceProvider _evidenceProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupActorContext"/> class.
    /// </summary>
    /// <param name="profileService">The private module profile service.</param>
    /// <param name="evidenceProvider">The authoritative group evidence provider.</param>
    /// <param name="timeProvider">Optional time provider for evidence freshness evaluation.</param>
    public GroupActorContext(
        IPrivateProfileService profileService,
        IGroupAuthoritativeEvidenceProvider evidenceProvider,
        TimeProvider? timeProvider = null)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _evidenceProvider = evidenceProvider ?? throw new ArgumentNullException(nameof(evidenceProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(
        string expectedModule,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(expectedModule))
        {
            return null;
        }

        var normalizedModule = expectedModule.Trim().ToLowerInvariant();
        if (normalizedModule is not ("learning" or "hobbies" or "social"))
        {
            return null;
        }

        // 1. Resolve authenticated caller private module profile through M2 boundary
        PrivateProfileDto? authorizedProfile;
        try
        {
            authorizedProfile = await _profileService.WithAuthorizedProfileAsync(
                normalizedModule,
                profile => Task.FromResult<PrivateProfileDto?>(profile),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (!GroupAvailability.IsUnavailable(error))
        {
            // Fail closed on unauthenticated caller, access denial, or profile resolution failure
            return null;
        }

        if (authorizedProfile is null || authorizedProfile.ProfileId == Guid.Empty)
        {
            return null;
        }

        if (!string.Equals(authorizedProfile.Module, normalizedModule, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Retrieve authoritative profile standing evidence
        AuthoritativeGroupProfileEvidence? evidence;
        try
        {
            evidence = await _evidenceProvider.GetProfileEvidenceAsync(
                authorizedProfile.ProfileId,
                normalizedModule,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (!GroupAvailability.IsUnavailable(error))
        {
            // Fail closed on evidence provider failure
            return null;
        }

        if (evidence is null)
        {
            return null;
        }

        // 3. Verify exact identity and module correlation
        if (evidence.ProfileId != authorizedProfile.ProfileId || evidence.ProfileId == Guid.Empty)
        {
            return null;
        }

        if (!string.Equals(evidence.Module, normalizedModule, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 4. Verify active, unrestricted standing
        if (!evidence.IsActive || evidence.IsRestricted)
        {
            return null;
        }

        // 5. Verify strict evidence freshness: reject future-dated evidence and stale evidence
        var now = _timeProvider.GetUtcNow();
        var evidenceTime = evidence.EvidenceTimestampUtc;

        if (evidenceTime > now ||
            (now - evidenceTime) > GroupParticipationAuthorizationPolicy.MaximumEvidenceAge)
        {
            return null;
        }

        // 6. Enforce verified adult eligibility for Social Zone participation
        if (string.Equals(normalizedModule, "social", StringComparison.OrdinalIgnoreCase))
        {
            if (!evidence.VerifiedAge.HasValue ||
                evidence.VerifiedAge.Value < GroupParticipationAuthorizationPolicy.SocialZoneMinimumAge)
            {
                return null;
            }
        }

        // 7. Return verified authoritative evidence preserving original timestamp and values
        return evidence;
    }
}
