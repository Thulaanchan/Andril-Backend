
namespace Andril.Hobbies.Entities;

public sealed class GalleryPreferences
{
    // The owner of this Hobbies gallery.
    public Guid OwnerModuleProfileId { get; set; }

    // Selected cover photo.
    // Null means no cover has been selected.
    public Guid? CoverMediaId { get; set; }
}
