using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryCleanupReconciliationTests
{
    [Fact]
    public void Resolve_RequiresExactGenerationAndDoesNotInferDeletionFromAbsence()
    {
        var owner = Guid.NewGuid();
        var intent = new GalleryUploadIntent(Guid.NewGuid(), owner, new string('A', 64));
        var expected = new GalleryUploadObject(new(intent, new(Guid.NewGuid(), owner, "Hobbies", "test-key")), "v1");
        var replacement = expected with { Version = "v2" };
        Assert.Equal(GalleryCleanupResolution.Pending,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.Unavailable, null));
        Assert.Equal(GalleryCleanupResolution.Pending,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.AbsenceUnconfirmed, null));
        Assert.Equal(GalleryCleanupResolution.ExpectedGenerationStillPresent,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.ObjectPresent, expected));
        Assert.Equal(GalleryCleanupResolution.ConflictingEvidence,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.ObjectPresent, replacement));
        Assert.Equal(GalleryCleanupResolution.ConflictingEvidence,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.ExactGenerationDeleted, replacement));
        Assert.Equal(GalleryCleanupResolution.ConflictingEvidence,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.ExactGenerationDeleted, null));
        Assert.Equal(GalleryCleanupResolution.DeletionVerified,
            GalleryCleanupReconciliation.Resolve(expected, GalleryDeletionObservation.ExactGenerationDeleted, expected));
    }
}
