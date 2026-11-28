using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Media;

// Evidence check for controlled recovery. A future writer must acquire authoritative policy
// and serialize the tombstone transition against physical cleanup in its transaction.
internal static class GalleryRecoveryEligibility
{
    internal static bool Matches(Guid mediaId, Guid authorizedOwnerProfileId, bool isTombstoned,
        GalleryUploadReceipt receipt, GalleryUploadObject? observed)
    {
        var binding = receipt.Binding.Object;
        return isTombstoned && receipt.Completed && mediaId != Guid.Empty && authorizedOwnerProfileId != Guid.Empty &&
            binding.MediaId == mediaId && binding.OwnerModuleProfileId == authorizedOwnerProfileId &&
            binding.Module == "Hobbies" && observed is not null && receipt.Verified == observed;
    }
}
