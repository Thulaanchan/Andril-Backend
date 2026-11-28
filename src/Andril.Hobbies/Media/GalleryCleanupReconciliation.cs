using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Media;

internal enum GalleryDeletionObservation
{
    Unavailable,
    ObjectPresent,
    AbsenceUnconfirmed,
    ExactGenerationDeleted
}

internal enum GalleryCleanupResolution
{
    Pending,
    ExpectedGenerationStillPresent,
    ConflictingEvidence,
    DeletionVerified
}

// Interprets evidence only. Even an exact surviving generation grants no retry or delete permit.
internal static class GalleryCleanupReconciliation
{
    internal static GalleryCleanupResolution Resolve(GalleryUploadObject expected,
        GalleryDeletionObservation observation, GalleryUploadObject? evidence)
    {
        if (!expected.Binding.Matches(expected.Binding.Intent) || !expected.Matches(expected.Binding))
            return GalleryCleanupResolution.ConflictingEvidence;
        return observation switch
        {
            GalleryDeletionObservation.ObjectPresent when evidence == expected =>
                GalleryCleanupResolution.ExpectedGenerationStillPresent,
            GalleryDeletionObservation.ExactGenerationDeleted when evidence == expected =>
                GalleryCleanupResolution.DeletionVerified,
            GalleryDeletionObservation.ObjectPresent or GalleryDeletionObservation.ExactGenerationDeleted =>
                GalleryCleanupResolution.ConflictingEvidence,
            _ => GalleryCleanupResolution.Pending
        };
    }
}
