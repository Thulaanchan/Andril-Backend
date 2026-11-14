
namespace Andril.Hobbies.Entities;

public sealed class GalleryMedia
{
    public Guid Id { get; set; }

    public Guid OwnerModuleProfileId { get; set; }

    public string Module { get; set; } = "Hobbies";

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Category { get; set; } = string.Empty;

    public string MediaType { get; set; } = "Photo";

    public string StorageObjectKey { get; set; } = string.Empty;

    public bool IsPrivate { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IsDeleted { get; set; }
}
