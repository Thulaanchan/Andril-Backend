using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Hobbies;

/// <summary>
/// Persists pending-review Map contributions and retrieves them within a Hobbies profile scope.
/// </summary>
/// <remarks>
/// Requires a configured contribution entity, a database primary key on the contribution ID,
/// and evidence mapped as part of the contribution entry. Use the context sequentially.
/// Trusted callers are responsible for submission authorization, evidence verification,
/// and capture-session replay protection.
/// </remarks>
public sealed class MapContributionRepository<TDbContext> : IMapContributionRepository
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public MapContributionRepository(TDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task AddAsync(
        MapContribution contribution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        cancellationToken.ThrowIfCancellationRequested();

        if (contribution.ModerationStatus != MapContributionModerationStatus.PendingReview)
            throw new ArgumentException("The contribution must be pending review.", nameof(contribution));

        // Detect pending changes even when automatic detection is disabled.
        _dbContext.ChangeTracker.DetectChanges();
        if (_dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("The contribution context contains pending changes.");

        var entry = _dbContext.Entry(contribution);
        if (entry.State != EntityState.Detached)
            throw new InvalidOperationException("The contribution is already tracked by this context.");

        // This operation tracks only the contribution entry, so its evidence must
        // be stored on that entry rather than as a separately tracked entity.
        var evidenceName = nameof(MapContribution.LocationEvidence);
        if (entry.Metadata.FindProperty(evidenceName) is null
            && entry.Metadata.FindComplexProperty(evidenceName) is null)
        {
            throw new InvalidOperationException("Location evidence must be mapped on the contribution entry.");
        }

        try
        {
            entry.State = EntityState.Added;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    public async Task<MapContribution?> FindOwnedAsync(
        Guid contributionId,
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (contributionId == Guid.Empty || ownerHobbiesModuleProfileId == Guid.Empty)
            return null;

        return await _dbContext.Set<MapContribution>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                contribution => contribution.ContributionId == contributionId
                    && contribution.OwnerHobbiesModuleProfileId == ownerHobbiesModuleProfileId,
                cancellationToken);
    }
}
