namespace Andril.Hobbies.Interfaces;

// Backend-only object verification. This result contains no delivery grant.
internal interface IGalleryPrivateObjectEvidenceReader
{
    Task<GalleryPrivateObjectEvidence?> FindAuthorizedAsync(GalleryPrivateObjectEvidence expected,
        CancellationToken cancellationToken);
}

internal sealed record GalleryPrivateObjectEvidence(
    Guid MediaId, Guid OwnerModuleProfileId, string Module, string StorageObjectKey);
