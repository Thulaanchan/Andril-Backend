
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Services;

public sealed class GalleryService : IGalleryService
{
    private const string HobbiesModule = "Hobbies";

    private readonly IGalleryRepository _repository;
    private readonly IGalleryActorContext _actorContext;
    private readonly IGalleryPageRepository? _pages;

    public GalleryService(
        IGalleryRepository repository,
        IGalleryActorContext actorContext)
    {
        _repository = repository
            ?? throw new ArgumentNullException(nameof(repository));

        _actorContext = actorContext
            ?? throw new ArgumentNullException(nameof(actorContext));
    }

    // Explicit offline construction only; existing runtime registration and constructor are unchanged.
    public GalleryService(IGalleryRepository repository, IGalleryActorContext actorContext,
        IGalleryPageRepository pages) : this(repository, actorContext)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
    }

    // Deliberately absent from IGalleryService and HTTP contracts until runtime integration is approved.
    internal async Task<GalleryPhotoPage> GetMinePageAsync(int pageSize, int trustedTestOnlyMaximum,
        GalleryPagePosition? after = null, CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();
        GalleryPaging.Validate(ownerId, pageSize, trustedTestOnlyMaximum, after, cancellationToken);
        if (_pages is null)
            throw new InvalidOperationException("Offline Gallery paging is not configured.");
        try
        {
            var page = await _pages.GetOwnerGalleryPageAsync(ownerId, pageSize,
                trustedTestOnlyMaximum, after, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Reject an invalid port result as a whole; never return foreign metadata or continuation.
            if (page.Items.Count > pageSize || page.Items.Any(item => !IsAccessible(item, ownerId)))
                throw new InvalidOperationException();
            GalleryPaging.Validate(ownerId, pageSize, trustedTestOnlyMaximum, page.Next, cancellationToken);
            var coverId = await _repository.GetCoverIdAsync(ownerId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new GalleryPhotoPage(Array.AsReadOnly(page.Items.Select(item => ToDto(item, coverId)).ToArray()), page.Next);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // This offline boundary has no public error contract. Never return provider/private details.
            throw new InvalidOperationException("Gallery page retrieval is unavailable.");
        }
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
        cancellationToken.ThrowIfCancellationRequested();

        var media = await _repository.GetOwnerGalleryAsync(
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var coverId = await _repository.GetCoverIdAsync(
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

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
        cancellationToken.ThrowIfCancellationRequested();

        if (mediaId == Guid.Empty)
            return null;

        var media = await _repository.GetOwnedByIdAsync(
            mediaId,
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAccessible(media, ownerId))
            return null;

        var coverId = await _repository.GetCoverIdAsync(
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return ToDto(media!, coverId);
    }

    public async Task<bool> UpdateTitleAsync(
        Guid mediaId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();
        cancellationToken.ThrowIfCancellationRequested();

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
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAccessible(media, ownerId))
            return false;

        var updated = await _repository.UpdateTitleAsync(
            mediaId,
            ownerId,
            title.Trim(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return updated;
    }

    public async Task<bool> SelectCoverAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var ownerId = RequireActiveHobbiesProfile();
        cancellationToken.ThrowIfCancellationRequested();

        if (mediaId == Guid.Empty)
            return false;

        var media = await _repository.GetOwnedByIdAsync(
            mediaId,
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAccessible(media, ownerId))
            return false;

        var selected = await _repository.SetCoverAsync(
            mediaId,
            ownerId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return selected;
    }
}
