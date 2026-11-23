namespace Andril.Hobbies.Map.Places;

/// <summary>
/// Offline immutable state-transition prototype. Not mapped, registered or authoritative.
/// A state snapshot establishes neither caller authorization nor submission eligibility.
/// </summary>
internal sealed class MapAttractionStatePrototype
{
    private MapAttractionStatePrototype(Guid attractionId, bool isActive, long version)
    {
        AttractionId = attractionId;
        IsActive = isActive;
        Version = version;
    }

    internal Guid AttractionId { get; }
    internal bool IsActive { get; }
    internal long Version { get; }

    // No initial state or version is inferred from a canonical attraction record.
    internal static MapAttractionStatePrototype CreateSnapshot(MapAttraction attraction, bool isActive, long version)
    {
        ArgumentNullException.ThrowIfNull(attraction);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return new(attraction.AttractionId, isActive, version);
    }

    internal MapAttractionStatePrototype Transition(bool isActive, long expectedVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (expectedVersion != Version)
            throw new InvalidOperationException("The attraction state version does not match.");
        // A current-version no-op is not a durable idempotency protocol for old requests.
        return isActive == IsActive ? this : new(AttractionId, isActive, checked(Version + 1));
    }
}
