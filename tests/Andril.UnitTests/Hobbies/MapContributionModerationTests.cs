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

    private static MapContributionReviewRequest Request(MapContribution contribution,
        MapContributionReviewAction action = MapContributionReviewAction.Approve,
        long revision = 0, Guid? decisionId = null) =>
        new(decisionId ?? Guid.NewGuid(), contribution.ContributionId, action, revision, "trusted-subject");

    private static MapContribution Contribution() => MapContribution.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "private/map/photo.jpg", "Synthetic",
        new MapContributionLocationEvidence(7, 80, 7, 80, 5, Now.AddSeconds(-30), Now.AddSeconds(-5)), Now);
}
