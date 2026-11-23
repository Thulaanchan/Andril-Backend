using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionModerationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PendingDecisionRecordsImmutableEvidenceAndIncrementsRevision(int actionValue)
    {
        var action = (MapContributionReviewAction)actionValue;
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 4);
        var pending = domain.Snapshot;
        var request = Request(contribution, action, revision: 4);

        var accepted = domain.Decide(request, Now.ToOffset(TimeSpan.FromHours(5.5)));

        Assert.Equal(5, accepted.Revision);
        Assert.False(accepted.IsPendingReview);
        Assert.Equal(request, accepted.Decision!.Request);
        Assert.Equal(Now, accepted.Decision.DecidedAtUtc);
        Assert.Equal(TimeSpan.Zero, accepted.Decision.DecidedAtUtc.Offset);
        Assert.Equal(contribution.ContributionId, accepted.ContributionId);
        Assert.Equal(contribution.OwnerHobbiesModuleProfileId, accepted.OwnerHobbiesModuleProfileId);
        Assert.Equal(contribution.AttractionId, accepted.AttractionId);
        Assert.Equal(contribution.CaptureSessionId, accepted.CaptureSessionId);
        Assert.True(pending.IsPendingReview);
        Assert.Equal(4, pending.Revision);
        Assert.Null(pending.Decision);
        Assert.Same(accepted, domain.Snapshot);
        // This offline decision never mutates the persisted contribution or issues side effects.
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAcceptedRetryReturnsOriginalEvidenceDespiteOldRevisionAndLaterClock(int actionValue)
    {
        var action = (MapContributionReviewAction)actionValue;
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        var request = Request(contribution, action);
        var first = domain.Decide(request, Now);
        var equivalent = Request(contribution, action, decisionId: request.DecisionId);

        Assert.Same(first, domain.Decide(equivalent, Now.AddMinutes(1)));
        Assert.Equal(1, domain.Snapshot.Revision);
        Assert.Equal(Now, domain.Snapshot.Decision!.DecidedAtUtc);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("reviewer")]
    [InlineData("revision")]
    [InlineData("contribution")]
    public void SameDecisionIdentityWithChangedPayloadFailsWithoutMutation(string changed)
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        var request = Request(contribution);
        var accepted = domain.Decide(request, Now);
        var conflicting = new MapContributionReviewRequest(
            request.DecisionId,
            changed == "contribution" ? Guid.NewGuid() : contribution.ContributionId,
            changed == "action" ? MapContributionReviewAction.Reject : request.Action,
            changed == "revision" ? 1 : 0,
            changed == "reviewer" ? "other-trusted-subject" : request.ReviewerSubject);

        Assert.Throws<InvalidOperationException>(() => domain.Decide(conflicting, Now));
        Assert.Same(accepted, domain.Snapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void NewStaleOrFutureRevisionCannotChangePendingState(long expected)
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 1);
        var pending = domain.Snapshot;
        Assert.Throws<InvalidOperationException>(() => domain.Decide(Request(contribution, revision: expected), Now));
        Assert.Same(pending, domain.Snapshot);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(2, 1)]
    public void TerminalDecisionCannotBeReopenedByANewDecisionIdentity(
        int first, int next)
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        var accepted = domain.Decide(Request(contribution, (MapContributionReviewAction)first), Now);
        Assert.Throws<InvalidOperationException>(() => domain.Decide(Request(contribution, (MapContributionReviewAction)next, revision: 1), Now));
        Assert.Same(accepted, domain.Snapshot);
    }

    [Fact]
    public void ForeignContributionCannotSupplyIdentityOrChangeState()
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        var pending = domain.Snapshot;
        Assert.Throws<InvalidOperationException>(() => domain.Decide(Request(Contribution()), Now));
        Assert.Same(pending, domain.Snapshot);
        Assert.Equal(contribution.OwnerHobbiesModuleProfileId, pending.OwnerHobbiesModuleProfileId);
        Assert.Equal(contribution.AttractionId, pending.AttractionId);
    }

    [Fact]
    public async Task CompetingCallsOnOneInstanceAcceptExactlyOneDecision()
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        Task<bool> Compete(MapContributionReviewAction action) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            try { domain.Decide(Request(contribution, action), Now); return true; }
            catch (InvalidOperationException) { return false; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var approve = Compete(MapContributionReviewAction.Approve);
        var reject = Compete(MapContributionReviewAction.Reject);
        var bothReady = ready.Wait(TimeSpan.FromSeconds(10));
        start.Set();
        var results = await Task.WhenAll(approve, reject);
        Assert.True(bothReady);
        Assert.Single(results, accepted => accepted);
        Assert.Equal(1, domain.Snapshot.Revision);
        Assert.NotNull(domain.Snapshot.Decision);
    }

    [Fact]
    public void InvalidInputsAndRevisionOverflowLeaveSnapshotUnchanged()
    {
        var contribution = Contribution();
        Assert.Throws<ArgumentNullException>(() => MapContributionModeration.CreatePending(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapContributionModeration.CreatePending(contribution, -1));
        var domain = MapContributionModeration.CreatePending(contribution, long.MaxValue);
        var pending = domain.Snapshot;
        Assert.Throws<ArgumentNullException>(() => domain.Decide(null!, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => domain.Decide(Request(contribution, revision: long.MaxValue), default));
        Assert.Throws<OverflowException>(() => domain.Decide(Request(contribution, revision: long.MaxValue), Now));
        Assert.Same(pending, domain.Snapshot);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("contribution")]
    [InlineData("reviewer-null")]
    [InlineData("reviewer-empty")]
    [InlineData("reviewer-blank")]
    [InlineData("revision")]
    [InlineData("action-zero")]
    [InlineData("action-unknown")]
    public void InvalidDecisionRequestsAreRejected(string invalid)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MapContributionReviewRequest(
            invalid == "decision" ? Guid.Empty : Guid.NewGuid(),
            invalid == "contribution" ? Guid.Empty : Guid.NewGuid(),
            invalid == "action-zero" ? (MapContributionReviewAction)0 :
                invalid == "action-unknown" ? (MapContributionReviewAction)999 : MapContributionReviewAction.Approve,
            invalid == "revision" ? -1 : 0,
            invalid == "reviewer-null" ? null! : invalid == "reviewer-empty" ? "" :
                invalid == "reviewer-blank" ? " \t" : "trusted-subject"));
    }

    [Fact]
    public void OfflineEvidenceContainsNoPhotoLocationOrRewardAndHasNoMutableProperties()
    {
        var contribution = Contribution();
        var domain = MapContributionModeration.CreatePending(contribution, 0);
        var snapshot = domain.Decide(Request(contribution), Now);
        foreach (var type in new[] { snapshot.GetType(), snapshot.Decision!.GetType(), snapshot.Decision.Request.GetType() })
        {
            var properties = type.GetProperties(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Assert.All(properties, property => Assert.Null(property.SetMethod));
            Assert.DoesNotContain(properties, property => property.PropertyType == typeof(MapContribution));
            Assert.DoesNotContain(properties, property => property.Name.Contains("Photo") ||
                property.Name.Contains("Location") || property.Name.Contains("Points") || property.Name.Contains("Publish"));
        }
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
        Assert.Single(Enum.GetValues<MapContributionModerationStatus>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RestorePendingPreservesScopeAndAllowsNormalFirstDecision(long revision)
    {
        var contribution = Contribution();
        var evidence = MapContributionModeration.CreatePending(contribution, revision).Snapshot;
        var restored = MapContributionModeration.Restore(contribution, evidence, acceptedAction: null);
        Assert.True(restored.Snapshot.IsPendingReview);
        Assert.Null(restored.Snapshot.Decision);
        Assert.Equal(revision, restored.Snapshot.Revision);
        Assert.Equal(evidence.OwnerHobbiesModuleProfileId, restored.Snapshot.OwnerHobbiesModuleProfileId);
        Assert.Equal(evidence.AttractionId, restored.Snapshot.AttractionId);
        Assert.Equal(evidence.CaptureSessionId, restored.Snapshot.CaptureSessionId);
        var accepted = restored.Decide(Request(contribution, revision: revision), Now);
        Assert.Equal(revision + 1, accepted.Revision);
        Assert.True(evidence.IsPendingReview);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RestoreAcceptedPreservesOriginalReceiptAndExactRetry(int actionValue)
    {
        var contribution = Contribution();
        var action = (MapContributionReviewAction)actionValue;
        var original = MapContributionModeration.CreatePending(contribution, 4)
            .Decide(Request(contribution, action, revision: 4), Now);
        // Reconstruct all receipt objects, rather than retaining the original domain instance.
        var saved = original.Decision!;
        var request = new MapContributionReviewRequest(saved.Request.DecisionId, saved.Request.ContributionId,
            saved.Request.Action, saved.Request.ExpectedRevision, saved.Request.ReviewerSubject);
        var evidence = Snapshot(contribution, 5, new(request, saved.DecidedAtUtc.ToOffset(TimeSpan.FromHours(5.5))));
        var restored = MapContributionModeration.Restore(contribution, evidence, action);
        var snapshot = restored.Snapshot;
        Assert.False(snapshot.IsPendingReview);
        Assert.Equal(5, snapshot.Revision);
        Assert.Equal(saved.Request, snapshot.Decision!.Request);
        Assert.Equal(saved.DecidedAtUtc, snapshot.Decision.DecidedAtUtc);
        Assert.Equal(TimeSpan.Zero, snapshot.Decision.DecidedAtUtc.Offset);
        Assert.Same(snapshot, restored.Decide(request, Now.AddHours(1)));
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
    }

    [Theory]
    [InlineData(1, "action")]
    [InlineData(1, "reviewer")]
    [InlineData(1, "contribution")]
    [InlineData(1, "revision")]
    [InlineData(1, "new-key")]
    [InlineData(2, "action")]
    [InlineData(2, "reviewer")]
    [InlineData(2, "contribution")]
    [InlineData(2, "revision")]
    [InlineData(2, "new-key")]
    public void RestoredTerminalStateRejectsChangedRetriesWithoutMutation(int actionValue, string changed)
    {
        var contribution = Contribution();
        var action = (MapContributionReviewAction)actionValue;
        var request = Request(contribution, action);
        var restored = MapContributionModeration.Restore(contribution,
            Snapshot(contribution, 1, new(request, Now)), action);
        var accepted = restored.Snapshot;
        var conflict = new MapContributionReviewRequest(
            changed == "new-key" ? Guid.NewGuid() : request.DecisionId,
            changed == "contribution" ? Guid.NewGuid() : request.ContributionId,
            changed == "action" ? (action == MapContributionReviewAction.Approve ? MapContributionReviewAction.Reject : MapContributionReviewAction.Approve) : action,
            changed is "revision" or "new-key" ? 1 : 0,
            changed == "reviewer" ? "other-subject" : request.ReviewerSubject);
        Assert.Throws<InvalidOperationException>(() => restored.Decide(conflict, Now.AddHours(1)));
        Assert.Same(accepted, restored.Snapshot);
        Assert.Same(accepted, restored.Decide(request, Now.AddHours(1)));
    }

    [Theory]
    [InlineData("contribution", false)]
    [InlineData("owner", false)]
    [InlineData("attraction", false)]
    [InlineData("session", false)]
    [InlineData("contribution", true)]
    [InlineData("owner", true)]
    [InlineData("attraction", true)]
    [InlineData("session", true)]
    public void RestoreRejectsMismatchedIdentityEvidence(string changed, bool terminal)
    {
        var contribution = Contribution();
        var evidence = new MapContributionModerationSnapshot(
            changed == "contribution" ? Guid.NewGuid() : contribution.ContributionId,
            changed == "owner" ? Guid.NewGuid() : contribution.OwnerHobbiesModuleProfileId,
            changed == "attraction" ? Guid.NewGuid() : contribution.AttractionId,
            changed == "session" ? Guid.NewGuid() : contribution.CaptureSessionId,
            terminal ? 1 : 0, terminal ? new(Request(contribution), Now) : null);
        Assert.Throws<InvalidOperationException>(() => MapContributionModeration.Restore(contribution,
            evidence, terminal ? MapContributionReviewAction.Approve : null));
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void RestoreRejectsMissingOrContradictoryDecisionEvidence(int claimedAction, int receiptAction)
    {
        var contribution = Contribution();
        var evidence = Snapshot(contribution, receiptAction == 0 ? 0 : 1,
            receiptAction == 0 ? null : new(Request(contribution, (MapContributionReviewAction)receiptAction), Now));
        Assert.Throws<InvalidOperationException>(() => MapContributionModeration.Restore(contribution,
            evidence, claimedAction == 0 ? null : (MapContributionReviewAction)claimedAction));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void RestoreRejectsInvalidResultRevision(long revision)
    {
        var contribution = Contribution();
        var evidence = Snapshot(contribution, revision, new(Request(contribution, revision: 4), Now));
        Assert.ThrowsAny<ArgumentException>(() => MapContributionModeration.Restore(contribution,
            evidence, MapContributionReviewAction.Approve));
    }

    [Fact]
    public void RestoreRejectsIncompleteReceiptAndOverflowWithoutPublishingPartialState()
    {
        var contribution = Contribution();
        var pending = MapContributionModeration.CreatePending(contribution, 0);
        var original = pending.Snapshot;
        Assert.Throws<ArgumentNullException>(() => MapContributionModeration.Restore(null!, original, null));
        Assert.Throws<ArgumentNullException>(() => MapContributionModeration.Restore(contribution, null!, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapContributionModeration.Restore(contribution, original, (MapContributionReviewAction)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapContributionModeration.Restore(contribution, Snapshot(contribution, -1), null));
        Assert.Throws<ArgumentNullException>(() => MapContributionModeration.Restore(contribution,
            Snapshot(contribution, 1, new(null!, Now)), MapContributionReviewAction.Approve));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapContributionModeration.Restore(contribution,
            Snapshot(contribution, 1, new(Request(contribution), default)), MapContributionReviewAction.Approve));
        Assert.Throws<InvalidOperationException>(() => MapContributionModeration.Restore(contribution,
            Snapshot(contribution, 1, new(Request(Contribution()), Now)), MapContributionReviewAction.Approve));
        Assert.Throws<OverflowException>(() => MapContributionModeration.Restore(contribution,
            Snapshot(contribution, long.MaxValue, new(Request(contribution, revision: long.MaxValue), Now)), MapContributionReviewAction.Approve));
        Assert.Same(original, pending.Snapshot);
        Assert.True(pending.Snapshot.IsPendingReview);
    }

    [Fact]
    public void RestoredSnapshotsRemainImmutableAndHaveNoImplicitSideEffects()
    {
        var contribution = Contribution();
        var evidence = Snapshot(contribution, 1, new(Request(contribution), Now));
        var restored = MapContributionModeration.Restore(contribution, evidence, MapContributionReviewAction.Approve);
        Assert.NotSame(evidence, restored.Snapshot);
        foreach (var type in new[] { restored.Snapshot.GetType(), restored.Snapshot.Decision!.GetType(), restored.Snapshot.Decision.Request.GetType() })
            Assert.All(type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
        Assert.Single(Enum.GetValues<MapContributionModerationStatus>());
        var maxPending = MapContributionModeration.Restore(contribution, Snapshot(contribution, long.MaxValue), null);
        Assert.Throws<OverflowException>(() => maxPending.Decide(Request(contribution, revision: long.MaxValue), Now));
        Assert.True(maxPending.Snapshot.IsPendingReview);
    }

    private static MapContributionModerationSnapshot Snapshot(MapContribution contribution, long revision,
        MapContributionReviewDecision? decision = null) => new(contribution.ContributionId,
            contribution.OwnerHobbiesModuleProfileId, contribution.AttractionId, contribution.CaptureSessionId, revision, decision);

    private static MapContributionReviewRequest Request(MapContribution contribution,
        MapContributionReviewAction action = MapContributionReviewAction.Approve,
        long revision = 0, Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), contribution.ContributionId, action, revision, "trusted-subject");

    private static MapContribution Contribution() => MapContribution.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "private/map/photo.jpg", "Synthetic",
        new MapContributionLocationEvidence(7, 80, 7, 80, 5, Now.AddSeconds(-30), Now.AddSeconds(-5)), Now);
}
