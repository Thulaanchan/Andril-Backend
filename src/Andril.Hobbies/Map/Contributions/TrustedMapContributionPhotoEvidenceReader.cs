using System.Text.Json.Serialization;
using Andril.Shared.Authentication;

namespace Andril.Hobbies.Map.Contributions;

internal enum MapPhotoOrigin
{
    InAppCamera = 1,
    GalleryUpload = 2
}

internal sealed record MapCameraEvidence(
    [property: JsonIgnore] Guid AccountSubjectId,
    [property: JsonIgnore] Guid OwnerHobbiesModuleProfileId,
    [property: JsonIgnore] Guid AttractionId,
    [property: JsonIgnore] Guid CaptureSessionId,
    [property: JsonIgnore] string PrivateObjectReference,
    [property: JsonIgnore] DateTimeOffset CapturedAtUtc,
    [property: JsonIgnore] MapPhotoOrigin Origin)
{
    public override string ToString() => "Map camera evidence (internal).";
}

/// <summary>Reads a server-owned binding for one current, private Map photo.</summary>
/// <remarks>
/// Implementations must confirm the authorized private object exists and refers to the
/// same immutable media version that was validated. A session must have at most one
/// eligible binding, and that binding must not be shared across sessions. Origin and
/// capture time must come from trusted capture infrastructure.
/// Return null on missing, revoked, ambiguous or unavailable evidence.
/// </remarks>
internal interface IMapCameraEvidenceSource
{
    Task<MapCameraEvidence?> FindAsync(
        Guid accountSubjectId, Guid ownerHobbiesModuleProfileId, Guid attractionId,
        Guid captureSessionId, CancellationToken cancellationToken);
}

/// <summary>Internal verification boundary. Production has no approved evidence source.</summary>
internal sealed class TrustedMapContributionPhotoEvidenceReader(
    ICurrentUser currentUser,
    IMapCaptureSessionAccessReader accessReader,
    IMapContributionCaptureSessionRepository sessions,
    IMapCameraEvidenceSource evidenceSource,
    TimeProvider timeProvider) : IMapContributionPhotoEvidenceReader
{
    public async Task<MapContributionPhotoVerificationResult> VerifyAsync(
        Guid ownerHobbiesModuleProfileId, Guid attractionId, Guid captureSessionId,
        string? privatePhotoStorageObjectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subjectId || subjectId == Guid.Empty
            || ownerHobbiesModuleProfileId == Guid.Empty || attractionId == Guid.Empty || captureSessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(privatePhotoStorageObjectKey))
        {
            return MapContributionPhotoVerificationResult.Denied;
        }

        try
        {
            var owner = await accessReader.ResolveAuthorizedProfileIdAsync(attractionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (owner != ownerHobbiesModuleProfileId)
                return MapContributionPhotoVerificationResult.Denied;

            var session = await sessions.FindOwnedAsync(captureSessionId, ownerHobbiesModuleProfileId,
                attractionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session is null || session.SessionId != captureSessionId
                || session.OwnerHobbiesModuleProfileId != ownerHobbiesModuleProfileId
                || session.AttractionId != attractionId || session.CompletedAtUtc is not { } completedAt
                || !MapContributionSubmissionWindow.Allows(session, timeProvider.GetUtcNow()))
            {
                return MapContributionPhotoVerificationResult.Denied;
            }

            var evidence = await evidenceSource.FindAsync(subjectId, ownerHobbiesModuleProfileId,
                attractionId, captureSessionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return evidence is not null
                && currentUser.IsAuthenticated && currentUser.UserId == subjectId
                && evidence.AccountSubjectId == subjectId
                && evidence.OwnerHobbiesModuleProfileId == ownerHobbiesModuleProfileId
                && evidence.AttractionId == attractionId && evidence.CaptureSessionId == captureSessionId
                && string.Equals(evidence.PrivateObjectReference, privatePhotoStorageObjectKey, StringComparison.Ordinal)
                && evidence.Origin == MapPhotoOrigin.InAppCamera
                && evidence.CapturedAtUtc >= session.StartedAtUtc && evidence.CapturedAtUtc <= completedAt
                && MapContributionSubmissionWindow.Allows(session, timeProvider.GetUtcNow())
                ? MapContributionPhotoVerificationResult.Verified
                : MapContributionPhotoVerificationResult.Denied;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MapContributionPhotoVerificationResult.Denied;
        }
    }
}
