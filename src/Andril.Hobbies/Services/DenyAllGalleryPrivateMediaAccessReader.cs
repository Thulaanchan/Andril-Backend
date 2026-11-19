using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Services;

/// <summary>
/// Denies Gallery access while authoritative authorization and signing are unavailable.
/// </summary>
public sealed class DenyAllGalleryPrivateMediaAccessReader : IGalleryPrivateMediaAccessReader
{
    public Task<GalleryPrivateMediaAccess?> FindAuthorizedAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GalleryPrivateMediaAccess?>(null);
    }
}
