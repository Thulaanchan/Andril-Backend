using Andril.API.Integrations.AI;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.AI.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>Actual coordinator with deterministic protected-boundary simulations only.
/// Fakes do not prove database authorization, deletion, transactions or distributed locking.</summary>
public sealed class AiMaintenanceWorkerTests
{
    private const string Secret = "private-content-credential-connection-secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static AiOperationalRecordReference Record(int n = 1, AiOperationalCategory category = AiOperationalCategory.Billing) =>
        new(Id(n), category, category == AiOperationalCategory.SecurityAudit ? new AiRestrictedSecurityScope(Id(20), Id(21)) :
            new AiModuleOperationalScope(new(new(Id(21), Id(22), AiModule.Learning), Id(23), Id(24))), Id(30));
    private static AiOperationalRetentionEvidence Evidence(AiOperationalRecordReference r, int age = 40) =>
        new(new(r.Category, Id(40), "synthetic-policy", Id(41), Id(42)),
            new(r, Id(40), "synthetic-policy", Id(41), Id(42), Now.AddDays(-age)),
            new(r, Id(43), AiOperationalResolutionState.TerminalReconciled, AiOperationalPendingWork.None),
            new(r, Id(44), Id(45), "synthetic-replay"),
            new(r, Id(46), AiOperationalAccountState.NoConfirmedDeletion));

    private sealed class Dependencies : IAiConversationCleanupRepository, IAiOperationalCleanupRepository,
        IAiOperationalRetentionEvidenceReader, IAiAccountDeletionRepository, IAiRestoreRevocationRepository
    {
        internal int Lists, Attempts, Mutations, EvidenceReads, DeletionCalls, RecoveryCalls;
        internal bool Authorized = true;
        internal IReadOnlyList<AiOperationalCleanupCandidate> Operations = [];
        internal IReadOnlyList<AiConversationCleanupCandidate> Content = [];
        internal Func<AiOperationalRecordReference, AiOperationalRetentionEvidence?> Read = r => Evidence(r);
        internal Func<AiOperationalCleanupCandidate, CancellationToken, Task<AiOperationalCleanupResult>> Cleanup =
            (_, _) => Task.FromResult(new AiOperationalCleanupResult(AiOperationalCleanupOutcome.Removed, AiOperationalRetentionOutcome.EligibleForCleanup));
        internal Func<CancellationToken, Task>? OnList;
        internal AiConversationCleanupOutcome ContentOutcome = AiConversationCleanupOutcome.Purged;
        internal AiAccountDeletionScheduleOutcome DeletionOutcome = AiAccountDeletionScheduleOutcome.Scheduled;
        internal AiRestoreRevocationOutcome RecoveryOutcome = AiRestoreRevocationOutcome.Reconciled;
        internal Guid LastAccount, LastEvent, LastRecovery;
        private void Require(CancellationToken t) { t.ThrowIfCancellationRequested(); if (!Authorized) throw new UnauthorizedAccessException(Secret); }
        async Task<IReadOnlyList<AiOperationalCleanupCandidate>> IAiOperationalCleanupRepository.ListPendingAsync(int limit, CancellationToken t)
        { Lists++; Require(t); if (OnList is not null) await OnList(t); return Operations; }
        Task<IReadOnlyList<AiConversationCleanupCandidate>> IAiConversationCleanupRepository.ListPendingAsync(int limit, CancellationToken t)
        { Lists++; Require(t); return Task.FromResult(Content); }
        public Task<AiOperationalRetentionEvidence?> ReadVerifiedEvidenceAsync(AiOperationalRecordReference r, CancellationToken t)
        { EvidenceReads++; Require(t); return Task.FromResult(Read(r)); }
        public async Task<AiOperationalCleanupResult> TryCleanupAsync(AiOperationalCleanupCandidate c, CancellationToken t)
        { Attempts++; Require(t); var result = await Cleanup(c, t); if (result.Outcome == AiOperationalCleanupOutcome.Removed) Mutations++; return result; }
        public Task<AiConversationCleanupOutcome> TryPurgeAsync(AiConversationCleanupCandidate c, CancellationToken t)
        { Attempts++; Require(t); if (ContentOutcome == AiConversationCleanupOutcome.Purged) Mutations++; return Task.FromResult(ContentOutcome); }
        public Task<AiAccountDeletionScheduleOutcome> EnsureCleanupScheduledAsync(Guid account, Guid evt, CancellationToken t)
        { DeletionCalls++; Require(t); LastAccount = account; LastEvent = evt; return Task.FromResult(DeletionOutcome); }
        public Task<AiRestoreRevocationOutcome> EnsureRevocationsReconciledAsync(Guid run, CancellationToken t)
        { RecoveryCalls++; Require(t); LastRecovery = run; return Task.FromResult(RecoveryOutcome); }
        internal AiMaintenanceWorker Worker() => new(new(this, new Clock()), new(this), new(this), this, this, new Clock());
    }

