using Andril.Hobbies.Map.LocationValidation;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>Internal offline orchestration; deliberately absent from DI and HTTP routes.</summary>
/// <remarks>
/// Preflight resolves the trusted Hobbies owner; no caller-supplied owner/module is accepted.
/// This adds no authorization freshness guarantee, media adapter, publication or reward path.
/// The writer owns transaction, duplicate reconciliation and commit-ambiguity semantics.
/// </remarks>
internal sealed class MapContributionSubmissionService
{
    private readonly MapContributionSubmissionPreflight _preflight;
    private readonly IMapContributionSubmissionWriter _writer;

    internal MapContributionSubmissionService(MapContributionSubmissionPreflight preflight,
        IMapContributionSubmissionWriter writer)
    {
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    internal async Task<MapSubmissionResult> SubmitAsync(Guid attractionId, Guid captureSessionId,
        MapContributionLocationEvidence? reportedLocation, string? privatePhotoStorageObjectKey,
        string? description, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MapContributionSubmissionPreflight.PreparedSubmission? prepared;
        try
        {
            prepared = await _preflight.PrepareAsync(attractionId, captureSessionId, reportedLocation,
                privatePhotoStorageObjectKey, description, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(MapSubmissionOutcome.InfrastructureFailure);
        }

        if (prepared is null)
            return new(MapSubmissionOutcome.Denied);

        try
        {
            var result = await _writer.SubmitAsync(prepared, cancellationToken);
            // Never check cancellation after a writer result: commit may already have succeeded.
            // Preserve OutcomeUnknown; never retry, re-authorize or start another write here.
            return result switch
            {
                { Outcome: MapSubmissionOutcome.Created or MapSubmissionOutcome.ExistingAuthorized,
                    ContributionId: Guid id } when id != Guid.Empty => result,
                { Outcome: MapSubmissionOutcome.Denied or MapSubmissionOutcome.InfrastructureFailure
                    or MapSubmissionOutcome.OutcomeUnknown } => new(result.Outcome),
                _ => new(MapSubmissionOutcome.OutcomeUnknown)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // An unexpected writer failure does not establish rollback or non-commit.
            return new(MapSubmissionOutcome.OutcomeUnknown);
        }
    }
}
