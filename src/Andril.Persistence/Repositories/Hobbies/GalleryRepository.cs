
using System.Transactions;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andril.Persistence.Repositories.Hobbies;

public sealed class GalleryRepository<TDbContext>
    : IGalleryRepository, IGalleryPageRepository
    where TDbContext : DbContext
{
    private const string HobbiesModule = "Hobbies";

    private readonly TDbContext _context;

    public GalleryRepository(TDbContext context)
    {
        _context = context
            ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyList<GalleryMedia>>
        GetOwnerGalleryAsync(
            Guid ownerModuleProfileId,
            CancellationToken cancellationToken = default)
    {
        if (ownerModuleProfileId == Guid.Empty)
            return Array.Empty<GalleryMedia>();

        return await _context.Set<GalleryMedia>()
            .AsNoTracking()
            .Where(x =>
                x.OwnerModuleProfileId == ownerModuleProfileId &&
                x.Module == HobbiesModule &&
                !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    Task<GalleryPage> IGalleryPageRepository.GetOwnerGalleryPageAsync(
        Guid authorizedOwnerModuleProfileId, int pageSize, int trustedTestOnlyMaximum,
        GalleryPagePosition? after, CancellationToken cancellationToken) =>
        GetOwnerGalleryPageAsync(authorizedOwnerModuleProfileId, pageSize, trustedTestOnlyMaximum, after, cancellationToken);

    // Offline-only additive operation. Not exposed through IGalleryRepository or runtime callers.
    // A future protected caller must freshly authorize the owner on EVERY page request.
    // trustedTestOnlyMaximum is supplied by offline tests, not an approved Product limit.
    internal async Task<GalleryPage> GetOwnerGalleryPageAsync(
        Guid authorizedOwnerModuleProfileId,
        int pageSize,
        int trustedTestOnlyMaximum,
        GalleryPagePosition? after = null,
        CancellationToken cancellationToken = default)
    {
        GalleryPaging.Validate(authorizedOwnerModuleProfileId, pageSize, trustedTestOnlyMaximum, after, cancellationToken);

        var query = _context.Set<GalleryMedia>().AsNoTracking().Where(media =>
            media.OwnerModuleProfileId == authorizedOwnerModuleProfileId
            && media.Module == HobbiesModule && !media.IsDeleted);
        if (after is not null)
        {
            // Preserve the database-returned UTC microsecond position; never round client input.
            var timestamp = after.Timestamp;
            var id = after.Id;
            var fetch = checked(pageSize + 1);
            var sameTimestamp = query.Where(media => media.CreatedAtUtc == timestamp && media.Id.CompareTo(id) > 0)
                .OrderByDescending(media => media.CreatedAtUtc).ThenBy(media => media.Id).Take(fetch);
            var older = query.Where(media => media.CreatedAtUtc < timestamp)
                .OrderByDescending(media => media.CreatedAtUtc).ThenBy(media => media.Id).Take(fetch);
            // Disjoint branches retain every possible combined top-fetch row; Concat stays server-side.
            query = sameTimestamp.Concat(older);
        }
        var fetched = await query.OrderByDescending(media => media.CreatedAtUtc).ThenBy(media => media.Id)
            .Take(checked(pageSize + 1)).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var items = fetched.Take(pageSize).ToArray();
        var next = fetched.Count > pageSize
            ? new GalleryPagePosition(authorizedOwnerModuleProfileId, HobbiesModule, items[^1].CreatedAtUtc, items[^1].Id)
            : null;
        // Replaying a valid position is a read, not a consumed token. No authorization is cached.
        return new GalleryPage(Array.AsReadOnly(items), next);
    }

    public async Task<GalleryMedia?> GetOwnedByIdAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty ||
            ownerModuleProfileId == Guid.Empty)
        {
            return null;
        }

        return await _context.Set<GalleryMedia>()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x =>
                    x.Id == mediaId &&
                    x.OwnerModuleProfileId == ownerModuleProfileId &&
                    x.Module == HobbiesModule &&
                    !x.IsDeleted,
                cancellationToken);
    }

    public async Task<Guid?> GetCoverIdAsync(
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        if (ownerModuleProfileId == Guid.Empty)
            return null;

        var coverId = await _context
            .Set<GalleryPreferences>()
            .AsNoTracking()
            .Where(x =>
                x.OwnerModuleProfileId == ownerModuleProfileId)
            .Select(x => x.CoverMediaId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!coverId.HasValue)
            return null;

        var validCover = await _context.Set<GalleryMedia>()
            .AsNoTracking()
            .AnyAsync(
                x =>
                    x.Id == coverId.Value &&
                    x.OwnerModuleProfileId == ownerModuleProfileId &&
                    x.Module == HobbiesModule &&
                    !x.IsDeleted,
                cancellationToken);

        return validCover ? coverId : null;
    }

    public async Task<bool> UpdateTitleAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty ||
            ownerModuleProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalizedTitle = title.Trim();

        var updatedRows = await _context.Set<GalleryMedia>()
            .Where(x =>
                x.Id == mediaId &&
                x.OwnerModuleProfileId == ownerModuleProfileId &&
                x.Module == HobbiesModule &&
                !x.IsDeleted)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.Title,
                    normalizedTitle),
                cancellationToken);

        return updatedRows == 1;
    }

    public async Task<bool> SetCoverAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty ||
            ownerModuleProfileId == Guid.Empty)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tracker = _context.ChangeTracker;
        tracker.DetectChanges();
        if (tracker.HasChanges())
            throw new InvalidOperationException("The Gallery context contains pending changes.");

        // Leave caller-owned instances untouched and avoid EF identity conflicts or stale reads.
        if (tracker.Entries<GalleryPreferences>().Any(entry =>
            entry.Entity.OwnerModuleProfileId == ownerModuleProfileId))
        {
            throw new InvalidOperationException("The owner's Gallery preferences are already tracked.");
        }

        var database = _context.Database;
        if (database.CurrentTransaction is not null || Transaction.Current is not null
            || database.GetEnlistedTransaction() is not null)
        {
            throw new InvalidOperationException("Cover selection requires its own transaction.");
        }
        // An outer EF strategy can also retry the entire operation.
        if (ExecutionStrategy.Current?.RetriesOnFailure == true
            || database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Cover selection requires a non-retrying execution strategy.");

        await using var transaction =
            await database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);

        var mediaExists = await _context.Set<GalleryMedia>()
            .AnyAsync(
                x =>
                    x.Id == mediaId &&
                    x.OwnerModuleProfileId == ownerModuleProfileId &&
                    x.Module == HobbiesModule &&
                    !x.IsDeleted,
                cancellationToken);

        if (!mediaExists)
            return false;

        var preferences = await _context
            .Set<GalleryPreferences>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x =>
                    x.OwnerModuleProfileId == ownerModuleProfileId,
                cancellationToken);

        var isNew = preferences is null;
        preferences ??= new GalleryPreferences
        {
            OwnerModuleProfileId = ownerModuleProfileId,
            CoverMediaId = mediaId
        };

        var entry = _context.Entry(preferences);
        try
        {
            if (isNew)
            {
                entry.State = EntityState.Added;
            }
            else if (preferences.CoverMediaId != mediaId)
            {
                entry.State = EntityState.Unchanged;
                var cover = entry.Property(preference => preference.CoverMediaId);
                cover.CurrentValue = mediaId;
                cover.IsModified = true;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}
