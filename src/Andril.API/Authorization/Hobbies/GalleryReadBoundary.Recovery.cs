using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;

namespace Andril.API.Authorization.Hobbies;

internal sealed partial class GalleryReadBoundary
{
    // Controlled evidence only. There is no restoration or production route.
    internal Task<GalleryMetadataResult<GalleryMutationReceipt>> VerifyRecoveryEvidenceAsync(Guid mediaId,
        IGalleryRecoveryEvidenceReader receipts, IGalleryPrivateObjectMutations provider,
        CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryMutationReceipt>(GalleryReadOperation.RecoveryEvidence, async (scope, token) =>
        {
            if (mediaId == Guid.Empty)
                return new(GalleryMetadataStatus.InvalidMediaId);
            var owner = scope.Actor.ModuleProfileId;
            var receipt = await receipts.FindOwnedTombstoneAsync(mediaId, owner, token);
            if (receipt is null || !GalleryRecoveryEligibility.Matches(mediaId, owner, true, receipt, receipt.Verified))
                return new(GalleryMetadataStatus.MediaUnavailable);
            if (await ResolveAuthorizedScopeAsync(GalleryReadOperation.RecoveryEvidence, token) != scope)
                return new(GalleryMetadataStatus.Denied);

            var observed = await provider.InspectAsync(receipt.Binding, token);
            token.ThrowIfCancellationRequested();
            if (!GalleryRecoveryEligibility.Matches(mediaId, owner, true, receipt, observed))
                return new(GalleryMetadataStatus.MediaUnavailable);
            var current = await receipts.FindOwnedTombstoneAsync(mediaId, owner, token);
            if (current is null || current.Revision != receipt.Revision ||
                !GalleryRecoveryEligibility.Matches(mediaId, owner, true, current, observed))
                return new(GalleryMetadataStatus.MediaUnavailable);
            return new(GalleryMetadataStatus.Success, new(mediaId));
        }, cancellationToken);
}
