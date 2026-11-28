using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class GalleryUploadReceiptTests
{
    private readonly GalleryUploadIntent intent = new(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64));

    [Fact]
    public void Reserve_RequiresValidScopedIdentityAndFingerprint()
    {
        var binding = Binding();
        var receipt = Assert.IsType<GalleryUploadReceipt>(GalleryUploadReceipt.Reserve(binding));
        Assert.Equal(GalleryMutationKind.Upload, intent.Key.Kind);
        Assert.Equal("Hobbies", intent.Key.Module);
        Assert.Equal(GalleryUploadReservationState.Inspect, receipt.Replay(intent).State);
        Assert.Null(receipt.Replay(intent with { OwnerProfileId = Guid.NewGuid() }).Binding);
        Assert.Equal(GalleryUploadReservationState.Conflict, receipt.Replay(intent with { Sha256 = new string('B', 64) }).State);
        Assert.NotEqual(intent.Key, (intent with { OperationId = Guid.NewGuid() }).Key);
        Assert.NotEqual(intent.Key, (intent with { OwnerProfileId = Guid.NewGuid() }).Key);
        Assert.Null(GalleryUploadReceipt.Reserve(binding with { Intent = intent with { OperationId = Guid.Empty } }));
        Assert.Null(GalleryUploadReceipt.Reserve(binding with { Intent = intent with { Sha256 = "malformed" } }));
        Assert.Null(GalleryUploadReceipt.Reserve(binding with { Object = binding.Object with { Module = "Learning" } }));
    }

    [Fact]
    public void Transitions_RejectStaleRevisionAndSubstitutionAndRequireVerifiedEvidence()
    {
        var binding = Binding();
        var evidence = new GalleryUploadObject(binding, "generation-one");
        var receipt = Assert.IsType<GalleryUploadReceipt>(GalleryUploadReceipt.Reserve(binding));
        Assert.Null(receipt.Complete(0, evidence));
        Assert.Null(receipt.Verify(0, evidence with { Version = "" }));
        Assert.Null(receipt.Verify(0, evidence with { Binding = binding with { Intent = intent with { Sha256 = new string('B', 64) } } }));
        var verified = Assert.IsType<GalleryUploadReceipt>(receipt.Verify(0, evidence));
        Assert.Equal(1, verified.Revision);
        Assert.Null(verified.Verify(0, evidence));
        Assert.Null(verified.Complete(0, evidence));
        Assert.Null(verified.Complete(1, evidence with { Version = "generation-two" }));
        Assert.Equal(GalleryUploadReservationState.Inspect, verified.Replay(intent).State);

        var completed = Assert.IsType<GalleryUploadReceipt>(verified.Complete(1, evidence));
        Assert.Equal(2, completed.Revision);
        Assert.Null(completed.Verify(2, evidence with { Version = "generation-two" }));
        Assert.Same(completed, completed.Complete(2, evidence));
        Assert.Equal(new GalleryUploadReservation(GalleryUploadReservationState.Completed, binding, evidence), completed.Replay(intent));
    }

    [Fact]
    public void RecoveryEvidence_RequiresTombstoneAndExactCompletedGeneration()
    {
        var binding = Binding();
        var evidence = new GalleryUploadObject(binding, "generation-one");
        var receipt = Assert.IsType<GalleryUploadReceipt>(GalleryUploadReceipt.Reserve(binding));
        var verified = Assert.IsType<GalleryUploadReceipt>(receipt.Verify(0, evidence));
        var completed = Assert.IsType<GalleryUploadReceipt>(verified.Complete(1, evidence));
        var mediaId = binding.Object.MediaId;
        Assert.True(GalleryRecoveryEligibility.Matches(mediaId, intent.OwnerProfileId, true, completed, evidence));
        Assert.False(GalleryRecoveryEligibility.Matches(mediaId, intent.OwnerProfileId, false, completed, evidence));
        Assert.False(GalleryRecoveryEligibility.Matches(mediaId, intent.OwnerProfileId, true, verified, evidence));
        Assert.False(GalleryRecoveryEligibility.Matches(mediaId, Guid.NewGuid(), true, completed, evidence));
        Assert.False(GalleryRecoveryEligibility.Matches(Guid.NewGuid(), intent.OwnerProfileId, true, completed, evidence));
        Assert.False(GalleryRecoveryEligibility.Matches(mediaId, intent.OwnerProfileId, true, completed, null));
        Assert.False(GalleryRecoveryEligibility.Matches(mediaId, intent.OwnerProfileId, true, completed, evidence with { Version = "generation-two" }));
    }

    private GalleryUploadBinding Binding() => new(intent, new(Guid.NewGuid(), intent.OwnerProfileId, "Hobbies", "test-object"));
}
