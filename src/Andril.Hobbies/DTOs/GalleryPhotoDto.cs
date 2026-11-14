
namespace Andril.Hobbies.DTOs;

public sealed record GalleryPhotoDto(
    Guid Id,
    string Title,
    string? Description,
    string Category,
    string MediaType,
    DateTimeOffset CreatedAtUtc,
    bool IsPrivate,
    bool IsCover
);
