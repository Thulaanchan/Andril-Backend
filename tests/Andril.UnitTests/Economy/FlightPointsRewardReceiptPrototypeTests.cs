using Andril.Economy.FlightPoints;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Economy;

// PROPOSED TEST-ONLY receipt semantics. No ledger repository, database, M2 authority,
// production coordinator or business-entitlement claim is implemented by these fakes.
public sealed class FlightPointsRewardReceiptPrototypeTests
{
    [Fact]
    public async Task T01_IdenticalReconstructedIntentReturnsOriginalSyntheticReceipt()
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend();
        var journal = new ProposedReceiptJournal(backend);
        var first = await journal.ProcessAsync(source.Intent());
        var replay = await journal.ProcessAsync(source.Intent());
        Assert.Same(first, replay); Assert.Equal(1, backend.Attempts);
        Assert.Equal(source.Key(), first.Intent.Identity);
        Assert.Equal(source.Contribution.OwnerHobbiesModuleProfileId, first.Intent.Owner);
        Assert.Equal(source.Accepted.Decision!.DecidedAtUtc, first.AcceptedResult.CreatedAtUtc);
        Assert.Equal(FlightPointsRewardPolicy.FirstEligibleContributionPoints, first.AcceptedResult.PointsDelta);
        Assert.Single(backend.Records); Assert.Single(journal.Accepted);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("timestamp")]
    [InlineData("reviewer")]
    [InlineData("attraction")]
    [InlineData("session")]
    public async Task T02_T03_ConflictingEvidenceNeverOverwritesOrPostsAnotherResult(string changed)
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend();
        var journal = new ProposedReceiptJournal(backend);
        var original = await journal.ProcessAsync(source.Intent());
        var contribution = source.Contribution;
        if (changed is "attraction" or "session")
            contribution = MapContribution.Create(contribution.ContributionId, contribution.OwnerHobbiesModuleProfileId,
                changed == "attraction" ? Guid.NewGuid() : contribution.AttractionId,
                changed == "session" ? Guid.NewGuid() : contribution.CaptureSessionId,
                contribution.PrivatePhotoStorageObjectKey, contribution.Description, contribution.LocationEvidence, contribution.SubmittedAtUtc);
        var receipt = source.Accepted.Decision!;
        var request = new MapContributionReviewRequest(receipt.Request.DecisionId, contribution.ContributionId,
            receipt.Request.Action, receipt.Request.ExpectedRevision, changed == "reviewer" ? "different-synthetic-reviewer" : receipt.Request.ReviewerSubject);
        var evidence = new MapContributionModerationSnapshot(contribution.ContributionId, contribution.OwnerHobbiesModuleProfileId,
            contribution.AttractionId, contribution.CaptureSessionId, source.Accepted.Revision,
            new(request, changed == "timestamp" ? receipt.DecidedAtUtc.AddSeconds(1) : receipt.DecidedAtUtc));
        var conflict = ProposedRewardIntent.FromSyntheticSource(source.Key(), contribution.OwnerHobbiesModuleProfileId,
            contribution, evidence, changed == "policy" ? "synthetic-policy-B" : "synthetic-policy-A", Facts);
        Assert.Equal(original.Intent.Identity, conflict.Identity);
        await Assert.ThrowsAsync<ProposedReceiptConflictException>(() => journal.ProcessAsync(conflict));
        Assert.Same(original, await journal.ProcessAsync(source.Intent()));
        Assert.Equal(1, backend.Attempts); Assert.Single(backend.Records);
        // Rejecting policy conflicts is candidate behavior; final version-conflict policy is PENDING DECISION.
    }

    [Fact]
    public async Task T04_EligibleComponentsHaveSeparateReceiptsAndExistingAmounts()
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend();
        var journal = new ProposedReceiptJournal(backend);
        var first = await journal.ProcessAsync(source.Intent());
        var explorer = await journal.ProcessAsync(source.Intent(component: FlightPointsRewardType.FirstExplorer));
        Assert.NotEqual(first.Intent.Identity, explorer.Intent.Identity);
        Assert.NotEqual(first.AcceptedResult.LedgerEntryId, explorer.AcceptedResult.LedgerEntryId);
        Assert.Equal(FlightPointsRewardPolicy.FirstExplorerBonusPoints, explorer.AcceptedResult.PointsDelta);
        Assert.Equal(2, backend.Records.Count);
        // Not a proof of First Explorer selection, entitlement uniqueness or atomic component batching.
    }

    [Fact]
    public async Task T05_ForeignOwnerWithSameComponentIdentityCannotReplaceReceipt()
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend();
        var journal = new ProposedReceiptJournal(backend); var original = await journal.ProcessAsync(source.Intent());
        var c = source.Contribution; var foreignOwner = Guid.NewGuid();
        var substituted = MapContribution.Create(c.ContributionId, foreignOwner, c.AttractionId, c.CaptureSessionId,
            c.PrivatePhotoStorageObjectKey, c.Description, c.LocationEvidence, c.SubmittedAtUtc);
        var evidence = new MapContributionModerationSnapshot(c.ContributionId, foreignOwner, c.AttractionId,
            c.CaptureSessionId, source.Accepted.Revision, source.Accepted.Decision);
        var foreign = ProposedRewardIntent.FromSyntheticSource(source.Key(), foreignOwner, substituted, evidence, "synthetic-policy-A", Facts);
        await Assert.ThrowsAsync<ProposedReceiptConflictException>(() => journal.ProcessAsync(foreign));
        Assert.Same(original, await journal.ProcessAsync(source.Intent())); Assert.Equal(1, backend.Attempts);
        Assert.Throws<UnauthorizedAccessException>(() => ProposedRewardIntent.FromSyntheticSource(source.Key(), foreignOwner,
            c, source.Accepted, "synthetic-policy-A", Facts));
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("hobbies")]
    public void T06_WrongModuleCannotConstructReceiptIntent(string module)
    {
        var source = new ProposedRewardSource();
        Assert.Throws<ArgumentException>(() => new ProposedRewardEventIdentity(module,
            source.Key().ContributionId, source.Key().DecisionId, source.Key().RewardType));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void T07_T08_PendingAndRejectedEvidenceCannotConstructRewardIntent(int action)
    {
        var source = new ProposedRewardSource(); var moderation = MapContributionModeration.CreatePending(source.Contribution, 1);
        var evidence = action == 0 ? moderation.Snapshot : moderation.Decide(new(Guid.NewGuid(), source.Contribution.ContributionId,
            MapContributionReviewAction.Reject, 1, "synthetic-reviewer"), ProposedRewardSource.Now);
        Assert.Throws<InvalidOperationException>(() => ProposedRewardIntent.FromSyntheticSource(source.Key(),
            source.Contribution.OwnerHobbiesModuleProfileId, source.Contribution, evidence, "synthetic-policy-A", Facts));
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("decision")]
    [InlineData("contribution")]
    [InlineData("revision")]
    public void T09_InvalidAcceptanceEvidenceCannotConstructReceiptIntent(string changed)
    {
        var source = new ProposedRewardSource(); var c = source.Contribution;
        var evidence = new MapContributionModerationSnapshot(c.ContributionId, c.OwnerHobbiesModuleProfileId,
            c.AttractionId, c.CaptureSessionId, changed == "revision" ? 9 : source.Accepted.Revision,
            changed == "timestamp" ? new(source.Accepted.Decision!.Request, default) : source.Accepted.Decision);
        var key = new ProposedRewardEventIdentity("Hobbies", changed == "contribution" ? Guid.NewGuid() : c.ContributionId,
            changed == "decision" ? Guid.NewGuid() : source.Key().DecisionId, source.Key().RewardType);
        var error = Record.Exception(() => ProposedRewardIntent.FromSyntheticSource(key, c.OwnerHobbiesModuleProfileId,
            c, evidence, "synthetic-policy-A", Facts));
        if (changed == "timestamp") Assert.IsType<ArgumentOutOfRangeException>(error);
        else if (changed == "revision") Assert.IsType<ArgumentException>(error);
        else Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void T09_AcceptedResultMustMatchIntentRatherThanCallerSuppliedGrant()
    {
        var intent = new ProposedRewardSource().Intent();
        var foreignGrant = FlightPointsLedgerEntry.CreateGrant(Guid.NewGuid(), Guid.NewGuid(), intent.Identity.ContributionId,
            intent.Attraction, intent.Identity.RewardType, "synthetic-result", intent.AcceptedAtUtc);
        Assert.Throws<InvalidOperationException>(() => new ProposedAcceptedReceipt(intent, foreignGrant));
        Assert.Throws<ArgumentNullException>(() => ProposedRewardIntent.FromSyntheticSource(null!, intent.Owner,
            new ProposedRewardSource().Contribution, null!, "synthetic-policy-A", Facts));
    }

    [Fact]
    public async Task T10_ReceiptAndAcceptedResultRemainReadOnlyAfterConflictingRetry()
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend(); var journal = new ProposedReceiptJournal(backend);
        var original = await journal.ProcessAsync(source.Intent()); var result = original.AcceptedResult;
        await Assert.ThrowsAsync<ProposedReceiptConflictException>(() => journal.ProcessAsync(source.Intent("synthetic-policy-B")));
        Assert.Equal("synthetic-policy-A", original.Intent.Policy); Assert.Same(result, original.AcceptedResult);
        Assert.Equal(ProposedRewardSource.Now, original.Intent.AcceptedAtUtc);
        Assert.All(typeof(ProposedAcceptedReceipt).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(FlightPointsLedgerEntry).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T11_T12_ConcurrentCandidatesSerializeToOneSyntheticAcceptance(bool conflict)
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend { Pause = true };
        var journal = new ProposedReceiptJournal(backend);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = journal.ProcessAsync(source.Intent(), deadline.Token);
        Task<ProposedAcceptedReceipt>? second = null;
        try
        {
            await backend.Entered.Task.WaitAsync(deadline.Token);
            second = journal.ProcessAsync(source.Intent(conflict ? "synthetic-policy-B" : "synthetic-policy-A"), deadline.Token);
            Assert.False(second.IsCompleted); Assert.Equal(1, backend.Attempts);
        }
        finally { backend.Release.TrySetResult(); }
        var original = await first;
        Assert.NotNull(second);
        if (conflict) await Assert.ThrowsAsync<ProposedReceiptConflictException>(() => second);
        else Assert.Same(original, await second);
        Assert.Equal(1, backend.Attempts); Assert.Single(journal.Accepted); Assert.Single(backend.Records);
    }

    [Fact]
    public async Task T13_KnownPreWriteFailureCreatesNoReceiptAndPermitsSameIntentRetry()
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend { Mode = ProposedCommitMode.PreWriteFailure };
        var journal = new ProposedReceiptJournal(backend);
        await Assert.ThrowsAsync<ProposedPreWriteFailureException>(() => journal.ProcessAsync(source.Intent()));
        Assert.Empty(journal.Accepted); Assert.Empty(backend.Records); Assert.Equal(0, journal.UncertainCount);
        backend.Mode = ProposedCommitMode.Normal;
        var result = await journal.ProcessAsync(source.Intent());
        Assert.Equal(source.Intent(), result.Intent); Assert.Equal(2, backend.Attempts); Assert.Single(backend.Records);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("waiting")]
    [InlineData("late")]
    public async Task T14_CancellationNeverReturnsFalseSuccess(string stage)
    {
        var source = new ProposedRewardSource(); using var cancellation = new CancellationTokenSource();
        var backend = new ProposedReceiptBackend { Pause = stage == "waiting",
            AfterCommit = stage == "late" ? cancellation.Cancel : null };
        var journal = new ProposedReceiptJournal(backend);
        if (stage == "pre") cancellation.Cancel();
        var pending = journal.ProcessAsync(source.Intent(), cancellation.Token);
        if (stage == "waiting") { await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Empty(journal.Accepted);
        Assert.Equal(stage == "late" ? 1 : 0, backend.Records.Count);
        if (stage == "pre") { Assert.Equal(0, backend.Attempts); Assert.Equal(0, journal.UncertainCount); }
        else
        {
            await Assert.ThrowsAsync<ProposedReconciliationRequiredException>(() => journal.ProcessAsync(source.Intent()));
            Assert.Equal(1, backend.Attempts);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T15_UnknownOutcomeBlocksAnotherAttemptUntilAuthoritativeSyntheticReconciliation(bool committed)
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend
        { Mode = committed ? ProposedCommitMode.UnknownCommitted : ProposedCommitMode.UnknownRolledBack };
        var journal = new ProposedReceiptJournal(backend);
        await Assert.ThrowsAsync<ProposedUnknownCommitException>(() => journal.ProcessAsync(source.Intent()));
        Assert.Empty(journal.Accepted); Assert.Equal(1, journal.UncertainCount);
        await Assert.ThrowsAsync<ProposedReconciliationRequiredException>(() => journal.ProcessAsync(source.Intent()));
        Assert.Equal(1, backend.Attempts);
        backend.HideOutcome = true;
        await Assert.ThrowsAsync<ProposedReconciliationRequiredException>(() => journal.ReconcileAsync(source.Intent()));
        Assert.Equal(1, journal.UncertainCount); Assert.Equal(1, backend.Attempts);
        backend.HideOutcome = false;
        var reconciled = await journal.ReconcileAsync(source.Intent());
        Assert.Equal(0, journal.UncertainCount);
        backend.Mode = ProposedCommitMode.Normal;
        var result = await journal.ProcessAsync(source.Intent());
        if (committed) { Assert.Same(reconciled, result); Assert.Equal(1, backend.Attempts); }
        else { Assert.Null(reconciled); Assert.Equal(2, backend.Attempts); }
        Assert.Single(backend.Records); Assert.Single(journal.Accepted);
        // The fake returns explicit committed/confirmed-rollback evidence; mere absence never proves rollback.
    }

    [Theory]
    [InlineData(FlightPointsRewardType.FirstEligibleContribution)]
    [InlineData(FlightPointsRewardType.FirstExplorer)]
    public async Task T16_ReversalKeepsOriginalGrantAndDoesNotFreeComponentIdentity(FlightPointsRewardType component)
    {
        var source = new ProposedRewardSource(); var backend = new ProposedReceiptBackend(); var journal = new ProposedReceiptJournal(backend);
        var original = await journal.ProcessAsync(source.Intent(component: component));
        var grant = original.AcceptedResult;
        var reversal = FlightPointsLedgerEntry.CreateReversal(Guid.NewGuid(), grant, "synthetic-reversal", ProposedRewardSource.Now.AddHours(1));
        Assert.Equal(grant.LedgerEntryId, reversal.ReversedEntryId); Assert.Equal(grant.OwnerHobbiesModuleProfileId, reversal.OwnerHobbiesModuleProfileId);
        Assert.Equal(grant.ContributionId, reversal.ContributionId); Assert.Equal(grant.AttractionId, reversal.AttractionId);
        Assert.Equal(component, reversal.RewardType); Assert.Equal(-grant.PointsDelta, reversal.PointsDelta);
        Assert.Same(original, await journal.ProcessAsync(source.Intent(component: component)));
        Assert.Equal(1, backend.Attempts); Assert.Equal(FlightPointsLedgerEntryType.Grant, grant.EntryType);
        // No First Explorer reassignment is permitted or implemented. Re-award/entitlement scope is PENDING DECISION.
    }

    private static readonly FlightPointsEligibility Facts = new(true, false, true, false, true, true);
}

internal sealed class ProposedAcceptedReceipt
{
    internal ProposedAcceptedReceipt(ProposedRewardIntent intent, FlightPointsLedgerEntry acceptedResult)
    {
        ArgumentNullException.ThrowIfNull(intent); ArgumentNullException.ThrowIfNull(acceptedResult);
        if (acceptedResult.EntryType != FlightPointsLedgerEntryType.Grant || acceptedResult.OwnerHobbiesModuleProfileId != intent.Owner
            || acceptedResult.ContributionId != intent.Identity.ContributionId || acceptedResult.AttractionId != intent.Attraction
            || acceptedResult.RewardType != intent.Identity.RewardType || acceptedResult.PointsDelta != intent.Points
            || acceptedResult.CreatedAtUtc != intent.AcceptedAtUtc)
            throw new InvalidOperationException("Proposed accepted result does not match receipt evidence.");
        Intent = intent; AcceptedResult = acceptedResult;
    }
    public ProposedRewardIntent Intent { get; }
    public FlightPointsLedgerEntry AcceptedResult { get; }
}

internal sealed class ProposedReceiptConflictException() : Exception("Proposed receipt payload conflict.");
internal sealed class ProposedReconciliationRequiredException() : Exception("Proposed receipt requires reconciliation.");
internal sealed class ProposedUnknownCommitException() : Exception("Synthetic commit outcome unknown.");
internal sealed class ProposedPreWriteFailureException() : Exception("Synthetic known failure before any write.");
internal enum ProposedCommitMode { Normal, PreWriteFailure, UnknownCommitted, UnknownRolledBack }
internal enum ProposedObservedOutcome { Unknown, Committed, ConfirmedNotWritten }

// A single-instance, in-memory journal. Its semaphore proves no cross-process or PostgreSQL guarantee.
internal sealed class ProposedReceiptJournal(ProposedReceiptBackend backend)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ProposedRewardEventIdentity, ProposedAcceptedReceipt> _accepted = [];
    private readonly Dictionary<ProposedRewardEventIdentity, ProposedRewardIntent> _uncertain = [];
    internal IReadOnlyCollection<ProposedAcceptedReceipt> Accepted => _accepted.Values;
    internal int UncertainCount => _uncertain.Count;
    internal async Task<ProposedAcceptedReceipt> ProcessAsync(ProposedRewardIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent); token.ThrowIfCancellationRequested();
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_uncertain.ContainsKey(intent.Identity)) throw new ProposedReconciliationRequiredException();
            if (_accepted.TryGetValue(intent.Identity, out var existing))
            {
                if (existing.Intent != intent) throw new ProposedReceiptConflictException();
                return existing;
            }
            try
            {
                var receipt = await backend.CommitAsync(intent, token);
                token.ThrowIfCancellationRequested();
                _accepted.Add(intent.Identity, receipt); return receipt;
            }
            catch (ProposedUnknownCommitException) { _uncertain.Add(intent.Identity, intent); throw; }
            catch (OperationCanceledException) { _uncertain.Add(intent.Identity, intent); throw; }
        }
        finally { _gate.Release(); }
    }
    internal async Task<ProposedAcceptedReceipt?> ReconcileAsync(ProposedRewardIntent intent, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); await _gate.WaitAsync(token);
        try
        {
            if (!_uncertain.TryGetValue(intent.Identity, out var uncertain)) throw new ProposedReconciliationRequiredException();
            if (uncertain != intent) throw new ProposedReceiptConflictException();
            var (outcome, receipt) = backend.Observe(intent.Identity);
            token.ThrowIfCancellationRequested();
            if (outcome == ProposedObservedOutcome.Unknown) throw new ProposedReconciliationRequiredException();
            if (outcome == ProposedObservedOutcome.Committed)
            {
                if (receipt is null || receipt.Intent != intent) throw new ProposedReceiptConflictException();
                _accepted.Add(intent.Identity, receipt);
            }
            _uncertain.Remove(intent.Identity); return receipt;
        }
        finally { _gate.Release(); }
    }
}

