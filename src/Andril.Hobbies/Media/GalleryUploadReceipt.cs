using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Media;

// Immutable transition rules for a receipt store. The store must enforce uniqueness and
// compare the revision in its write transaction before persisting a returned state.
internal sealed class GalleryUploadReceipt
{
    private GalleryUploadReceipt(GalleryUploadBinding binding, long revision, GalleryUploadObject? verified,
        bool completed)
    {
        Binding = binding;
        Revision = revision;
        Verified = verified;
        Completed = completed;
    }

    internal GalleryUploadBinding Binding { get; }
    internal long Revision { get; }
    internal GalleryUploadObject? Verified { get; }
    internal bool Completed { get; }

    internal static GalleryUploadReceipt? Reserve(GalleryUploadBinding binding) =>
        binding.Matches(binding.Intent) ? new(binding, 0, null, false) : null;

    internal GalleryUploadReservation Replay(GalleryUploadIntent intent)
    {
        if (intent != Binding.Intent)
            return new(GalleryUploadReservationState.Conflict, null);
        return Completed
            ? new(GalleryUploadReservationState.Completed, Binding, Verified)
            : new(GalleryUploadReservationState.Inspect, Binding);
    }

    internal GalleryUploadReceipt? Verify(long expectedRevision, GalleryUploadObject evidence)
    {
        if (expectedRevision != Revision || !evidence.Matches(Binding))
            return null;
        if (Verified is not null)
            return Verified == evidence ? this : null;
        return new(Binding, Revision + 1, evidence, false);
    }

    // Persist this transition together with metadata and immutable generation, or persist neither.
    internal GalleryUploadReceipt? Complete(long expectedRevision, GalleryUploadObject evidence)
    {
        if (expectedRevision != Revision || Verified is null || Verified != evidence)
            return null;
        return Completed ? this : new(Binding, Revision + 1, evidence, true);
    }
}
