using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Andril.Hobbies.Services;

namespace Andril.API.Authorization.Hobbies;

internal enum GalleryReadOperation
{
    Metadata,
    PrivateObjectEvidence,
    MetadataList,
    MetadataDetail,
    PrivateMediaDelivery,
    UploadValidation,
    UploadMutation,
    LogicalDeletion
}

// Explicit backend composition. The existing Gallery registration and HTTP route inventory do not use it.
internal sealed partial class GalleryReadBoundary(
    AccountBackedGalleryActorResolver actors,
    Func<GalleryActor, GalleryReadOperation, CancellationToken, Task<bool>> authorizeOperation,
    IGalleryRepository repository,
    IGalleryPageRepository pages,
    int trustedPageMaximum)
{
    internal Task<GalleryPhotoDto?> GetByIdAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        ReadAsync((service, token) => service.GetMineByIdAsync(mediaId, token), cancellationToken);

    internal Task<GalleryPhotoPage> GetPageAsync(int pageSize, GalleryPagePosition? after = null,
        CancellationToken cancellationToken = default) =>
        ReadAsync((service, token) => service.GetMinePageAsync(pageSize, trustedPageMaximum, after, token),
            cancellationToken);

    internal Task<GalleryMetadataResult<GalleryMetadataPage>> ReadMetadataPageAsync(string? pageSize, string? cursor,
        GalleryPageBounds bounds, GalleryMetadataCursor codec, CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryMetadataPage>(GalleryReadOperation.MetadataList, async (scope, token) =>
        {
            if (bounds.Maximum > trustedPageMaximum)
                return new(GalleryMetadataStatus.DependencyUnavailable);
            if (!bounds.TryResolve(pageSize, out var size))
                return new(GalleryMetadataStatus.InvalidPageSize);
            GalleryPagePosition? position = null;
            if (cursor is not null && !codec.TryDecode(scope.Actor, cursor, out position))
                return new(GalleryMetadataStatus.InvalidCursor);

            if (await ResolveAuthorizedScopeAsync(GalleryReadOperation.MetadataList, token) != scope)
                return new(GalleryMetadataStatus.Denied);
            var page = await CreateService(scope.Actor).GetMinePageAsync(size, trustedPageMaximum, position, token);
            var next = page.Next is null ? null : codec.Encode(scope.Actor, page.Next);
            return new(GalleryMetadataStatus.Success, new GalleryMetadataPage(page.Items, next));
        }, cancellationToken);

    internal Task<GalleryMetadataResult<GalleryPhotoDto>> ReadMetadataDetailAsync(string mediaId,
        CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryPhotoDto>(GalleryReadOperation.MetadataDetail, async (scope, token) =>
        {
            if (mediaId.Length != 36 || !Guid.TryParseExact(mediaId, "D", out var id) || id == Guid.Empty)
                return new(GalleryMetadataStatus.InvalidMediaId);
            var photo = await CreateService(scope.Actor).GetMineByIdAsync(id, token);
            return photo is null ? new(GalleryMetadataStatus.MediaUnavailable) : new(GalleryMetadataStatus.Success, photo);
        }, cancellationToken);

    private async Task<GalleryMetadataResult<T>> ReadForReleaseAsync<T>(GalleryReadOperation operation,
        Func<GalleryReadScope, CancellationToken, Task<GalleryMetadataResult<T>>> read, CancellationToken token)
        where T : class
    {
        try
        {
            var scope = await ResolveAuthorizedScopeAsync(operation, token);
            if (scope is null)
                return new(GalleryMetadataStatus.Denied);
            var result = await read(scope, token);
            token.ThrowIfCancellationRequested();
            // Recheck before handing buffered data to the caller. A production release fence needs a separate contract.
            if ((result.Status is GalleryMetadataStatus.Success or GalleryMetadataStatus.MediaUnavailable or
                GalleryMetadataStatus.MutationPending or GalleryMetadataStatus.MutationConflict) &&
                await ResolveAuthorizedScopeAsync(operation, token) != scope)
                return new(GalleryMetadataStatus.Denied);
            return result;
        }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            return new(GalleryMetadataStatus.DependencyUnavailable);
        }
    }

    internal Task<GalleryMetadataResult<byte[]>> ReadPrivateMediaAsync(Guid mediaId,
        IGalleryVersionedPrivateObjectReader objects, int maximumBytes, CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<byte[]>(GalleryReadOperation.PrivateMediaDelivery, async (scope, token) =>
        {
            if (maximumBytes < 1)
                return new(GalleryMetadataStatus.DependencyUnavailable);

            var service = CreateService(scope.Actor);
            var binding = await service.FindPrivateMediaBindingAsync(mediaId, token);
            if (binding is null)
                return new(GalleryMetadataStatus.MediaUnavailable);
            if (await ResolveAuthorizedScopeAsync(GalleryReadOperation.PrivateMediaDelivery, token) != scope)
                return new(GalleryMetadataStatus.Denied);

            var version = await objects.FindAuthorizedAsync(binding, token);
            token.ThrowIfCancellationRequested();
            if (version is null || version.Binding != binding || string.IsNullOrWhiteSpace(version.Version) ||
                (binding.ProviderGeneration is not null && version.Version != binding.ProviderGeneration))
                return new(GalleryMetadataStatus.MediaUnavailable);
            if (await ResolveAuthorizedScopeAsync(GalleryReadOperation.PrivateMediaDelivery, token) != scope)
                return new(GalleryMetadataStatus.Denied);

            var content = await objects.ReadBoundedAsync(version, maximumBytes, token);
            token.ThrowIfCancellationRequested();
            if (content is null || content.Object != version || content.Bytes.IsEmpty || content.Bytes.Length > maximumBytes)
                return new(GalleryMetadataStatus.MediaUnavailable);
            var buffered = content.Bytes.ToArray();
            if (await service.FindPrivateMediaBindingAsync(mediaId, token) != binding)
                return new(GalleryMetadataStatus.MediaUnavailable);
            return new(GalleryMetadataStatus.Success, buffered);
        }, cancellationToken);

    internal Task<GalleryMetadataResult<GalleryValidatedUpload>> ValidateUploadAsync(Stream input, string? contentType,
        GalleryJpegUploadValidator validator, CancellationToken cancellationToken = default) =>
        ReadForReleaseAsync<GalleryValidatedUpload>(GalleryReadOperation.UploadValidation, async (_, token) =>
        {
            var upload = await validator.ValidateAsync(input, contentType, token);
            return upload is null ? new(GalleryMetadataStatus.InvalidMediaContent) : new(GalleryMetadataStatus.Success, upload);
        }, cancellationToken);

    internal async Task<bool> VerifyPrivateMediaAsync(Guid mediaId, IGalleryPrivateObjectEvidenceReader objects,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var service = await CreateServiceAsync(GalleryReadOperation.PrivateObjectEvidence, cancellationToken);
            return await service.VerifyPrivateMediaAsync(mediaId, objects, cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private async Task<T> ReadAsync<T>(Func<GalleryService, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        var service = await CreateServiceAsync(GalleryReadOperation.Metadata, cancellationToken);
        try
        {
            return await read(service, cancellationToken);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Gallery retrieval is unavailable.");
        }
    }

    private async Task<GalleryService> CreateServiceAsync(GalleryReadOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GalleryReadScope? scope = null;
        try
        {
            scope = await ResolveAuthorizedScopeAsync(operation, cancellationToken);
        }
        catch (Exception)
        {
            // Missing authority or unavailable decisions receive the same denial.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (scope is null)
            throw new UnauthorizedAccessException("An active Hobbies profile is required.");

        return CreateService(scope.Actor);
    }

    private async Task<GalleryReadScope?> ResolveAuthorizedScopeAsync(GalleryReadOperation operation, CancellationToken token)
    {
        var scope = await actors.ResolveScopeAsync(token);
        token.ThrowIfCancellationRequested();
        if (scope is null)
            return null;
        var allowed = await authorizeOperation(scope.Actor, operation, token);
        token.ThrowIfCancellationRequested();
        return allowed ? scope : null;
    }

    private GalleryService CreateService(GalleryActor actor) =>
        new(repository, new ResolvedActorContext(actor), pages);

    private sealed class ResolvedActorContext(GalleryActor actor) : IGalleryActorContext
    {
        public GalleryActor Current => actor;
    }
}
