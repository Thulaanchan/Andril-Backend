using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Andril.Economy.FlightPoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Andril.Persistence.Repositories.Economy;

/// <summary>
/// Appends Flight Points ledger entries and retrieves entries within a Hobbies profile scope.
/// </summary>
/// <remarks>
/// Use a sequential DbContext with a relational ledger mapping and a database primary key
/// on the ledger entry ID. Appends own their transaction and do not retry commits.
/// Schema constraints must enforce uniqueness across concurrent writers, including reversal
/// uniqueness. Idempotency-key uniqueness awaits an approved scope.
/// </remarks>
public sealed class FlightPointsLedgerRepository<TDbContext> : IFlightPointsLedgerRepository
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public FlightPointsLedgerRepository(TDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AppendAsync(
        IReadOnlyCollection<FlightPointsLedgerEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();

        var batch = entries.ToArray();
        if (batch.Length == 0)
            throw new ArgumentException("The ledger batch must contain at least one entry.", nameof(entries));

        var entryIds = new HashSet<Guid>();
        var reversals = new Dictionary<Guid, FlightPointsLedgerEntry>();
        foreach (var entry in batch)
        {
            if (entry is null)
                throw new ArgumentException("The ledger batch contains a null entry.", nameof(entries));
            if (!entryIds.Add(entry.LedgerEntryId))
                throw new ArgumentException("The ledger batch contains a duplicate entry ID.", nameof(entries));
            if (entry.EntryType == FlightPointsLedgerEntryType.Reversal
                && (entry.ReversedEntryId is not Guid originalId || originalId == Guid.Empty
                    || !reversals.TryAdd(originalId, entry)))
            {
                throw new ArgumentException(
                    "Each reversal must reference a distinct original grant.", nameof(entries));
            }
        }

        // Check pending changes even when automatic detection is disabled.
        var tracker = _dbContext.ChangeTracker;
        tracker.DetectChanges();
        if (tracker.HasChanges())
            throw new InvalidOperationException("The ledger context contains pending changes.");
        if (tracker.Entries<FlightPointsLedgerEntry>().Any(entry => entryIds.Contains(entry.Entity.LedgerEntryId)))
            throw new InvalidOperationException("A ledger entry in the batch is already tracked by this context.");

        var database = _dbContext.Database;
        if (!database.IsRelational() || database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Ledger appends require a transaction-enabled relational context.");
        if (database.CurrentTransaction is not null || Transaction.Current is not null
            || database.GetEnlistedTransaction() is not null)
        {
            throw new InvalidOperationException("Ledger appends cannot run in a caller-owned or ambient transaction.");
        }
        if (database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Ledger appends require a non-retrying execution strategy.");
        if (_dbContext.Model.FindEntityType(typeof(FlightPointsLedgerEntry)) is null)
            throw new InvalidOperationException("The context must map Flight Points ledger entries.");

        await using var transaction = await database.BeginTransactionAsync(cancellationToken);
        if (reversals.Count > 0)
            await ValidateReversalsAsync(reversals, cancellationToken);

        var staged = new List<EntityEntry<FlightPointsLedgerEntry>>(batch.Length);
        try
        {
            foreach (var entry in batch)
            {
                var tracked = _dbContext.Entry(entry);
                staged.Add(tracked);
                tracked.State = EntityState.Added;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            foreach (var entry in staged)
                entry.State = EntityState.Detached;
        }
    }

    public async Task<FlightPointsLedgerEntry?> FindOwnedAsync(
        Guid ledgerEntryId,
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ledgerEntryId == Guid.Empty || ownerHobbiesModuleProfileId == Guid.Empty)
            return null;

        return await _dbContext.Set<FlightPointsLedgerEntry>()
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.LedgerEntryId == ledgerEntryId
                && entry.OwnerHobbiesModuleProfileId == ownerHobbiesModuleProfileId, cancellationToken);
    }

    public async Task<long> GetNetPointsAsync(
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ownerHobbiesModuleProfileId == Guid.Empty)
            throw new ArgumentException("An owner Hobbies profile ID is required.", nameof(ownerHobbiesModuleProfileId));

        return await _dbContext.Set<FlightPointsLedgerEntry>()
            .AsNoTracking()
            .Where(entry => entry.OwnerHobbiesModuleProfileId == ownerHobbiesModuleProfileId)
            .SumAsync(entry => (long)entry.PointsDelta, cancellationToken);
    }

    private async Task ValidateReversalsAsync(
        Dictionary<Guid, FlightPointsLedgerEntry> reversals,
        CancellationToken cancellationToken)
    {
        var originalIds = reversals.Keys.ToArray();
        var ledger = _dbContext.Set<FlightPointsLedgerEntry>().AsNoTracking();
        var grants = await ledger
            .Where(entry => entry.EntryType == FlightPointsLedgerEntryType.Grant
                && originalIds.Contains(entry.LedgerEntryId))
            .ToDictionaryAsync(entry => entry.LedgerEntryId, cancellationToken);

        foreach (var (originalId, reversal) in reversals)
        {
            if (!grants.TryGetValue(originalId, out var original)
                || original.OwnerHobbiesModuleProfileId != reversal.OwnerHobbiesModuleProfileId
                || original.ContributionId != reversal.ContributionId
                || original.AttractionId != reversal.AttractionId
                || original.RewardType != reversal.RewardType
                || original.PointsDelta <= 0
                || reversal.PointsDelta != -original.PointsDelta)
            {
                throw new InvalidOperationException("The reversal does not match a persisted original grant.");
            }
        }

        // This detects recorded reversals. A database constraint must close the concurrent-write race.
        if (await ledger.AnyAsync(entry => entry.EntryType == FlightPointsLedgerEntryType.Reversal
            && entry.ReversedEntryId.HasValue && originalIds.Contains(entry.ReversedEntryId.Value), cancellationToken))
        {
            throw new InvalidOperationException("A grant in the ledger batch has already been reversed.");
        }
    }
}
