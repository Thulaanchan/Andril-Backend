using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;

namespace Andril.API.Integrations.AI;

public enum AiMaintenanceTask { ConversationContent, OperationalMetadata, AccountDeletionScheduling, RecoveryRevocation }
public enum AiMaintenanceRunOutcome { Completed, AlreadyRunning, ReconciliationRequired }

/// <summary>Minimized internal monitoring result, not deletion/access/financial authority.
/// Completed acknowledges only this particular task. Account-deletion scheduling is not erasure;
/// recovery reconciliation does not release recovery fences. Deferred records are not removed.</summary>
public sealed record AiMaintenanceRunResult(AiMaintenanceTask Task, AiMaintenanceRunOutcome Outcome,
    int CandidateCount = 0, int RemovedCount = 0, int AnonymizedCount = 0, int AlreadyRemovedCount = 0,
    int NotEligibleCount = 0, int DeferredCount = 0, int ReviewRequiredCount = 0)
{
    public override string ToString() => $"AI maintenance ({Task}, {Outcome}) [aggregate counts only]";
}

/// <summary>Unregistered, inactive MANUAL coordination; not IHostedService/BackgroundService.
/// No timer, interval, default batch size, event discovery, worker identity or database query.
/// Every injected repository/evidence reader MUST independently authorize the protected internal
/// maintenance caller and fence current scope/revision/recovery at every operation. IDs, options
/// and candidate/evidence objects are not grants. No permissive worker-auth fallback is supplied.
///
/// One process-wide semaphore prevents overlap among instances of this coordinator ONLY; it is
/// not distributed locking or protection against other callers. M2 must provide transactional
/// rechecks/fencing across workers, replicas and normal operations before activation.
/// Existing policy anchors, unresolved finance, replay and legal dispositions remain mandatory.
/// No consent-audit purge contract is available: consent audits are deliberately not deleted.
/// Never expose these methods as user endpoints or automatically retry uncertain tasks.</summary>
public sealed class AiMaintenanceWorker
{
    private static readonly SemaphoreSlim LocalRun = new(1, 1);
    private readonly AiConversationCleanupService _content;
    private readonly AiAccountDeletionService _deletion;
    private readonly AiRestoreRevocationService _recovery;
    private readonly IAiOperationalCleanupRepository _operational;
    private readonly IAiOperationalRetentionEvidenceReader _evidence;
    private readonly TimeProvider _clock;

