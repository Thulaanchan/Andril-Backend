using Andril.Hobbies.Media;

namespace Andril.Hobbies.Interfaces;

internal interface IGalleryRecoveryEvidenceReader
{
    Task<GalleryUploadReceipt?> FindOwnedTombstoneAsync(Guid mediaId, Guid ownerProfileId,
        CancellationToken cancellationToken);
}
