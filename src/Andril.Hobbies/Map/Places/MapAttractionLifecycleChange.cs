namespace Andril.Hobbies.Map.Places;

/// <summary>Immutable offline command. Identity and permissions never come from this payload.</summary>
internal sealed record MapAttractionLifecycleChange
{
    internal MapAttractionLifecycleChange(Guid requestId, Guid attractionId, bool isActive, long expectedVersion)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request ID is required.", nameof(requestId));
        if (attractionId == Guid.Empty) throw new ArgumentException("An attraction ID is required.", nameof(attractionId));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        RequestId = requestId;
        AttractionId = attractionId;
        IsActive = isActive;
        ExpectedVersion = expectedVersion;
    }

    internal Guid RequestId { get; }
    internal Guid AttractionId { get; }
    internal bool IsActive { get; }
    internal long ExpectedVersion { get; }
}
