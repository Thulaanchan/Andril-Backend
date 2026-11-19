namespace Andril.Economy.FlightPoints;

public enum FlightPointsLedgerEntryType
{
    Grant = 1,
    Reversal = 2
}

public enum FlightPointsRewardType
{
    FirstEligibleContribution = 1,
    FirstExplorer = 2
}

/// <summary>
/// Represents one contribution reward component or its full compensating reversal.
/// </summary>
/// <remarks>
/// Trusted application services must authorize the operation before persistence.
/// Durable idempotency, reward uniqueness, and atomic balance and ledger updates
/// require transactional persistence.
/// </remarks>
public sealed class FlightPointsLedgerEntry
{
    private FlightPointsLedgerEntry(
        Guid ledgerEntryId,
        Guid ownerHobbiesModuleProfileId,
        Guid contributionId,
        Guid attractionId,
        FlightPointsLedgerEntryType entryType,
        FlightPointsRewardType rewardType,
        int pointsDelta,
        string idempotencyKey,
        Guid? reversedEntryId,
        DateTimeOffset createdAtUtc)
    {
        LedgerEntryId = ledgerEntryId;
        OwnerHobbiesModuleProfileId = ownerHobbiesModuleProfileId;
        ContributionId = contributionId;
        AttractionId = attractionId;
        EntryType = entryType;
        RewardType = rewardType;
        PointsDelta = pointsDelta;
        IdempotencyKey = idempotencyKey;
        ReversedEntryId = reversedEntryId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid LedgerEntryId { get; }
    public Guid OwnerHobbiesModuleProfileId { get; }
    public Guid ContributionId { get; }
    public Guid AttractionId { get; }
    public FlightPointsLedgerEntryType EntryType { get; }
    public FlightPointsRewardType RewardType { get; }
    public int PointsDelta { get; }
    public string IdempotencyKey { get; }
    public Guid? ReversedEntryId { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    public static FlightPointsLedgerEntry CreateGrant(
        Guid ledgerEntryId,
        Guid ownerHobbiesModuleProfileId,
        Guid contributionId,
        Guid attractionId,
        FlightPointsRewardType rewardType,
        string idempotencyKey,
        DateTimeOffset createdAt)
    {
        if (ledgerEntryId == Guid.Empty)
            throw new ArgumentException("A ledger entry ID is required.", nameof(ledgerEntryId));
        if (ownerHobbiesModuleProfileId == Guid.Empty)
            throw new ArgumentException("An owner Hobbies module profile ID is required.", nameof(ownerHobbiesModuleProfileId));
        if (contributionId == Guid.Empty)
            throw new ArgumentException("A contribution ID is required.", nameof(contributionId));
        if (attractionId == Guid.Empty)
            throw new ArgumentException("An attraction ID is required.", nameof(attractionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var points = rewardType switch
        {
            FlightPointsRewardType.FirstEligibleContribution => FlightPointsRewardPolicy.FirstEligibleContributionPoints,
            FlightPointsRewardType.FirstExplorer => FlightPointsRewardPolicy.FirstExplorerBonusPoints,
            _ => throw new ArgumentOutOfRangeException(nameof(rewardType), "The reward type is undefined.")
        };

        return new FlightPointsLedgerEntry(
            ledgerEntryId, ownerHobbiesModuleProfileId, contributionId, attractionId,
            FlightPointsLedgerEntryType.Grant, rewardType, points, idempotencyKey, null, createdAt);
    }

    /// <summary>
    /// Models a full compensating entry for an original grant supplied by a trusted caller.
    /// </summary>
    public static FlightPointsLedgerEntry CreateReversal(
        Guid ledgerEntryId,
        FlightPointsLedgerEntry originalGrant,
        string idempotencyKey,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(originalGrant);
        if (ledgerEntryId == Guid.Empty || ledgerEntryId == originalGrant.LedgerEntryId)
            throw new ArgumentException("A distinct reversal entry ID is required.", nameof(ledgerEntryId));
        if (originalGrant.EntryType != FlightPointsLedgerEntryType.Grant)
            throw new ArgumentException("Only an original grant can be reversed.", nameof(originalGrant));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        return new FlightPointsLedgerEntry(
            ledgerEntryId, originalGrant.OwnerHobbiesModuleProfileId, originalGrant.ContributionId,
            originalGrant.AttractionId, FlightPointsLedgerEntryType.Reversal, originalGrant.RewardType,
            -originalGrant.PointsDelta, idempotencyKey, originalGrant.LedgerEntryId, createdAt);
    }
}
