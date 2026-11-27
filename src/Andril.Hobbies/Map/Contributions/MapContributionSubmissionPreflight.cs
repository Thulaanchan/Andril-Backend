using System.Runtime.CompilerServices;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;

[assembly: InternalsVisibleTo("Andril.UnitTests")]
[assembly: InternalsVisibleTo("Andril.Persistence")]
[assembly: InternalsVisibleTo("Andril.IntegrationTests")]

namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Checks submission prerequisites without persisting a contribution or changing a capture session.
/// </summary>
internal sealed class MapContributionSubmissionPreflight(
    IMapCaptureSessionAccessReader accessReader,
    IMapContributionCaptureSessionRepository captureSessions,
    IMapAttractionRepository attractions,
    IMapContributionPhotoEvidenceReader photoEvidence,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly object PreparationAuthority = new();
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
        => await PrepareAsync(attractionId, captureSessionId, reportedLocation,
            privatePhotoStorageObjectKey, "Preflight validation", cancellationToken) is not null;

    internal async Task<PreparedSubmission?> PrepareAsync(
        Guid attractionId,
        Guid captureSessionId,
        MapContributionLocationEvidence? reportedLocation,
        string? privatePhotoStorageObjectKey,
        string? description,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (attractionId == Guid.Empty || captureSessionId == Guid.Empty
            || reportedLocation is null || string.IsNullOrWhiteSpace(privatePhotoStorageObjectKey)
            || string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // This port resolves the current owner and checks attraction eligibility together.
        var profileId = await accessReader.ResolveAuthorizedProfileIdAsync(attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (profileId is not Guid ownerId || ownerId == Guid.Empty)
            return null;

        var session = await captureSessions.FindOwnedAsync(captureSessionId, ownerId, attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (session is null || session.SessionId != captureSessionId
            || session.OwnerHobbiesModuleProfileId != ownerId || session.AttractionId != attractionId
            || session.CompletedAtUtc is null)
        {
            return null;
        }

        var attraction = await attractions.FindByIdAsync(attractionId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (attraction is null || attraction.AttractionId != attractionId)
            return null;

        var location = MapContributionEvidenceValidator.Validate(attraction, session, ownerId, reportedLocation);
        if (!location.PassesChecks || location.Evidence is not { } evidence)
            return null;

        if (!MapContributionSubmissionWindow.Allows(session, _timeProvider.GetUtcNow()))
            return null;

        var photoResult = await photoEvidence.VerifyAsync(
            ownerId, attractionId, captureSessionId, privatePhotoStorageObjectKey, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (photoResult != MapContributionPhotoVerificationResult.Verified)
            return null;

        var submittedAt = _timeProvider.GetUtcNow();
        if (!MapContributionSubmissionWindow.Allows(session, submittedAt))
            return null;

        try
        {
            return new PreparedSubmission(MapContribution.Create(Guid.NewGuid(), ownerId,
                attractionId, captureSessionId, privatePhotoStorageObjectKey,
                description, evidence, submittedAt), PreparationAuthority);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal sealed class PreparedSubmission
    {
        internal PreparedSubmission(MapContribution contribution, object authority)
        {
            if (!ReferenceEquals(authority, PreparationAuthority))
                throw new InvalidOperationException("Only trusted preflight may prepare a submission.");
            Contribution = contribution;
        }

        internal MapContribution Contribution { get; }
    }
}
