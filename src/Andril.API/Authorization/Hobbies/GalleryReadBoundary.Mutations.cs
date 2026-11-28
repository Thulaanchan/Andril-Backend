using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;

namespace Andril.API.Authorization.Hobbies;

internal sealed record GalleryMutationReceipt(Guid MediaId);

internal sealed partial class GalleryReadBoundary
{
    internal Task<GalleryMetadataResult<GalleryMutationReceipt>> DeletePrivateMediaAsync(Guid mediaId,
        IGalleryLifecycleRepository lifecycle, CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryMutationReceipt>(GalleryReadOperation.LogicalDeletion, async (scope, token) =>
        {
            if (mediaId == Guid.Empty)
                return new(GalleryMetadataStatus.InvalidMediaId);
            var outcome = await lifecycle.DeleteOwnedAsync(mediaId, scope.Actor.ModuleProfileId,
                async ct => await ResolveAuthorizedScopeAsync(GalleryReadOperation.LogicalDeletion, ct) == scope, token);
            return outcome switch
            {
                GalleryLogicalDeleteOutcome.Deleted => new(GalleryMetadataStatus.Success, new(mediaId)),
                GalleryLogicalDeleteOutcome.SelectedCover => new(GalleryMetadataStatus.MutationConflict),
                GalleryLogicalDeleteOutcome.Denied => new(GalleryMetadataStatus.Denied),
                _ => new(GalleryMetadataStatus.MediaUnavailable)
            };
        }, cancellationToken);

    internal Task<GalleryMetadataResult<GalleryMutationReceipt>> UploadPrivateMediaAsync(Guid operationId,
        Stream body, string? contentType, GalleryJpegUploadValidator validator, IGalleryUploadOperations operations,
        IGalleryPrivateObjectMutations provider, CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryMutationReceipt>(GalleryReadOperation.UploadMutation, async (scope, token) =>
        {
            if (operationId == Guid.Empty)
                return new(GalleryMetadataStatus.MutationConflict);
            var upload = await validator.ValidateAsync(body, contentType, token);
            if (upload is null)
                return new(GalleryMetadataStatus.InvalidMediaContent);

            async Task<bool> StillAuthorized(CancellationToken ct) =>
                await ResolveAuthorizedScopeAsync(GalleryReadOperation.UploadMutation, ct) == scope;

            if (!await StillAuthorized(token))
                return new(GalleryMetadataStatus.Denied);
            var intent = new GalleryUploadIntent(operationId, scope.Actor.ModuleProfileId, upload.Sha256);
            try
            {
                var reservation = await operations.ReserveAsync(intent, token);
                if (!await StillAuthorized(token))
                    return new(GalleryMetadataStatus.Denied);
                if (reservation.State == GalleryUploadReservationState.Conflict)
                    return new(GalleryMetadataStatus.MutationConflict);
                var binding = reservation.Binding;
                if (binding is null || !binding.Matches(intent))
                    return new(GalleryMetadataStatus.MutationPending);

                var evidence = reservation.State switch
                {
                    GalleryUploadReservationState.Create => await provider.CreateOnlyAsync(binding, upload.Bytes, token),
                    GalleryUploadReservationState.Inspect => await provider.InspectAsync(binding, token),
                    GalleryUploadReservationState.Completed => reservation.Completed,
                    _ => null
                };
                token.ThrowIfCancellationRequested();
                if (!await StillAuthorized(token))
                    return new(GalleryMetadataStatus.Denied);
                if (evidence is null || !evidence.Matches(binding))
                    return new(GalleryMetadataStatus.MutationPending);
                if (reservation.State != GalleryUploadReservationState.Completed &&
                    !await operations.CompleteAsync(evidence, StillAuthorized, token))
                    return await StillAuthorized(token)
                        ? new(GalleryMetadataStatus.MutationPending)
                        : new(GalleryMetadataStatus.Denied);
                return new(GalleryMetadataStatus.Success, new(binding.Object.MediaId));
            }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                return await StillAuthorized(token)
                    ? new(GalleryMetadataStatus.MutationPending)
                    : new(GalleryMetadataStatus.Denied);
            }
        }, cancellationToken);
}