    [Theory]
    [InlineData("content")] [InlineData("deletion")] [InlineData("recovery")] [InlineData("operational")] [InlineData("evidence")]
    public void MissingDependenciesCannotCreateWorker(string missing)
    {
        var d = new Dependencies();
        Assert.Throws<ArgumentNullException>(() => new AiMaintenanceWorker(
            missing == "content" ? null! : new(d), missing == "deletion" ? null! : new(d),
            missing == "recovery" ? null! : new(d), missing == "operational" ? null! : d, missing == "evidence" ? null! : d));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(101)] [InlineData(int.MaxValue)]
    public async Task InvalidLimitsNeverReachProtectedDependencies(int limit)
    {
        var d = new Dependencies(); var w = d.Worker();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => w.RunConversationBatchAsync(limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => w.RunOperationalBatchAsync(limit)); Assert.Equal(0, d.Lists);
    }

    [Theory]
    [InlineData(1)] [InlineData(100)]
    public async Task EmptyBoundedBatchesReturnActualZeroCounts(int limit)
    {
        var d = new Dependencies(); var w = d.Worker();
        foreach (var result in new[] { await w.RunConversationBatchAsync(limit), await w.RunOperationalBatchAsync(limit) })
        { Assert.Equal(AiMaintenanceRunOutcome.Completed, result.Outcome); Assert.Equal(0, result.CandidateCount); Assert.Equal(0, result.RemovedCount); }
        Assert.Equal(0, d.Attempts);
    }

    [Theory]
    [InlineData("null")] [InlineData("oversize")] [InlineData("duplicate")] [InlineData("invalid")] [InlineData("null-row")]
    public async Task EntireOperationalSnapshotIsValidatedBeforeMutation(string defect)
    {
        var d = new Dependencies(); var good = new AiOperationalCleanupCandidate(Record());
        d.Operations = defect switch
        {
            "null" => null!, "oversize" => [good, new(Record(2))], "duplicate" => [good, good],
            "invalid" => [good, new(Record(2) with { SourceRevision = Guid.Empty })], _ => [good, null!]
        };
        var result = await d.Worker().RunOperationalBatchAsync(defect == "oversize" ? 1 : 2);
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(0, d.Attempts); Assert.Equal(0, d.EvidenceReads);
    }

    [Theory]
    [InlineData("missing")] [InlineData("anchor")] [InlineData("foreign")] [InlineData("replay")]
    [InlineData("billing")] [InlineData("uncertain")] [InlineData("not-expired")]
    public async Task MissingAuthorityOrUnresolvedLiabilityDefersWithoutDelete(string defect)
    {
        var d = new Dependencies { Operations = [new(Record())] };
        d.Read = r => defect switch
        {
            "missing" => null, "anchor" => Evidence(r) with { Anchor = null },
            "foreign" => Evidence(r) with { Anchor = Evidence(Record(99)).Anchor },
            "replay" => Evidence(r) with { Replay = null },
            "billing" => Evidence(r) with { Resolution = new(r, Id(43), AiOperationalResolutionState.Unresolved, AiOperationalPendingWork.BillingReconciliation) },
            "uncertain" => Evidence(r) with { Resolution = new(r, Id(43), AiOperationalResolutionState.Unresolved, AiOperationalPendingWork.UncertainProvider) },
            _ => Evidence(r, 29)
        };
        var result = await d.Worker().RunOperationalBatchAsync(1);
        Assert.Equal(AiMaintenanceRunOutcome.Completed, result.Outcome); Assert.Equal(1, result.DeferredCount);
        Assert.Equal(0, d.Attempts); Assert.Equal(0, d.Mutations);
        Assert.Equal(defect == "not-expired" ? 0 : 1, result.ReviewRequiredCount);
    }

