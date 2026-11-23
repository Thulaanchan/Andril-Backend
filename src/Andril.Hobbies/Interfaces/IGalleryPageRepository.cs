using Andril.Hobbies.DTOs;

namespace Andril.Hobbies.Interfaces;

/// <summary>
/// Backend-only offline paging port. Owner scope must come from trusted actor evidence;
/// positions are not authorization. Production callers require fresh M2 authorization.
/// The maximum is explicitly test-only, not a Product-approved runtime setting.
/// </summary>
public interface IGalleryPageRepository
{
    Task<GalleryPage> GetOwnerGalleryPageAsync(
        Guid authorizedOwnerModuleProfileId,
        int pageSize,
        int trustedTestOnlyMaximum,
        GalleryPagePosition? after = null,
        CancellationToken cancellationToken = default);
}