internal sealed class ProposedReceiptBackend
{
    private readonly Dictionary<ProposedRewardEventIdentity, ProposedAcceptedReceipt> _records = [];
    private readonly HashSet<ProposedRewardEventIdentity> _confirmedNotWritten = [];
    private int _attempts;
    internal ProposedCommitMode Mode { get; set; }
    internal bool Pause { get; init; }
    internal bool HideOutcome { get; set; }
    internal Action? AfterCommit { get; init; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Attempts => Volatile.Read(ref _attempts);
    internal IReadOnlyCollection<ProposedAcceptedReceipt> Records => _records.Values;
    internal async Task<ProposedAcceptedReceipt> CommitAsync(ProposedRewardIntent intent, CancellationToken token)
    {
        Interlocked.Increment(ref _attempts); Entered.TrySetResult();
        try
        {
            if (Pause) await Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (Mode == ProposedCommitMode.PreWriteFailure) throw new ProposedPreWriteFailureException();
            if (Mode == ProposedCommitMode.UnknownRolledBack)
            { _confirmedNotWritten.Add(intent.Identity); throw new ProposedUnknownCommitException(); }
            var grant = FlightPointsLedgerEntry.CreateGrant(Guid.NewGuid(), intent.Owner, intent.Identity.ContributionId,
                intent.Attraction, intent.Identity.RewardType, "proposed-test-component", intent.AcceptedAtUtc);
            var receipt = new ProposedAcceptedReceipt(intent, grant);
            _records.Add(intent.Identity, receipt); _confirmedNotWritten.Remove(intent.Identity);
            if (Mode == ProposedCommitMode.UnknownCommitted) throw new ProposedUnknownCommitException();
            AfterCommit?.Invoke(); return receipt;
        }
        catch (OperationCanceledException)
        { if (!_records.ContainsKey(intent.Identity)) _confirmedNotWritten.Add(intent.Identity); throw; }
    }
    internal (ProposedObservedOutcome Outcome, ProposedAcceptedReceipt? Receipt) Observe(ProposedRewardEventIdentity identity)
    {
        if (HideOutcome) return (ProposedObservedOutcome.Unknown, null);
        if (_records.TryGetValue(identity, out var receipt)) return (ProposedObservedOutcome.Committed, receipt);
        return _confirmedNotWritten.Contains(identity) ? (ProposedObservedOutcome.ConfirmedNotWritten, null) : (ProposedObservedOutcome.Unknown, null);
    }
}
