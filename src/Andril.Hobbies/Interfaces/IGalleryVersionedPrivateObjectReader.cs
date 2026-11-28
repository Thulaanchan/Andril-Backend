namespace Andril.Hobbies.Interfaces;

internal sealed record GalleryVersionedPrivateObject(GalleryPrivateObjectEvidence Binding, string Version);

internal sealed record GalleryPrivateObjectContent(GalleryVersionedPrivateObject Object, ReadOnlyMemory<byte> Bytes);

// Controlled interface. Implementations must authorize the full binding and enforce the immutable
// version and byte bound during retrieval, with no fallback to the latest object.
internal interface IGalleryVersionedPrivateObjectReader
{
    Task<GalleryVersionedPrivateObject?> FindAuthorizedAsync(GalleryPrivateObjectEvidence binding,
        CancellationToken cancellationToken);

    Task<GalleryPrivateObjectContent?> ReadBoundedAsync(GalleryVersionedPrivateObject expected, int maximumBytes,
        CancellationToken cancellationToken);
}
