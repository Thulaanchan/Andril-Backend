using System.Runtime.CompilerServices;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;

[assembly: InternalsVisibleTo("Andril.UnitTests")]

namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Checks submission prerequisites without persisting a contribution or changing a capture session.
/// </summary>
internal sealed class MapContributionSubmissionPreflight(
    IMapCaptureSessionAccessReader accessReader,
    IMapContributionCaptureSessionRepository captureSessions,
    IMapAttractionRepository attractions,
    IMapContributionPhotoEvidenceReader photoEvidence)
{
    /// <summary>
    /// Returns a decision for internal use. A positive result does not reserve the capture
    /// session or authorize publication, moderation, or rewards.
    /// </summary>
    internal async Task<bool> PassesChecksAsync(
        Guid attractionId,
        Guid captureSessionId,
        MapContributionLocationEvidence? reportedLocation,
        string? privatePhotoStorageObjectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attractionId == Guid.Empty || captureSessionId == Guid.Empty
            || reportedLocation is null || string.IsNullOrWhiteSpace(privatePhotoStorageObjectKey))
        {
            return false;
        }

        // This port resolves the current owner and checks attraction eligibility together.
        var profileId = await accessReader.ResolveAuthorizedProfileIdAsync(attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId is not Guid ownerId || ownerId == Guid.Empty)
            return false;

        var session = await captureSessions.FindOwnedAsync(captureSessionId, ownerId, attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null || session.SessionId != captureSessionId
            || session.OwnerHobbiesModuleProfileId != ownerId || session.AttractionId != attractionId
            || session.CompletedAtUtc is null)
        {
            return false;
        }

        var attraction = await attractions.FindByIdAsync(attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (attraction is null || attraction.AttractionId != attractionId
            || !MapContributionEvidenceValidator.Validate(attraction, session, ownerId, reportedLocation).PassesChecks)
        {
            return false;
        }

        var photoResult = await photoEvidence.VerifyAsync(
            ownerId, attractionId, captureSessionId, privatePhotoStorageObjectKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return photoResult == MapContributionPhotoVerificationResult.Verified;
    }
}
