using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Map.Contributions;

// Internal projection only; never return contribution entities, media keys or location evidence.
internal sealed record MapContributionOwnerStatus(Guid ContributionId, MapContributionModerationStatus Status);

/// <summary>Offline owner-scoped status retrieval; no DI or HTTP activation.</summary>
/// <remarks>
/// Reuses the existing Hobbies actor context, including its exact "Hobbies" module value.
/// The context must be supplied by a trusted backend, never deserialized client claims.
/// Production authorization freshness remains an M2 integration dependency. Attraction
/// submission eligibility is deliberately not used to authorize an owner's status read.
/// </remarks>
internal sealed class MapContributionOwnerStatusService
{
    private readonly IMapContributionRepository _contributions;
    private readonly IGalleryActorContext _actors;

    internal MapContributionOwnerStatusService(IMapContributionRepository contributions, IGalleryActorContext actors)
    {
        _contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    internal async Task<MapContributionOwnerStatus?> GetMineAsync(Guid contributionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (contributionId == Guid.Empty)
            return null;
        try
        {
            var actor = _actors.Current;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveHobbies(actor))
                return null;
            var owner = actor!.ModuleProfileId;
            var contribution = await _contributions.FindOwnedAsync(contributionId, owner, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = _actors.Current;
            cancellationToken.ThrowIfCancellationRequested();
            // Local scope changes during a read must not disclose a previous owner's metadata.
            // This check is not a persisted revocation fence or an authorization lease.
            if (!IsActiveHobbies(current) || current!.ModuleProfileId != owner
                || contribution is null || contribution.ContributionId != contributionId
                || contribution.OwnerHobbiesModuleProfileId != owner
                || contribution.ModerationStatus != MapContributionModerationStatus.PendingReview)
                return null;
            cancellationToken.ThrowIfCancellationRequested();
            return new(contribution.ContributionId, contribution.ModerationStatus);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // No provider text, private identifiers or inner exception cross this boundary.
            throw new InvalidOperationException("Contribution status retrieval is unavailable.");
        }
    }

    private static bool IsActiveHobbies(GalleryActor? actor) => actor is
        { IsActive: true, Module: "Hobbies" } && actor.ModuleProfileId != Guid.Empty;
}
