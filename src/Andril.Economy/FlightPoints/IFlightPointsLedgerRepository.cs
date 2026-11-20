using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Economy.FlightPoints;

/// <summary>
/// Defines append-only persistence and private, owner-scoped retrieval for Flight Points.
/// </summary>
/// <remarks>
/// Only trusted backend services may use this contract. Those services establish Hobbies
/// profile ownership, contribution eligibility and approval, First Explorer selection
/// and ordering, and reversal authorization. Flight Points are separate from XP and coins.
/// Any operation spanning approval, ledger entries, or balances requires its own
/// transactional coordination.
/// </remarks>
public interface IFlightPointsLedgerRepository
{
    /// <summary>
    /// Inserts an entire batch of ledger entries atomically.
    /// </summary>
    /// <remarks>
    /// Reject null or empty collections, null entries, and duplicate ledger entry IDs
    /// within the batch or in persisted records. Reject duplicate idempotency keys
    /// according to the approved key scope once it is defined. Preserve existing ledger
    /// records. Honor cancellation and propagate persistence failures.
    ///
    /// Commit either the entire batch or none of it, including when the batch contains
    /// both the 5-point first contribution grant and the 10-point First Explorer bonus.
    /// Durable duplicate protection, concurrent writes, and batch atomicity require
    /// transaction handling and database constraints. Future integration must enforce
    /// reward uniqueness for eligible user/place events, First Explorer events, and reversals.
    ///
    /// Preserve each reversal's link to its original grant. Persistence and service
    /// integration must verify the original grant exists, ownership and contribution,
    /// attraction and reward category match, the delta fully offsets the original
    /// grant, and a duplicate reversal cannot be appended. Trusted services must
    /// authorize each reversal.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is null.</exception>
    Task AppendAsync(
        IReadOnlyCollection<FlightPointsLedgerEntry> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an immutable, authoritative ledger entry by entry ID and owner Hobbies profile ID.
    /// </summary>
    /// <remarks>
    /// The owner ID must come from trusted Hobbies profile authorization. Apply both
    /// identifiers in the persistence query. Return null for empty identifiers,
    /// missing entries, or ownership mismatches without disclosing whether another
    /// profile owns the entry. Returned entries must prevent implicit persistence
    /// of changes. Honor cancellation and propagate unexpected persistence failures.
    /// </remarks>
    Task<FlightPointsLedgerEntry?> FindOwnedAsync(
        Guid ledgerEntryId,
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the signed net total of persisted ledger entries for a Hobbies profile.
    /// Grants increase the total, reversals decrease it, and an empty ledger returns zero.
    /// </summary>
    /// <remarks>
    /// The profile ID must come from trusted backend authorization. Reward eligibility and
    /// spending authorization require separate checks. Honors cancellation and propagates
    /// persistence and overflow failures.
    /// </remarks>
    /// <exception cref="ArgumentException">The owner profile ID is empty.</exception>
    Task<long> GetNetPointsAsync(
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default);
}
