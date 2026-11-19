namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Denies verification while authoritative private camera evidence is unavailable.
/// </summary>
public sealed class DenyAllMapContributionPhotoEvidenceReader : IMapContributionPhotoEvidenceReader
{
    public Task<MapContributionPhotoVerificationResult> VerifyAsync(
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        Guid captureSessionId,
        string? privatePhotoStorageObjectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MapContributionPhotoVerificationResult.Denied);
    }
}
