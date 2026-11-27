using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;

namespace Andril.API.Authorization.Hobbies;

internal enum GalleryReadOperation
{
    Metadata,
    PrivateObjectEvidence
}

// Explicit backend composition. The existing Gallery registration and HTTP route inventory do not use it.
internal sealed class GalleryReadBoundary(
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
        GalleryActor? actor = null;
        var allowed = false;
        try
        {
            actor = await actors.ResolveAsync(cancellationToken);
            if (actor is not null)
                allowed = await authorizeOperation(actor, operation, cancellationToken);
        }
        catch (Exception)
        {
            // Missing authority or unavailable decisions receive the same denial.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (actor is null || !allowed)
            throw new UnauthorizedAccessException("An active Hobbies profile is required.");

        return new GalleryService(repository, new ResolvedActorContext(actor), pages);
    }

    private sealed class ResolvedActorContext(GalleryActor actor) : IGalleryActorContext
    {
        public GalleryActor Current => actor;
    }
}
