
using System.Data;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Hobbies;

public sealed class GalleryRepository<TDbContext>
    : IGalleryRepository
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

        await using var transaction =
            await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
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
            .SingleOrDefaultAsync(
                x =>
                    x.OwnerModuleProfileId == ownerModuleProfileId,
                cancellationToken);

        if (preferences is null)
        {
            preferences = new GalleryPreferences
            {
                OwnerModuleProfileId = ownerModuleProfileId,
                CoverMediaId = mediaId
            };

            _context.Set<GalleryPreferences>()
                .Add(preferences);
        }
        else
        {
            preferences.CoverMediaId = mediaId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return true;
    }
}
