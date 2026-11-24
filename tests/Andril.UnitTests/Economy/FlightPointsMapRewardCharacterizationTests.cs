using Andril.Economy.FlightPoints;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Economy;

// Synthetic dispatch only. This is NOT a production approval/reward transaction coordinator,
// trusted authorization provider, First Explorer selector, or durable event-idempotency design.
public sealed class FlightPointsMapRewardCharacterizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly FlightPointsEligibility TrustedSyntheticFacts = new(false, false, true, false, true, true);
    private static MapContribution Contribution() => MapContribution.Create(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), "private/synthetic/photo.jpg", "Synthetic contribution",
        new MapContributionLocationEvidence(7, 80, 7, 80, 5, Now.AddSeconds(-30), Now.AddSeconds(-5)), Now);
    private static MapContributionModerationSnapshot Snapshot(MapContribution contribution, MapContributionReviewAction? action)
    {
        var moderation = MapContributionModeration.CreatePending(contribution, 1);
        return action is null ? moderation.Snapshot : moderation.Decide(new(Guid.NewGuid(), contribution.ContributionId,
            action.Value, 1, "synthetic-authorized-reviewer"), Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task T01_T02_PendingOrRejectedOfflineDecisionNeverCallsLedger(int action)
    {
        var contribution = Contribution(); var ledger = new RecordingLedger();
        var snapshot = Snapshot(contribution, action == 0 ? null : MapContributionReviewAction.Reject);
        Assert.False(await new SyntheticDispatch(ledger).RunAsync(contribution, snapshot,
            contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        Assert.Empty(ledger.Attempts); Assert.Empty(ledger.Persisted);
    }

    [Fact]
    public async Task T03_ApprovedOfflineDecisionUsesExistingPolicyAndLedgerFactories()
    {
        var contribution = Contribution(); var snapshot = Snapshot(contribution, MapContributionReviewAction.Approve);
        var ledger = new RecordingLedger();
        Assert.True(await new SyntheticDispatch(ledger).RunAsync(contribution, snapshot,
            contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        var batch = Assert.Single(ledger.Attempts);
        Assert.Equal(2, batch.Length);
        Assert.Equal(FlightPointsRewardPolicy.FirstEligibleContributionPoints, batch[0].PointsDelta);
        Assert.Equal(FlightPointsRewardPolicy.FirstExplorerBonusPoints, batch[1].PointsDelta);
        Assert.All(batch, entry =>
        {
            Assert.Equal(contribution.OwnerHobbiesModuleProfileId, entry.OwnerHobbiesModuleProfileId);
            Assert.Equal(contribution.ContributionId, entry.ContributionId);
            Assert.Equal(contribution.AttractionId, entry.AttractionId);
            Assert.Equal(snapshot.Decision!.DecidedAtUtc, entry.CreatedAtUtc);
        });
    }

    [Theory]
    [InlineData("ineligible")]
    [InlineData("repeat")]
    public async Task T03_ApprovalAloneCannotOverrideOtherPolicyDenials(string denial)
    {
        var contribution = Contribution(); var ledger = new RecordingLedger();
        var facts = denial == "ineligible" ? TrustedSyntheticFacts with { IsEligible = false }
            : TrustedSyntheticFacts with { IsRepeatSubmission = true };
        Assert.False(await new SyntheticDispatch(ledger).RunAsync(contribution, Snapshot(contribution, MapContributionReviewAction.Approve),
            contribution.OwnerHobbiesModuleProfileId, "Hobbies", facts));
        Assert.Empty(ledger.Attempts);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("hobbies")]
    public async Task T06_T07_T15_SyntheticScopeMismatchCannotReachLedger(string mismatch)
    {
        var contribution = Contribution(); var ledger = new RecordingLedger();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new SyntheticDispatch(ledger).RunAsync(contribution,
            Snapshot(contribution, MapContributionReviewAction.Approve),
            mismatch == "owner" ? Guid.NewGuid() : contribution.OwnerHobbiesModuleProfileId,
            mismatch == "owner" ? "Hobbies" : mismatch, TrustedSyntheticFacts));
        Assert.Empty(ledger.Attempts); Assert.Empty(ledger.Persisted);
    }

    [Theory]
    [InlineData("contribution")]
    [InlineData("owner")]
    [InlineData("attraction")]
    [InlineData("session")]
    public async Task T08_T15_ExistingRestorationRejectsSubstitutedEvidenceBeforeDispatch(string mismatch)
    {
        var contribution = Contribution(); var original = Snapshot(contribution, MapContributionReviewAction.Approve);
        var substituted = new MapContributionModerationSnapshot(
            mismatch == "contribution" ? Guid.NewGuid() : original.ContributionId,
            mismatch == "owner" ? Guid.NewGuid() : original.OwnerHobbiesModuleProfileId,
            mismatch == "attraction" ? Guid.NewGuid() : original.AttractionId,
            mismatch == "session" ? Guid.NewGuid() : original.CaptureSessionId, original.Revision, original.Decision);
        var ledger = new RecordingLedger();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SyntheticDispatch(ledger).RunAsync(contribution,
            substituted, contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        Assert.Empty(ledger.Attempts); Assert.Empty(ledger.Persisted);
    }

    [Fact]
    public async Task T09_T10_LedgerFailureIsNotSuccessAndCannotUndoSeparateOfflineApproval()
    {
        var contribution = Contribution(); var accepted = Snapshot(contribution, MapContributionReviewAction.Approve);
        var ledger = new RecordingLedger { FailNext = true };
        var failure = await Assert.ThrowsAsync<IOException>(() => new SyntheticDispatch(ledger).RunAsync(contribution,
            accepted, contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        Assert.Same(ledger.Failure, failure); Assert.Empty(ledger.Persisted);
        Assert.False(accepted.IsPendingReview);
        Assert.Equal(MapContributionReviewAction.Approve, accepted.Decision!.Request.Action);
        Assert.Equal(2, accepted.Revision);
        // T10 production atomic coordination is BLOCKED: this immutable offline approval is not a ledger transaction.
    }

    [Fact]
    public async Task T11_KnownPreWriteFailureCanRetrySameSyntheticBatchWithoutFalseSuccess()
    {
        var contribution = Contribution(); var accepted = Snapshot(contribution, MapContributionReviewAction.Approve);
        var ledger = new RecordingLedger { FailNext = true }; var dispatch = new SyntheticDispatch(ledger);
        await Assert.ThrowsAsync<IOException>(() => dispatch.RunAsync(contribution, accepted,
            contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        Assert.Empty(ledger.Persisted);
        Assert.True(await dispatch.RunAsync(contribution, accepted, contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts));
        Assert.Equal(2, ledger.Attempts.Count); Assert.Equal(2, ledger.Persisted.Count);
        Assert.Equal(ledger.Attempts[0].Select(entry => entry.LedgerEntryId), ledger.Attempts[1].Select(entry => entry.LedgerEntryId));
        Assert.Equal(ledger.Attempts[0].Select(entry => entry.IdempotencyKey), ledger.Attempts[1].Select(entry => entry.IdempotencyKey));
        // Stable fixture IDs are not an approved durable event-key scope or lost-acknowledgement reconciliation.
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("during")]
    [InlineData("late")]
    public async Task T12_CancellationNeverProducesSuccessfulDispatch(string stage)
    {
        var contribution = Contribution(); var accepted = Snapshot(contribution, MapContributionReviewAction.Approve);
        using var cancellation = new CancellationTokenSource();
        var ledger = new RecordingLedger { BeforeAppend = stage == "during" ? cancellation.Cancel : null,
            AfterAppend = stage == "late" ? cancellation.Cancel : null };
        if (stage == "pre") cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SyntheticDispatch(ledger).RunAsync(
            contribution, accepted, contribution.OwnerHobbiesModuleProfileId, "Hobbies", TrustedSyntheticFacts, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(stage == "late" ? 2 : 0, ledger.Persisted.Count);
        if (stage == "pre") Assert.Empty(ledger.Attempts);
        // Late cancellation may follow persistence; it is not proof of rollback or permission to reissue rewards.
    }

    [Fact]
    public async Task UnresolvedExplorerEligibilityCannotBeTurnedIntoSuccessfulIssuance()
    {
        var contribution = Contribution(); var ledger = new RecordingLedger();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SyntheticDispatch(ledger).RunAsync(contribution,
            Snapshot(contribution, MapContributionReviewAction.Approve), contribution.OwnerHobbiesModuleProfileId, "Hobbies",
            TrustedSyntheticFacts with { IsFirstEligibleContribution = false }));
        Assert.Empty(ledger.Attempts);
    }

    private sealed class SyntheticDispatch(IFlightPointsLedgerRepository ledger)
    {
        private readonly Guid _firstId = Guid.NewGuid();
        private readonly Guid _explorerId = Guid.NewGuid();
        internal async Task<bool> RunAsync(MapContribution contribution, MapContributionModerationSnapshot evidence,
            Guid syntheticTrustedOwner, string syntheticTrustedModule, FlightPointsEligibility facts, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (syntheticTrustedOwner != contribution.OwnerHobbiesModuleProfileId || syntheticTrustedModule != "Hobbies")
                throw new UnauthorizedAccessException("Synthetic Hobbies scope denied.");
            var action = evidence.Decision?.Request.Action;
            var restored = MapContributionModeration.Restore(contribution, evidence, action).Snapshot;
            var calculation = FlightPointsRewardPolicy.Calculate(facts with
            {
                IsAdminApproved = action == MapContributionReviewAction.Approve,
                IsRejected = action == MapContributionReviewAction.Reject
            });
            if (calculation.RequiresEligibilityResolution) throw new InvalidOperationException("Eligibility resolution is required.");
            if (calculation.TotalPoints == 0) return false;
            var batch = new List<FlightPointsLedgerEntry>();
            if (calculation.FirstContributionPoints > 0) batch.Add(FlightPointsLedgerEntry.CreateGrant(_firstId,
                restored.OwnerHobbiesModuleProfileId, restored.ContributionId, restored.AttractionId,
                FlightPointsRewardType.FirstEligibleContribution, "synthetic-first-component", restored.Decision!.DecidedAtUtc));
            if (calculation.FirstExplorerPoints > 0) batch.Add(FlightPointsLedgerEntry.CreateGrant(_explorerId,
                restored.OwnerHobbiesModuleProfileId, restored.ContributionId, restored.AttractionId,
                FlightPointsRewardType.FirstExplorer, "synthetic-explorer-component", restored.Decision!.DecidedAtUtc));
            await ledger.AppendAsync(batch, token);
            token.ThrowIfCancellationRequested();
            return true;
        }
    }

    private sealed class RecordingLedger : IFlightPointsLedgerRepository
    {
        internal List<FlightPointsLedgerEntry[]> Attempts { get; } = [];
        internal List<FlightPointsLedgerEntry> Persisted { get; } = [];
        internal IOException Failure { get; } = new("Synthetic failure before any ledger write.");
        internal bool FailNext { get; set; }
        internal Action? BeforeAppend { get; init; }
        internal Action? AfterAppend { get; init; }
        public Task AppendAsync(IReadOnlyCollection<FlightPointsLedgerEntry> entries, CancellationToken cancellationToken = default)
        {
            Attempts.Add(entries.ToArray()); BeforeAppend?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (FailNext) { FailNext = false; throw Failure; }
            Persisted.AddRange(entries); AfterAppend?.Invoke(); return Task.CompletedTask;
        }
        public Task<FlightPointsLedgerEntry?> FindOwnedAsync(Guid ledgerEntryId, Guid ownerHobbiesModuleProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> GetNetPointsAsync(Guid ownerHobbiesModuleProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlightPointsHistoryItem>> GetHistoryAsync(Guid ownerHobbiesModuleProfileId, FlightPointsHistoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
