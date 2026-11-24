namespace Andril.Hobbies.Map.Places;

internal enum MapAttractionDiscoveryOutcome { Success, Denied, Unavailable }

/// <summary>Internal metadata only. ScannedCount describes the candidate window, not a public total.</summary>
/// <remarks>A filtered empty page is not proof of exhaustion. No public cursor contract is defined.</remarks>
internal sealed class MapAttractionDiscoveryResult
{
    private MapAttractionDiscoveryResult(MapAttractionDiscoveryOutcome outcome,
        IEnumerable<MapAttractionDiscoveryItem> items, int scannedCount)
    {
        Outcome = outcome;
        Items = Array.AsReadOnly(items.ToArray());
        ScannedCount = scannedCount;
    }

    internal MapAttractionDiscoveryOutcome Outcome { get; }
    internal IReadOnlyList<MapAttractionDiscoveryItem> Items { get; }
    internal int ScannedCount { get; }

    internal static MapAttractionDiscoveryResult Success(IEnumerable<MapAttractionDiscoveryItem> items, int scannedCount)
        => new(MapAttractionDiscoveryOutcome.Success, items, scannedCount);
    internal static MapAttractionDiscoveryResult Denied() => new(MapAttractionDiscoveryOutcome.Denied, [], 0);
    internal static MapAttractionDiscoveryResult Unavailable() => new(MapAttractionDiscoveryOutcome.Unavailable, [], 0);
}
