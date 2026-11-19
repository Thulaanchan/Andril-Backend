using System;

namespace Andril.Hobbies.Map.Places;

/// <summary>Read-only canonical attraction data for trusted backend discovery.</summary>
public sealed class MapAttractionDiscoveryItem(Guid attractionId, string name, double latitude, double longitude)
{
    public Guid AttractionId { get; } = attractionId;
    public string Name { get; } = name;
    public double Latitude { get; } = latitude;
    public double Longitude { get; } = longitude;
}
