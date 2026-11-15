
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Services;

public sealed class GalleryService : IGalleryService
{
    private const string HobbiesModule = "Hobbies";

    private readonly IGalleryRepository _repository;
    private readonly IGalleryActorContext _actorContext;

    public GalleryService(
        IGalleryRepository repository,
        IGalleryActorContext actorContext)
    {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));

        _actorContext = actorContext
            ?? throw new ArgumentNullException(nameof(actorContext));
    }

    private Guid RequireActiveHobbiesProfile()
    {
        var actor = _actorContext.Current;

        if (actor is null ||
            !actor.IsActive ||
            actor.ModuleProfileId == Guid.Empty ||
            !string.Equals(
                actor.Module,
                HobbiesModule,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "An active Hobbies profile is required.");
        }

        return actor.ModuleProfileId;
    }

    private static bool IsAccessible(
        GalleryMedia? media,
        Guid ownerProfileId)
    {
        return media is not null &&
               !media.IsDeleted &&
               media.OwnerModuleProfileId == ownerProfileId &&
               string.Equals(
                   media.Module,
                   HobbiesModule,
                   StringComparison.Ordinal);
    }

    private static GalleryPhotoDto ToDto(
        GalleryMedia media,
        Guid? coverId)
    {
        return new GalleryPhotoDto(
            media.Id,
            media.Title,
            media.Description,
            media.Category,
            media.MediaType,
            media.CreatedAtUtc,
            media.IsPrivate,
            media.Id == coverId
        );
    }

    public async Task<IReadOnlyList<GalleryPhotoDto>> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();

        var media = await _repository.GetOwnerGalleryAsync(
            ownerId,
            cancellationToken);

        var coverId = await _repository.GetCoverIdAsync(
            ownerId,
            cancellationToken);

        return media
            .Where(item => IsAccessible(item, ownerId))
            .Select(item => ToDto(item, coverId))
            .ToArray();
    }

    public async Task<GalleryPhotoDto?> GetMineByIdAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();

        if (mediaId == Guid.Empty)
            return null;

        var media = await _repository.GetOwnedByIdAsync(
            mediaId,
            ownerId,
            cancellationToken);

        if (!IsAccessible(media, ownerId))
            return null;

        var coverId = await _repository.GetCoverIdAsync(
            ownerId,
            cancellationToken);

        return ToDto(media!, coverId);
    }

    public async Task<bool> UpdateTitleAsync(
        Guid mediaId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();

        if (mediaId == Guid.Empty)
            return false;

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                "Gallery title cannot be empty.",
                nameof(title));
        }

        var media = await _repository.GetOwnedByIdAsync(
            mediaId,
            ownerId,
            cancellationToken);

        if (!IsAccessible(media, ownerId))
            return false;

        return await _repository.UpdateTitleAsync(
            mediaId,
            ownerId,
            title.Trim(),
            cancellationToken);
    }

    public async Task<bool> SelectCoverAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();

        if (mediaId == Guid.Empty)
            return false;

        var media = await _repository.GetOwnedByIdAsync(
            mediaId,
            ownerId,
            cancellationToken);

        if (!IsAccessible(media, ownerId))
            return false;

        return await _repository.SetCoverAsync(
            mediaId,
            ownerId,
            cancellationToken);
    }
}
