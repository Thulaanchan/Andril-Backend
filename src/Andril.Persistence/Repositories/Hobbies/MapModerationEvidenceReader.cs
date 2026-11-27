using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Hobbies;

// Called only behind the reviewer authorization boundary. No user-supplied
// storage key or account identity reaches the private storage inspector.
internal sealed class MapModerationEvidenceReader(
    IDbContextFactory<HobbiesMapDbContext> contexts, IMapPrivateMediaStorage storage)
    : IMapModerationEvidenceReader
{
    public async Task<VerifiedMapMedia?> InspectAsync(MapModerationItem item, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var binding = await (
            from contribution in db.MapContributions.AsNoTracking()
            join media in db.Set<MapPrivateMediaRow>().AsNoTracking()
                on new { Scope = contribution.AttractionId, Profile = contribution.OwnerHobbiesModuleProfileId, Session = contribution.CaptureSessionId }
                equals new { Scope = media.AttractionId, Profile = media.ProfileId, Session = media.SessionId }
            where contribution.ContributionId == item.Scope.ContributionId
                && contribution.AttractionId == item.Scope.AttractionId
                && contribution.OwnerHobbiesModuleProfileId == item.OwnerProfileId
                && contribution.CaptureSessionId == item.CaptureSessionId
                && media.FinalizedAt != null && media.CleanupClaim == null
                && media.MediaId != Guid.Empty
                && media.PrivateReference != null && media.Version != null && media.Sha256 != null
            select new { Media = media, Key = contribution.PrivatePhotoStorageObjectKey }
        ).SingleOrDefaultAsync(cancellationToken);

        if (binding is null || !string.Equals(binding.Key, binding.Media.PrivateReference, StringComparison.Ordinal))
            return null;

        var row = binding.Media;
        var expected = new VerifiedMapMedia(row.MediaId, row.PrivateReference!, row.Version!, row.Sha256!);
        var actual = await storage.InspectAsync(row.Reservation(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return actual == expected ? expected : null;
    }
}
