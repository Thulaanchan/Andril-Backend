namespace Andril.Hobbies.Map.Places;

internal enum MapAttractionVisibility { Unknown = 0, Hidden = 1, Visible = 2 }

internal sealed class MapAttractionVisibilityDecision(Guid attractionId, MapAttractionVisibility visibility)
{
    internal Guid AttractionId { get; } = attractionId;
    internal MapAttractionVisibility Visibility { get; } = visibility;
}

/// <summary>Defensively copied evidence. The consumer validates completeness and exact requested scope.</summary>
internal sealed class MapAttractionVisibilityBatch
{
    internal MapAttractionVisibilityBatch(Guid ownerHobbiesModuleProfileId,
        IEnumerable<MapAttractionVisibilityDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        OwnerHobbiesModuleProfileId = ownerHobbiesModuleProfileId;
        Decisions = Array.AsReadOnly(decisions.ToArray());
    }

    internal Guid OwnerHobbiesModuleProfileId { get; }
    internal IReadOnlyList<MapAttractionVisibilityDecision> Decisions { get; }
}
