using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Places;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Hobbies;

/// <summary>
/// Persists canonical attractions and retrieves authoritative stored attraction data.
/// </summary>
/// <remarks>
/// Requires a context with attraction entity mapping and a database primary key
/// on AttractionId. The context must not be used concurrently.
/// Trusted backend callers must supply canonical data and enforce authorization,
/// contribution eligibility, visibility, approval and reward policies separately.
/// </remarks>
public sealed class MapAttractionRepository<TDbContext> : IMapAttractionRepository
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public MapAttractionRepository(TDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(
        MapAttraction attraction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attraction);
        cancellationToken.ThrowIfCancellationRequested();

        // Detect pending changes even when automatic change detection is disabled.
        _dbContext.ChangeTracker.DetectChanges();
        if (_dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("The attraction context contains pending changes.");

        var entry = _dbContext.Entry(attraction);
        if (entry.State != EntityState.Detached)
            throw new InvalidOperationException("The attraction is already tracked by this context.");

        try
        {
            // Added state always requests an insert. The database key rejects duplicates.
            entry.State = EntityState.Added;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // Remove only this operation's entry, including after a failed insert.
            entry.State = EntityState.Detached;
        }
    }

    public async Task<MapAttraction?> FindByIdAsync(
        Guid attractionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attractionId == Guid.Empty)
            return null;

        return await _dbContext.Set<MapAttraction>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                attraction => attraction.AttractionId == attractionId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<MapAttractionDiscoveryItem>> DiscoverAsync(
        MapAttractionDiscoveryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var items = await _dbContext.Set<MapAttraction>()
            .AsNoTracking()
            .OrderBy(attraction => attraction.AttractionId)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(attraction => new MapAttractionDiscoveryItem(
                attraction.AttractionId, attraction.Name, attraction.Latitude, attraction.Longitude))
            .ToListAsync(cancellationToken);
        return items.AsReadOnly();
    }
}
