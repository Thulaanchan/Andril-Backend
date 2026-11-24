using Andril.Hobbies.Interfaces;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>Internal offline, read-only reconciliation; no writer or automatic retry capability.</summary>
/// <remarks>
/// Uses the approved committed-reader boundary and retained original request comparison.
/// Outcomes describe this observation, not permission to submit again or publish/reward.
/// Production authorization freshness and public retry semantics remain separate contracts.
/// </remarks>
internal sealed class MapContributionSubmissionReconciliationService
{
    private readonly IMapContributionCommittedReader _reader;
    private readonly IGalleryActorContext _actors;

    internal MapContributionSubmissionReconciliationService(IMapContributionCommittedReader reader, IGalleryActorContext actors)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    }

    internal async Task<MapSubmissionResult> ReconcileAsync(MapSubmissionOriginalEvidence? original,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var actor = _actors.Current;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveHobbies(actor)) return new(MapSubmissionOutcome.Denied);
            if (original is null) return new(MapSubmissionOutcome.OutcomeUnknown);
            var owner = actor!.ModuleProfileId;
            if (original.OwnerHobbiesModuleProfileId != owner) return new(MapSubmissionOutcome.Denied);

            var committed = await _reader.FindOwnedByCaptureSessionAsync(original.CaptureSessionId,
                owner, original.AttractionId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var current = _actors.Current;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveHobbies(current) || current!.ModuleProfileId != owner)
                return new(MapSubmissionOutcome.Denied);
            if (committed is null) return new(MapSubmissionOutcome.OutcomeUnknown);
            if (!original.MatchesRequest(committed)) return new(MapSubmissionOutcome.Denied);
            if (committed.ContributionId == Guid.Empty || committed.ModerationStatus != MapContributionModerationStatus.PendingReview)
                return new(MapSubmissionOutcome.OutcomeUnknown);
            cancellationToken.ThrowIfCancellationRequested();
            return new(MapSubmissionOutcome.ExistingAuthorized, committed.ContributionId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A failed observation does not establish whether the original write committed.
            // No provider details, retained evidence or private identifiers leave this boundary.
            return new(MapSubmissionOutcome.OutcomeUnknown);
        }
    }

    private static bool IsActiveHobbies(GalleryActor? actor) => actor is
        { IsActive: true, Module: "Hobbies" } && actor.ModuleProfileId != Guid.Empty;
}