    [Theory]
    [InlineData(AiOperationalCategory.Quota, 14)] [InlineData(AiOperationalCategory.OperationIdempotency, 14)]
    [InlineData(AiOperationalCategory.Billing, 30)] [InlineData(AiOperationalCategory.SecurityAudit, 30)]
    public async Task ExistingApprovedCategoryBoundaryControlsMutation(AiOperationalCategory category, int days)
    {
        var r = Record(category: category); var d = new Dependencies { Operations = [new(r)] };
        d.Read = x => Evidence(x, days - 1);
        Assert.Equal(1, (await d.Worker().RunOperationalBatchAsync(1)).DeferredCount); Assert.Equal(0, d.Mutations);
        d.Read = x => Evidence(x, days);
        Assert.Equal(1, (await d.Worker().RunOperationalBatchAsync(1)).RemovedCount); Assert.Equal(1, d.Mutations);
    }

    [Fact]
    public async Task ConversationExpiryAndPurgeDeadlineArePreservedAndDoNotDelayPromptCleanup()
    {
        var actor = new AiAuthorizedActor(Id(1), Id(2), AiModule.Learning);
        var conversation = AiConversationEntity.CreateForFirstUserMessage(actor, Id(3), Now.AddDays(-14));
        Assert.Equal(Now, conversation.ExpiresAtUtc);
        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(conversation, Now));
        var candidate = new AiConversationCleanupCandidate(conversation.Id, actor.UserId, actor.ModuleProfileId, actor.Module,
            Now, Now.Add(AiConversationRetentionPolicy.ActiveDataPurgeTarget));
        var d = new Dependencies { Content = [candidate] };
        Assert.Equal(TimeSpan.FromHours(24), candidate.PurgeTargetUtc - candidate.RevokedAtUtc);
        Assert.Equal(1, (await d.Worker().RunConversationBatchAsync(1)).RemovedCount);
        Assert.Equal(Now, candidate.RevokedAtUtc); Assert.Equal(Now.AddHours(24), candidate.PurgeTargetUtc);
        d.Content = [candidate with { PurgeTargetUtc = Now.AddHours(23) }];
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, (await d.Worker().RunConversationBatchAsync(1)).Outcome);
        Assert.Equal(1, d.Attempts);
    }

    [Theory]
    [InlineData("content")] [InlineData("metadata")] [InlineData("deletion")] [InlineData("recovery")]
    public async Task MissingWorkerAuthorizationNeverPerformsProtectedMutation(string task)
    {
        var d = new Dependencies { Authorized = false }; var w = d.Worker();
        var result = await (task switch
        {
            "content" => w.RunConversationBatchAsync(1), "metadata" => w.RunOperationalBatchAsync(1),
            "deletion" => w.EnsureAccountDeletionScheduledAsync(Id(1), Id(2)), _ => w.EnsureRestoreRevocationsReconciledAsync(Id(3))
        });
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome); Assert.Equal(0, d.Mutations);
        Assert.DoesNotContain(Secret, result.ToString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidDeletionReferencesCannotReachRepository(bool account)
    {
        var d = new Dependencies();
        await Assert.ThrowsAsync<ArgumentException>(() => d.Worker().EnsureAccountDeletionScheduledAsync(account ? Guid.Empty : Id(1), account ? Id(2) : Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => d.Worker().EnsureRestoreRevocationsReconciledAsync(Guid.Empty));
        Assert.Equal(0, d.DeletionCalls); Assert.Equal(0, d.RecoveryCalls);
    }

    [Theory]
    [InlineData(AiAccountDeletionScheduleOutcome.Scheduled, true)] [InlineData(AiAccountDeletionScheduleOutcome.AlreadyRecorded, true)]
    [InlineData(AiAccountDeletionScheduleOutcome.NotConfirmedOrMissing, false)] [InlineData(AiAccountDeletionScheduleOutcome.Unknown, false)]
    public async Task DeletionResultsMeanSchedulingOnly(AiAccountDeletionScheduleOutcome outcome, bool complete)
    {
        var d = new Dependencies { DeletionOutcome = outcome };
        var result = await d.Worker().EnsureAccountDeletionScheduledAsync(Id(1), Id(2));
        Assert.Equal(complete ? AiMaintenanceRunOutcome.Completed : AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(Id(1), d.LastAccount); Assert.Equal(Id(2), d.LastEvent); Assert.Equal(0, result.RemovedCount);
    }

    [Theory]
    [InlineData(AiRestoreRevocationOutcome.Reconciled, true)] [InlineData(AiRestoreRevocationOutcome.AlreadyReconciled, true)]
    [InlineData(AiRestoreRevocationOutcome.NotReady, false)] [InlineData(AiRestoreRevocationOutcome.Unknown, false)]
    public async Task RecoveryCannotConvertInvalidEvidenceIntoTrafficRelease(AiRestoreRevocationOutcome outcome, bool complete)
    {
        var d = new Dependencies { RecoveryOutcome = outcome };
        var result = await d.Worker().EnsureRestoreRevocationsReconciledAsync(Id(3));
        Assert.Equal(complete ? AiMaintenanceRunOutcome.Completed : AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(Id(3), d.LastRecovery); Assert.Equal(0, result.RemovedCount);
    }

    [Theory]
    [InlineData(AiOperationalCleanupOutcome.Unknown)] [InlineData(AiOperationalCleanupOutcome.ReconciliationRequired)]
    [InlineData(AiOperationalCleanupOutcome.Removed)] [InlineData(AiOperationalCleanupOutcome.Anonymized)]
    public async Task ContradictoryOrUnknownCleanupResultsNeverCountAsSuccess(AiOperationalCleanupOutcome outcome)
    {
        var d = new Dependencies { Operations = [new(Record())] };
        d.Cleanup = (_, _) => Task.FromResult(new AiOperationalCleanupResult(outcome, AiOperationalRetentionOutcome.NotYetExpired));
        var result = await d.Worker().RunOperationalBatchAsync(1);
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome); Assert.Equal(0, result.RemovedCount);
    }

    [Fact]
    public async Task PartialFailureDoesNotClaimRollbackOrExposeSensitiveExceptionAndSemaphoreReleases()
    {
        var d = new Dependencies { Operations = [new(Record()), new(Record(2))] };
        d.Cleanup = (c, _) => c.Record.RecordId == Id(2) ? throw new InvalidOperationException(Secret)
            : Task.FromResult(new AiOperationalCleanupResult(AiOperationalCleanupOutcome.Removed, AiOperationalRetentionOutcome.EligibleForCleanup));
        var result = await d.Worker().RunOperationalBatchAsync(2);
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome); Assert.Equal(1, d.Mutations);
        Assert.Equal(0, result.RemovedCount); Assert.Equal(2, d.Attempts); Assert.DoesNotContain(Secret, result.ToString());
        d.Operations = []; Assert.Equal(AiMaintenanceRunOutcome.Completed, (await d.Worker().RunOperationalBatchAsync(1)).Outcome);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationReleasesLocalSemaphoreWithoutAutomaticRetry(bool during)
    {
        var d = new Dependencies(); using var cts = new CancellationTokenSource();
        if (!during) cts.Cancel(); else d.OnList = _ => { cts.Cancel(); return Task.CompletedTask; };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.Worker().RunOperationalBatchAsync(1, cts.Token));
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Equal(0, d.Mutations); Assert.Equal(during ? 1 : 0, d.Lists);
        d.OnList = null; Assert.Equal(AiMaintenanceRunOutcome.Completed, (await d.Worker().RunOperationalBatchAsync(1)).Outcome);
    }

    [Fact]
    public async Task AggregateOutcomesKeepRemovalAnonymizationDeferralAndReplayDistinct()
    {
        var d = new Dependencies { Operations = Enumerable.Range(1, 6).Select(n => new AiOperationalCleanupCandidate(Record(n))).ToArray() };
        d.Read = r => r.RecordId == Id(2) ? Evidence(r) with { Erasure = new(r, Id(46), AiOperationalAccountState.ConfirmedDeleted,
            new(r, Id(50), "synthetic-disposition", AiOperationalDispositionKind.Anonymize, Now.AddDays(1))) } : Evidence(r);
        d.Cleanup = (c, _) => Task.FromResult(c.Record.RecordId == Id(1)
            ? new AiOperationalCleanupResult(AiOperationalCleanupOutcome.Removed, AiOperationalRetentionOutcome.EligibleForCleanup)
            : c.Record.RecordId == Id(2) ? new(AiOperationalCleanupOutcome.Anonymized, AiOperationalRetentionOutcome.EligibleForAnonymization)
            : c.Record.RecordId == Id(3) ? new(AiOperationalCleanupOutcome.AlreadyRemoved, AiOperationalRetentionOutcome.EligibleForCleanup)
            : c.Record.RecordId == Id(4) ? new(AiOperationalCleanupOutcome.NotEligibleOrMissing, AiOperationalRetentionOutcome.NotYetExpired)
            : c.Record.RecordId == Id(5) ? new(AiOperationalCleanupOutcome.Deferred, AiOperationalRetentionOutcome.DeferredUnresolvedOperation)
            : new(AiOperationalCleanupOutcome.ReviewRequired, AiOperationalRetentionOutcome.RetentionPolicyUnavailable));
        var result = await d.Worker().RunOperationalBatchAsync(6);
        Assert.Equal(AiMaintenanceRunOutcome.Completed, result.Outcome); Assert.Equal(6, result.CandidateCount);
        Assert.Equal(1, result.RemovedCount); Assert.Equal(1, result.AnonymizedCount); Assert.Equal(1, result.AlreadyRemovedCount);
        Assert.Equal(1, result.NotEligibleCount); Assert.Equal(2, result.DeferredCount); Assert.Equal(1, result.ReviewRequiredCount);
    }

    [Theory]
    [InlineData(AiConversationCleanupOutcome.NotEligibleOrMissing)]
    [InlineData(AiConversationCleanupOutcome.DeferredByRetentionPolicy)]
    [InlineData(AiConversationCleanupOutcome.Unknown)]
    public async Task ConversationDependencyResultsDoNotFabricateRemoval(AiConversationCleanupOutcome outcome)
    {
        var d = new Dependencies { Content = [new(Id(1), Id(2), Id(3), AiModule.Learning, Now, Now.AddHours(24))], ContentOutcome = outcome };
        var result = await d.Worker().RunConversationBatchAsync(1);
        Assert.Equal(0, result.RemovedCount); Assert.Equal(0, d.Mutations);
        if (outcome == AiConversationCleanupOutcome.Unknown) Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome);
        else
        {
            Assert.Equal(AiMaintenanceRunOutcome.Completed, result.Outcome);
            Assert.Equal(outcome == AiConversationCleanupOutcome.NotEligibleOrMissing ? 1 : 0, result.NotEligibleCount);
            Assert.Equal(outcome == AiConversationCleanupOutcome.DeferredByRetentionPolicy ? 1 : 0, result.DeferredCount);
        }
    }

    [Fact]
    public async Task CancellationAfterPartialMutationRequiresReconciliationAndNeverRetries()
    {
        var d = new Dependencies { Operations = [new(Record()), new(Record(2))] }; using var cts = new CancellationTokenSource();
        d.Cleanup = (c, token) =>
        {
            Assert.Equal(cts.Token, token);
            if (c.Record.RecordId == Id(2)) cts.Cancel();
            return Task.FromResult(new AiOperationalCleanupResult(AiOperationalCleanupOutcome.Removed, AiOperationalRetentionOutcome.EligibleForCleanup));
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.Worker().RunOperationalBatchAsync(2, cts.Token));
        Assert.Contains("reconcile", error.Message); Assert.Null(error.InnerException); Assert.Equal(2, d.Attempts);
        Assert.Equal(2, d.Mutations); // Both fake operations completed before cancellation was observed; no rollback claim.
        d.Operations = []; Assert.Equal(AiMaintenanceRunOutcome.Completed, (await d.Worker().RunOperationalBatchAsync(1)).Outcome);
    }

    [Fact]
    public async Task NullAndUndefinedDependencyResponsesAreSafelyReported()
    {
        var d = new Dependencies { Operations = [new(Record())] };
        foreach (var response in new AiOperationalCleanupResult?[] { null,
            new((AiOperationalCleanupOutcome)99, AiOperationalRetentionOutcome.EligibleForCleanup),
            new(AiOperationalCleanupOutcome.Removed, AiOperationalRetentionOutcome.Unknown) })
        {
            d.Cleanup = (_, _) => Task.FromResult(response!);
            Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, (await d.Worker().RunOperationalBatchAsync(1)).Outcome);
        }
        d.OnList = _ => throw new InvalidOperationException(Secret);
        var result = await d.Worker().RunOperationalBatchAsync(1);
        Assert.Equal(AiMaintenanceRunOutcome.ReconciliationRequired, result.Outcome); Assert.DoesNotContain(Secret, result.ToString());
    }

    [Fact]
    public async Task ProcessLocalOverlapAcrossInstancesAndTaskKindsIsRejectedWithoutMutation()
    {
        var d = new Dependencies();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        d.OnList = async t => { entered.SetResult(); await release.Task.WaitAsync(t); };
        var first = d.Worker().RunOperationalBatchAsync(1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = await d.Worker().EnsureAccountDeletionScheduledAsync(Id(1), Id(2));
            Assert.Equal(AiMaintenanceRunOutcome.AlreadyRunning, other.Outcome); Assert.Equal(0, d.DeletionCalls);
        }
        finally { release.TrySetResult(); await first; }
        Assert.Equal(AiMaintenanceRunOutcome.Completed, (await d.Worker().RunConversationBatchAsync(1)).Outcome);
        Assert.False(typeof(IHostedService).IsAssignableFrom(typeof(AiMaintenanceWorker)));
    }
}
