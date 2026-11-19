namespace Andril.Hobbies.Map.Places;

/// <summary>
/// Defines a canonical attraction identity and geographic coordinates.
/// Backend integrations must load canonical coordinates from authoritative records.
/// </summary>
/// <remarks>
/// A single attraction can be associated with multiple approved visitor contributions.
/// Contribution eligibility, approval, visibility and rewards require separate policies.
/// </remarks>
public sealed class MapAttraction
{
    private MapAttraction(Guid attractionId, string name, double latitude, double longitude)
    {
        AttractionId = attractionId;
        Name = name;
        Latitude = latitude;
        Longitude = longitude;
    }

    public Guid AttractionId { get; }

    public string Name { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public static MapAttraction Create(
        Guid attractionId,
        string name,
        double latitude,
        double longitude)
    {
        if (attractionId == Guid.Empty)
            throw new ArgumentException("An attraction ID is required.", nameof(attractionId));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An attraction name is required.", nameof(name));

        if (!double.IsFinite(latitude) || latitude < -90 || latitude > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be finite and within -90 to 90 degrees.");

        if (!double.IsFinite(longitude) || longitude < -180 || longitude > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be finite and within -180 to 180 degrees.");

        return new MapAttraction(attractionId, name, latitude, longitude);
    }
}
