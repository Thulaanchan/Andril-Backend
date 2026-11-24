namespace Andril.Hobbies.Map.Contributions;

internal enum MapSubmissionOutcome
{
    Created,
    ExistingAuthorized,
    Denied,
    InfrastructureFailure,
    OutcomeUnknown
}

// No storage keys, coordinates or exception details leave this boundary.
internal sealed record MapSubmissionResult(MapSubmissionOutcome Outcome, Guid? ContributionId = null);

internal interface IMapContributionSubmissionWriter
{
    Task<MapSubmissionResult> SubmitAsync(
        MapContributionSubmissionPreflight.PreparedSubmission prepared,
        CancellationToken cancellationToken = default);
}
