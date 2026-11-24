using System;

namespace Andril.Economy.FlightPoints;

/// <summary>Read-only ledger transaction data for a trusted owner-scoped history request.</summary>
public sealed class FlightPointsHistoryItem(
    Guid ledgerEntryId,
    Guid contributionId,
    Guid attractionId,
    FlightPointsLedgerEntryType entryType,
    FlightPointsRewardType rewardType,
    int pointsDelta,
    Guid? reversedEntryId,
    DateTimeOffset createdAtUtc)
{
    public Guid LedgerEntryId { get; } = ledgerEntryId;
    public Guid ContributionId { get; } = contributionId;
    public Guid AttractionId { get; } = attractionId;
    public FlightPointsLedgerEntryType EntryType { get; } = entryType;
    public FlightPointsRewardType RewardType { get; } = rewardType;
    public int PointsDelta { get; } = pointsDelta;
    public Guid? ReversedEntryId { get; } = reversedEntryId;
    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
}
