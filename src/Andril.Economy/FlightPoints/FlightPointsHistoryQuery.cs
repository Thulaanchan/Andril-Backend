using System;

namespace Andril.Economy.FlightPoints;

/// <summary>Specifies a bounded ledger history page for trusted backend callers.</summary>
public sealed class FlightPointsHistoryQuery
{
    /// <summary>Internal engineering ceiling for one history read.</summary>
    public const int MaximumLimit = 100;

    public FlightPointsHistoryQuery(int offset, int limit)
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
