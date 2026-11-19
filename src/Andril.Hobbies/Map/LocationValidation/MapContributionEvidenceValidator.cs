using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;

namespace Andril.Hobbies.Map.LocationValidation;

public enum MapContributionEvidenceFailure
{
    MissingRecords = 1,
    InconsistentRecords,
    OwnershipMismatch,
    AttractionMismatch,
    IncompleteCaptureSession,
    LocationPolicyFailed
}

/// <summary>
/// Reports backend validation results. Failures carry reason codes without record details.
/// </summary>
public sealed class MapContributionEvidenceResult
{
    internal MapContributionEvidenceResult(
        MapContributionEvidenceFailure? failure,
        MapContributionLocationFailure? locationFailure = null,
        MapContributionLocationEvidence? evidence = null)
    {
        Failure = failure;
        LocationFailure = locationFailure;
        Evidence = evidence;
    }

    public MapContributionEvidenceFailure? Failure { get; }
    public MapContributionLocationFailure? LocationFailure { get; }
    public MapContributionLocationEvidence? Evidence { get; }
    public bool PassesChecks => Failure is null && Evidence is not null;
}

public static class MapContributionEvidenceValidator
{
    /// <summary>
    /// Binds reported device observations to a canonical attraction and a completed server capture session.
    /// </summary>
    /// <remarks>
    /// A trusted backend caller must resolve the records and expected Hobbies profile ID
    /// through authorization. Keep the result and its evidence within the backend.
    /// Passing these checks does not establish GPS authenticity, camera freshness,
    /// private media ownership, submission eligibility, approval, or reward entitlement.
    /// </remarks>
    public static MapContributionEvidenceResult Validate(
        MapAttraction? attraction,
        MapContributionCaptureSession? captureSession,
        Guid expectedHobbiesModuleProfileId,
        MapContributionLocationEvidence? reportedLocation)
    {
        if (attraction is null || captureSession is null)
            return new(MapContributionEvidenceFailure.MissingRecords);
        if (expectedHobbiesModuleProfileId == Guid.Empty)
            return new(MapContributionEvidenceFailure.InconsistentRecords);
        if (captureSession.OwnerHobbiesModuleProfileId != expectedHobbiesModuleProfileId)
            return new(MapContributionEvidenceFailure.OwnershipMismatch);
        if (attraction.AttractionId == Guid.Empty || captureSession.SessionId == Guid.Empty
            || captureSession.AttractionId == Guid.Empty)
        {
            return new(MapContributionEvidenceFailure.InconsistentRecords);
        }
        if (captureSession.AttractionId != attraction.AttractionId)
            return new(MapContributionEvidenceFailure.AttractionMismatch);
        if (captureSession.CompletedAtUtc is not { } completedAt)
            return new(MapContributionEvidenceFailure.IncompleteCaptureSession);
        if (completedAt < captureSession.StartedAtUtc)
            return new(MapContributionEvidenceFailure.InconsistentRecords);
        if (reportedLocation is null)
            return new(MapContributionEvidenceFailure.LocationPolicyFailed, MapContributionLocationFailure.MissingEvidence);

        var evidence = new MapContributionLocationEvidence(
            reportedLocation.UserLatitude,
            reportedLocation.UserLongitude,
            attraction.Latitude,
            attraction.Longitude,
            reportedLocation.HorizontalAccuracyMetres,
            captureSession.StartedAtUtc,
            completedAt);

        var locationResult = MapContributionLocationPolicy.Evaluate(evidence);
        return locationResult.PassesChecks
            ? new(null, evidence: evidence)
            : new(MapContributionEvidenceFailure.LocationPolicyFailed, locationResult.Failure);
    }
}
