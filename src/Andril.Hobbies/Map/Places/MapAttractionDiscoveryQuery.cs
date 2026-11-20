using System;

namespace Andril.Hobbies.Map.Places;

/// <summary>Specifies a bounded page for trusted backend discovery.</summary>
public sealed class MapAttractionDiscoveryQuery
{
    /// <summary>Internal engineering ceiling for a single database read.</summary>
    public const int MaximumLimit = 100;

    public MapAttractionDiscoveryQuery(int offset, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        Offset = offset;
        Limit = limit;
    }

    public int Offset { get; }
    public int Limit { get; }
}
