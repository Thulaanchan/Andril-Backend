namespace Andril.Hobbies.Map.Contributions;

// Review actions are not additions to the persisted PendingReview-only status enum.
internal enum MapContributionReviewAction { Approve = 1, Reject = 2 }

/// <summary>Offline request identity and payload; reviewer identity must come from a trusted caller.</summary>
/// <remarks>No permission, role or resource authorization is inferred from this data.</remarks>
internal sealed record MapContributionReviewRequest
{
    internal MapContributionReviewRequest(Guid decisionId, Guid contributionId,
        MapContributionReviewAction action, long expectedRevision, string reviewerSubject)
    {
        if (decisionId == Guid.Empty) throw new ArgumentException("A decision ID is required.", nameof(decisionId));
        if (contributionId == Guid.Empty) throw new ArgumentException("A contribution ID is required.", nameof(contributionId));
        if (action is not (MapContributionReviewAction.Approve or MapContributionReviewAction.Reject))
            throw new ArgumentOutOfRangeException(nameof(action));
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSubject);
        DecisionId = decisionId;
        ContributionId = contributionId;
        Action = action;
        ExpectedRevision = expectedRevision;
        ReviewerSubject = reviewerSubject;
    }

    internal Guid DecisionId { get; }
    internal Guid ContributionId { get; }
    internal MapContributionReviewAction Action { get; }
    internal long ExpectedRevision { get; }
    internal string ReviewerSubject { get; }
}

internal sealed class MapContributionReviewDecision
{
    internal MapContributionReviewDecision(MapContributionReviewRequest request, DateTimeOffset decidedAtUtc)
    {
        Request = request;
        DecidedAtUtc = decidedAtUtc.ToUniversalTime();
    }

    internal MapContributionReviewRequest Request { get; }
    internal DateTimeOffset DecidedAtUtc { get; }
}

/// <summary>Immutable offline state. A decision records approval/rejection without changing persistence.</summary>
internal sealed class MapContributionModerationSnapshot
{
    internal MapContributionModerationSnapshot(Guid contributionId, Guid ownerHobbiesModuleProfileId,
        Guid attractionId, Guid captureSessionId, long revision, MapContributionReviewDecision? decision)
    {
        ContributionId = contributionId;
        OwnerHobbiesModuleProfileId = ownerHobbiesModuleProfileId;
        AttractionId = attractionId;
        CaptureSessionId = captureSessionId;
        Revision = revision;
        Decision = decision;
    }

    internal Guid ContributionId { get; }
    internal Guid OwnerHobbiesModuleProfileId { get; }
    internal Guid AttractionId { get; }
    internal Guid CaptureSessionId { get; }
    internal long Revision { get; }
    internal MapContributionReviewDecision? Decision { get; }
    internal bool IsPendingReview => Decision is null;
}

/// <summary>Internal, unmapped offline moderation domain; not registered for production use.</summary>
/// <remarks>
/// The trusted application must separately authorize each evaluation, including retries.
/// Serialization is instance-local. Replay evidence survives reconstruction only when a trusted
/// caller supplies the original complete snapshot; this class provides no durable storage.
/// No media, publication, audit persistence
/// or reward operations occur here. The existing contribution and its EF status remain unchanged.
/// </remarks>
internal sealed class MapContributionModeration
{
    private readonly object _gate = new();
    private MapContributionModerationSnapshot _snapshot;

    private MapContributionModeration(MapContributionModerationSnapshot snapshot) => _snapshot = snapshot;

    internal static MapContributionModeration CreatePending(MapContribution contribution, long revision)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (contribution.ModerationStatus != MapContributionModerationStatus.PendingReview)
            throw new InvalidOperationException("Only a pending contribution can begin offline review.");
        return new(new(contribution.ContributionId, contribution.OwnerHobbiesModuleProfileId,
            contribution.AttractionId, contribution.CaptureSessionId, revision, null));
    }

    /// <summary>
    /// Restores consistent offline evidence supplied by a trusted authoritative caller.
    /// A null acceptedAction explicitly means PendingReview; otherwise the original receipt
    /// must establish that action and the preceding revision. Never bind client input here:
    /// consistency checks establish neither authorization nor proof of database persistence.
    /// The contribution supplies identity only; its persisted PendingReview status is unchanged.
    /// </summary>
    internal static MapContributionModeration Restore(MapContribution contribution,
        MapContributionModerationSnapshot snapshot, MapContributionReviewAction? acceptedAction)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Revision);
        if (acceptedAction is not (null or MapContributionReviewAction.Approve or MapContributionReviewAction.Reject))
            throw new ArgumentOutOfRangeException(nameof(acceptedAction));
        if (snapshot.ContributionId != contribution.ContributionId
            || snapshot.OwnerHobbiesModuleProfileId != contribution.OwnerHobbiesModuleProfileId
            || snapshot.AttractionId != contribution.AttractionId
            || snapshot.CaptureSessionId != contribution.CaptureSessionId)
            throw new InvalidOperationException("The moderation evidence does not match this contribution scope.");

        MapContributionReviewDecision? decision = null;
        if (acceptedAction is null)
        {
            if (snapshot.Decision is not null)
                throw new InvalidOperationException("Pending review cannot contain accepted decision evidence.");
        }
        else
        {
            var receipt = snapshot.Decision
                ?? throw new InvalidOperationException("Accepted review requires complete decision evidence.");
            ArgumentNullException.ThrowIfNull(receipt.Request);
            var request = receipt.Request;
            if (request.ContributionId != contribution.ContributionId || request.Action != acceptedAction)
                throw new InvalidOperationException("The accepted decision does not match the review state.");
            if (receipt.DecidedAtUtc == default)
                throw new ArgumentOutOfRangeException(nameof(snapshot), "An original acceptance timestamp is required.");
            if (snapshot.Revision != checked(request.ExpectedRevision + 1))
                throw new ArgumentException("The accepted revision must follow the original request revision.", nameof(snapshot));
            decision = new(request, receipt.DecidedAtUtc);
        }

        return new(new(contribution.ContributionId, contribution.OwnerHobbiesModuleProfileId,
            contribution.AttractionId, contribution.CaptureSessionId, snapshot.Revision, decision));
    }

    internal MapContributionModerationSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// Applies one terminal decision. Exact accepted retries return the original snapshot;
    /// changed payloads, other terminal decisions and stale new requests fail without mutation.
    /// The server timestamp is evidence of first acceptance, not part of retry payload equality.
    /// </summary>
    internal MapContributionModerationSnapshot Decide(MapContributionReviewRequest request, DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (decidedAtUtc == default) throw new ArgumentOutOfRangeException(nameof(decidedAtUtc));
        lock (_gate)
        {
            var current = _snapshot;
            if (request.ContributionId != current.ContributionId)
                throw new InvalidOperationException("The decision does not match this contribution.");
            if (current.Decision is { } accepted)
            {
                if (accepted.Request == request) return current;
                throw new InvalidOperationException("The decision conflicts with the accepted review.");
            }
            if (request.ExpectedRevision != current.Revision)
                throw new InvalidOperationException("The moderation revision does not match.");
            var next = new MapContributionModerationSnapshot(current.ContributionId,
                current.OwnerHobbiesModuleProfileId, current.AttractionId, current.CaptureSessionId,
                checked(current.Revision + 1), new(request, decidedAtUtc));
            _snapshot = next;
            return next;
        }
    }
}
