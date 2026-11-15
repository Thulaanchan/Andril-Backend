
namespace Andril.Hobbies.Interfaces;

public sealed record GalleryActor(
    Guid ModuleProfileId,
    string Module,
    bool IsActive
);

public interface IGalleryActorContext
{
    GalleryActor? Current { get; }
}
