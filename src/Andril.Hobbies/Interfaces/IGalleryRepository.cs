
using Andril.Hobbies.Entities;

namespace Andril.Hobbies.Interfaces;

public interface IGalleryRepository
{
    Task<IReadOnlyList<GalleryMedia>> GetOwnerGalleryAsync(
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default);

    Task<GalleryMedia?> GetOwnedByIdAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default);

    Task<Guid?> GetCoverIdAsync(
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateTitleAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        string title,
        CancellationToken cancellationToken = default);

    Task<bool> SetCoverAsync(
        Guid mediaId,
        Guid ownerModuleProfileId,
        CancellationToken cancellationToken = default);
}
