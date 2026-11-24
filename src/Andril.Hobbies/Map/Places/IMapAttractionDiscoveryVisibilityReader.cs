namespace Andril.Hobbies.Map.Places;

/// <summary>Offline visibility boundary. Implementations must use trusted evidence, not contribution eligibility.</summary>
/// <remarks>No production provider or authorization freshness guarantee is supplied by this contract.</remarks>
internal interface IMapAttractionDiscoveryVisibilityReader
{
    Task<MapAttractionVisibilityBatch> ReadAsync(Guid ownerHobbiesModuleProfileId,
        IReadOnlyList<Guid> attractionIds, CancellationToken cancellationToken = default);
}

/// <summary>Fails closed until an authoritative visibility integration is approved.</summary>
internal sealed class DenyAllMapAttractionDiscoveryVisibilityReader : IMapAttractionDiscoveryVisibilityReader
{
    public Task<MapAttractionVisibilityBatch> ReadAsync(Guid ownerHobbiesModuleProfileId,
        IReadOnlyList<Guid> attractionIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(attractionIds);
        return Task.FromResult(new MapAttractionVisibilityBatch(ownerHobbiesModuleProfileId,
            attractionIds.Select(id => new MapAttractionVisibilityDecision(id, MapAttractionVisibility.Hidden))));
    }
}
