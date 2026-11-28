namespace Andril.Hobbies.Interfaces;

internal enum GalleryLogicalDeleteOutcome
{
    Unavailable,
    SelectedCover,
    Denied,
    Deleted
}

internal interface IGalleryLifecycleRepository
{
    Task<GalleryLogicalDeleteOutcome> DeleteOwnedAsync(Guid mediaId, Guid ownerProfileId,
        Func<CancellationToken, Task<bool>> reauthorize, CancellationToken cancellationToken);
}
