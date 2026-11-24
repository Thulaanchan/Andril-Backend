using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Map.Places;

/// <summary>Offline bounded discovery orchestration; never registered in DI or exposed through HTTP.</summary>
/// <remarks>
/// Visibility is supplied separately from contribution eligibility and photo publication.
/// Synthetic readers establish no production visibility policy. Actor rechecks detect local
/// scope changes only; authoritative freshness and revocation remain integration dependencies.
/// </remarks>
internal sealed class MapAttractionDiscoveryService
{
    private readonly IMapAttractionRepository _attractions;
    private readonly IMapAttractionDiscoveryVisibilityReader _visibility;
    private readonly IGalleryActorContext _actors;

    internal MapAttractionDiscoveryService(IMapAttractionRepository attractions,
        IMapAttractionDiscoveryVisibilityReader visibility, IGalleryActorContext actors)
    {
        _attractions = attractions ?? throw new ArgumentNullException(nameof(attractions));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    internal async Task<MapAttractionDiscoveryResult> DiscoverAsync(MapAttractionDiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            var actor = _actors.Current;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveHobbies(actor)) return MapAttractionDiscoveryResult.Denied();
            var owner = actor!.ModuleProfileId;
            var candidates = await _attractions.DiscoverAsync(query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasScope(owner, cancellationToken)) return MapAttractionDiscoveryResult.Denied();
            if (candidates is null || candidates.Count > query.Limit)
                return MapAttractionDiscoveryResult.Unavailable();
            // Copy dependency-owned collections before crossing another asynchronous boundary.
            var window = candidates.ToArray();
            var ids = new HashSet<Guid>();
            foreach (var item in window)
            {
                if (item is null || item.AttractionId == Guid.Empty || !ids.Add(item.AttractionId)
                    || string.IsNullOrWhiteSpace(item.Name)
                    || !double.IsFinite(item.Latitude) || item.Latitude is < -90 or > 90
                    || !double.IsFinite(item.Longitude) || item.Longitude is < -180 or > 180)
                    return MapAttractionDiscoveryResult.Unavailable();
            }
            if (window.Length == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return MapAttractionDiscoveryResult.Success([], 0);
            }
            var batch = await _visibility.ReadAsync(owner,
                Array.AsReadOnly(window.Select(item => item.AttractionId).ToArray()), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasScope(owner, cancellationToken)) return MapAttractionDiscoveryResult.Denied();
            if (batch is null || batch.OwnerHobbiesModuleProfileId != owner || batch.Decisions.Count != window.Length)
                return MapAttractionDiscoveryResult.Unavailable();
            var visible = new HashSet<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var decision in batch.Decisions)
            {
                if (decision is null || !ids.Contains(decision.AttractionId) || !seen.Add(decision.AttractionId)
                    || decision.Visibility is not (MapAttractionVisibility.Hidden or MapAttractionVisibility.Visible))
                    return MapAttractionDiscoveryResult.Unavailable();
                if (decision.Visibility == MapAttractionVisibility.Visible) visible.Add(decision.AttractionId);
            }
            var result = MapAttractionDiscoveryResult.Success(
                window.Where(item => visible.Contains(item.AttractionId)), window.Length);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Return no exception/provider text, private evidence or partially authorized records.
            return MapAttractionDiscoveryResult.Unavailable();
        }
    }

    private bool HasScope(Guid owner, CancellationToken cancellationToken)
    {
        var current = _actors.Current;
        cancellationToken.ThrowIfCancellationRequested();
        return IsActiveHobbies(current) && current!.ModuleProfileId == owner;
    }

    private static bool IsActiveHobbies(GalleryActor? actor) => actor is
        { IsActive: true, Module: "Hobbies" } && actor.ModuleProfileId != Guid.Empty;
}
