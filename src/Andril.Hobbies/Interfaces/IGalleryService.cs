
using Andril.Hobbies.DTOs;

namespace Andril.Hobbies.Interfaces;

public interface IGalleryService
{
    Task<IReadOnlyList<GalleryPhotoDto>> GetMineAsync(
        CancellationToken cancellationToken = default);

    Task<GalleryPhotoDto?> GetMineByIdAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateTitleAsync(
        Guid mediaId,
        string? title,
        CancellationToken cancellationToken = default);

    Task<bool> SelectCoverAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default);
}
