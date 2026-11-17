
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Map.Contributions;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Hobbies;

/// <summary>
/// EF Core persistence implementation for server-managed
/// Andril Map contribution capture sessions.
/// </summary>
/// <remarks>
/// Requires a dedicated, correctly configured DbContext
/// containing the capture-session entity mapping.
///
/// The caller must obtain contributor identity,
/// canonical attraction identity and timestamps
/// from trusted backend services.
/// </remarks>
public sealed class MapContributionCaptureSessionRepository<TDbContext>
    : IMapContributionCaptureSessionRepository
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;

    public MapContributionCaptureSessionRepository(
        TDbContext dbContext)
    {
        _dbContext = dbContext
            ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// Persists a new capture session without overwriting
    /// an existing record.
    /// </summary>
    public async Task AddAsync(
        MapContributionCaptureSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        cancellationToken.ThrowIfCancellationRequested();

        // Do not accidentally persist unrelated pending
        // changes in the same DbContext.
        if (_dbContext.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "The capture session context contains pending changes.");
        }

        var entry = _dbContext
            .Set<MapContributionCaptureSession>()
            .Add(session);

        try
        {
            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        finally
        {
            // Prevent accidental subsequent persistence
            // through this tracked entity instance.
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Returns a session only when its contributor
    /// and attraction scope match.
    /// </summary>
    public async Task<MapContributionCaptureSession?>
        FindOwnedAsync(
            Guid sessionId,
            Guid ownerHobbiesModuleProfileId,
            Guid attractionId,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sessionId == Guid.Empty
            || ownerHobbiesModuleProfileId == Guid.Empty
            || attractionId == Guid.Empty)
        {
            return null;
        }

        return await _dbContext
            .Set<MapContributionCaptureSession>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                session =>
                    session.SessionId == sessionId
                    && session.OwnerHobbiesModuleProfileId
                        == ownerHobbiesModuleProfileId
                    && session.AttractionId == attractionId,
                cancellationToken);
    }

    /// <summary>
    /// Atomically persists a session completion when
    /// the session is still incomplete and all
    /// ownership and timestamp conditions match.
    /// </summary>
    public async Task<bool> TryCompleteAsync(
        Guid sessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sessionId == Guid.Empty
            || ownerHobbiesModuleProfileId == Guid.Empty
            || attractionId == Guid.Empty)
        {
            return false;
        }

        var completedAtUtc = completedAt.ToUniversalTime();

        var affectedRows = await _dbContext
            .Set<MapContributionCaptureSession>()
            .Where(session =>
                session.SessionId == sessionId
                && session.OwnerHobbiesModuleProfileId
                    == ownerHobbiesModuleProfileId
                && session.AttractionId == attractionId
                && session.CompletedAtUtc == null
                && session.StartedAtUtc <= completedAtUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    session => session.CompletedAtUtc,
                    (DateTimeOffset?)completedAtUtc),
                cancellationToken);

        return affectedRows == 1;
    }
}
