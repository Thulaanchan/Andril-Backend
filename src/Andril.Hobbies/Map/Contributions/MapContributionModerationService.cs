using Andril.Shared.Authentication;

namespace Andril.Hobbies.Map.Contributions;

internal readonly record struct MapModerationScope(Guid AttractionId, Guid ContributionId);

internal interface IMapModerationAuthorization
{
    Task<bool> CanReviewAsync(Guid reviewerSubject, MapModerationScope scope, CancellationToken cancellationToken);
}

internal sealed class DenyAllMapModerationAuthorization : IMapModerationAuthorization
{
    public Task<bool> CanReviewAsync(Guid reviewerSubject, MapModerationScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

internal sealed record MapModerationItem(
    MapModerationScope Scope, Guid OwnerProfileId, Guid CaptureSessionId, long Revision, bool IsPending);

internal enum MapModerationOutcome { Denied, Ineligible, Applied, Replay, Conflict, OutcomeUnknown }

internal interface IMapModerationRepository
{
    Task<MapModerationItem?> FindAsync(MapModerationScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Reauthorize the current reviewer under the write transaction before exposing replay evidence.
    /// Match the original request before checking current state on retries. For a new acceptance,
    /// lock and recheck scope, revision and immutable approval evidence, then commit state, receipt
    /// and audit together using the existing MapContributionModeration transition.
    /// </summary>
    Task<MapModerationOutcome> DecideAsync(MapModerationScope scope, MapContributionReviewRequest request,
        VerifiedMapMedia? approvalEvidence, CancellationToken cancellationToken);
}

internal interface IMapModerationEvidenceReader
{
    /// <summary>Return independently inspected evidence for the exact immutable contribution binding.</summary>
    Task<VerifiedMapMedia?> InspectAsync(MapModerationItem item, CancellationToken cancellationToken);
}

// This boundary has no startup registration or externally callable endpoint.
internal sealed class MapContributionModerationService(
    ICurrentUser actor, IMapModerationAuthorization authorization,
    IMapModerationRepository repository, IMapModerationEvidenceReader evidence)
{
    internal async Task<MapModerationItem?> FindPendingAsync(
        MapModerationScope scope, CancellationToken cancellationToken = default)
    {
        var reviewer = await AuthorizeAsync(scope, cancellationToken);
        if (reviewer is null)
            return null;

        var item = await repository.FindAsync(scope, cancellationToken);
        return item is { IsPending: true } && IsScoped(item, scope)
            && await AuthorizeAsync(scope, cancellationToken) == reviewer ? item : null;
    }

    internal async Task<VerifiedMapMedia?> InspectPendingEvidenceAsync(
        MapModerationScope scope, CancellationToken cancellationToken = default)
    {
        var reviewer = await AuthorizeAsync(scope, cancellationToken);
        if (reviewer is null)
            return null;

        var item = await repository.FindAsync(scope, cancellationToken);
        if (item is not { IsPending: true } || !IsScoped(item, scope)
            || await AuthorizeAsync(scope, cancellationToken) != reviewer)
            return null;

        var verified = await evidence.InspectAsync(item, cancellationToken);
        return await AuthorizeAsync(scope, cancellationToken) == reviewer ? verified : null;
    }

    internal async Task<MapModerationOutcome> DecideAsync(
        MapModerationScope scope, Guid decisionId, MapContributionReviewAction action, long revision,
        CancellationToken cancellationToken = default)
    {
        if (decisionId == Guid.Empty || revision < 0
            || action is not (MapContributionReviewAction.Approve or MapContributionReviewAction.Reject))
            return MapModerationOutcome.Denied;
        var reviewer = await AuthorizeAsync(scope, cancellationToken);
        if (reviewer is not Guid subject)
            return MapModerationOutcome.Denied;

        var item = await repository.FindAsync(scope, cancellationToken);
        if (item is null || !IsScoped(item, scope)
            || await AuthorizeAsync(scope, cancellationToken) != subject)
            return MapModerationOutcome.Denied;

        VerifiedMapMedia? verified = null;
        if (item.IsPending && item.Revision == revision && action == MapContributionReviewAction.Approve)
        {
            verified = await evidence.InspectAsync(item, cancellationToken);
            if (await AuthorizeAsync(scope, cancellationToken) != subject)
                return MapModerationOutcome.Denied;
            if (verified is null)
                return MapModerationOutcome.Ineligible;
        }

        if (await AuthorizeAsync(scope, cancellationToken) != subject)
            return MapModerationOutcome.Denied;

        return await repository.DecideAsync(scope,
            new(decisionId, scope.ContributionId, action, revision, subject.ToString("D")),
            verified, cancellationToken);
    }

    private async Task<Guid?> AuthorizeAsync(MapModerationScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope.AttractionId == Guid.Empty || scope.ContributionId == Guid.Empty
            || !actor.IsAuthenticated || actor.UserId is not Guid subject || subject == Guid.Empty)
            return null;

        var permitted = await authorization.CanReviewAsync(subject, scope, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return permitted && actor.IsAuthenticated && actor.UserId == subject ? subject : null;
    }

    private static bool IsScoped(MapModerationItem item, MapModerationScope scope) =>
        item.Scope == scope && item.OwnerProfileId != Guid.Empty && item.CaptureSessionId != Guid.Empty
            && item.Revision >= 0;
}