    public AiMaintenanceWorker(AiConversationCleanupService content, AiAccountDeletionService deletion,
        AiRestoreRevocationService recovery, IAiOperationalCleanupRepository operational,
        IAiOperationalRetentionEvidenceReader evidence, TimeProvider? clock = null)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _operational = operational ?? throw new ArgumentNullException(nameof(operational));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _clock = clock ?? TimeProvider.System;
    }

    public Task<AiMaintenanceRunResult> RunConversationBatchAsync(int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit, IAiConversationCleanupRepository.MaxBatchSize);
        return RunExclusiveAsync(AiMaintenanceTask.ConversationContent, async token =>
        {
            var result = await _content.RunBatchAsync(limit, token).ConfigureAwait(false);
            return new(AiMaintenanceTask.ConversationContent, AiMaintenanceRunOutcome.Completed,
                CandidateCount: result.CandidateCount, RemovedCount: result.PurgedCount,
                NotEligibleCount: result.NotEligibleOrMissingCount, DeferredCount: result.DeferredCount);
        }, cancellationToken);
    }

    public Task<AiMaintenanceRunResult> RunOperationalBatchAsync(int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit, IAiOperationalCleanupRepository.MaxBatchSize);
        return RunExclusiveAsync(AiMaintenanceTask.OperationalMetadata, async token =>
        {
            var pending = await _operational.ListPendingAsync(limit, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (pending is null || pending.Count > limit) throw InvalidBatch();
            var candidates = pending.ToArray();
            var seen = new HashSet<Guid>();
            // Reject the entire malformed snapshot before any destructive request.
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (candidate is null || !AiOperationalRetentionPolicy.ValidReference(candidate.Record) ||
                    !seen.Add(candidate.Record.RecordId)) throw InvalidBatch();
            }
            var removed = 0; var anonymized = 0; var already = 0; var missing = 0; var deferred = 0; var review = 0;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                var evidence = await _evidence.ReadVerifiedEvidenceAsync(candidate.Record, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var decision = AiOperationalRetentionPolicy.Evaluate(candidate.Record, evidence, _clock.GetUtcNow());
                if (decision.Outcome is not (AiOperationalRetentionOutcome.EligibleForCleanup or AiOperationalRetentionOutcome.EligibleForAnonymization))
                {
                    deferred++;
                    if (decision.ReviewRequired) review++;
                    continue;
                }
                // Pure eligibility is NOT delete permission. Repository must reload/lock and
                // independently resolve current trusted evidence/authentication before mutation.
                var result = await _operational.TryCleanupAsync(candidate, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result is null || !Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.RetentionReason) ||
                    result.RetentionReason == AiOperationalRetentionOutcome.Unknown) throw InvalidBatch();
                switch (result.Outcome)
                {
                    case AiOperationalCleanupOutcome.Removed when result.RetentionReason == AiOperationalRetentionOutcome.EligibleForCleanup:
                        removed++; break;
                    case AiOperationalCleanupOutcome.Anonymized when result.RetentionReason == AiOperationalRetentionOutcome.EligibleForAnonymization:
                        anonymized++; break;
                    case AiOperationalCleanupOutcome.AlreadyRemoved:
                        already++; break;
                    case AiOperationalCleanupOutcome.NotEligibleOrMissing:
                        missing++; break;
                    case AiOperationalCleanupOutcome.Deferred:
                        deferred++; break;
                    case AiOperationalCleanupOutcome.ReviewRequired:
                        deferred++; review++; break;
                    default:
                        // Unknown/uncertain/mismatched outcomes never report successful cleanup.
                        throw InvalidBatch();
                }
            }
            return new(AiMaintenanceTask.OperationalMetadata, AiMaintenanceRunOutcome.Completed,
                candidates.Length, removed, anonymized, already, missing, deferred, review);
        }, cancellationToken);
    }

    public Task<AiMaintenanceRunResult> EnsureAccountDeletionScheduledAsync(Guid accountId, Guid deletionEventId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty || deletionEventId == Guid.Empty)
            throw new ArgumentException("A protected account-deletion reference is required.");
        return RunExclusiveAsync(AiMaintenanceTask.AccountDeletionScheduling, async token =>
        {
            await _deletion.EnsureCleanupScheduledAsync(accountId, deletionEventId, token).ConfigureAwait(false);
            return new(AiMaintenanceTask.AccountDeletionScheduling, AiMaintenanceRunOutcome.Completed);
        }, cancellationToken);
    }

    public Task<AiMaintenanceRunResult> EnsureRestoreRevocationsReconciledAsync(Guid recoveryRunId,
        CancellationToken cancellationToken = default)
    {
        if (recoveryRunId == Guid.Empty) throw new ArgumentException("A protected recovery-run reference is required.");
        return RunExclusiveAsync(AiMaintenanceTask.RecoveryRevocation, async token =>
        {
            await _recovery.EnsureRevocationsReconciledAsync(recoveryRunId, token).ConfigureAwait(false);
            return new(AiMaintenanceTask.RecoveryRevocation, AiMaintenanceRunOutcome.Completed);
        }, cancellationToken);
    }

    private static async Task<AiMaintenanceRunResult> RunExclusiveAsync(AiMaintenanceTask task,
        Func<CancellationToken, Task<AiMaintenanceRunResult>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await LocalRun.WaitAsync(0, token).ConfigureAwait(false))
            return new(task, AiMaintenanceRunOutcome.AlreadyRunning);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await action(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Earlier work may already be durable. Cancellation never promises rollback.
            throw new OperationCanceledException("AI maintenance cancelled; reconcile prior work.", token);
        }
        catch (Exception)
        {
            // Storage/auth errors may contain private IDs or connection data. No raw exception,
            // inner exception, log text, success count or automatic retry is exposed here.
            return new(task, AiMaintenanceRunOutcome.ReconciliationRequired);
        }
        finally { LocalRun.Release(); }
    }

    private static void RequireLimit(int limit, int maximum)
    {
        if (limit < 1 || limit > maximum) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private static InvalidOperationException InvalidBatch() => new("AI maintenance batch could not be verified.");
    public override string ToString() => "AI maintenance coordinator [unregistered; no worker authority granted]";
}
