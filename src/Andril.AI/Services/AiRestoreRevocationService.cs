
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;

namespace Andril.AI.Services;

/// <summary>
/// Internal recovery orchestration for AI-PRE-04 and AI-PRIV-01.
/// Does not restore databases, release recovery gates or authorize AI access.
/// The repository must independently verify maintenance permissions,
/// the current recovery run and authoritative revocation evidence.
/// Do not register in production before those controls are verified.
/// </summary>
public sealed class AiRestoreRevocationService
{
    private readonly IAiRestoreRevocationRepository _repository;

    public AiRestoreRevocationService(
        IAiRestoreRevocationRepository repository)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Request durable reconciliation for a trusted recovery run.
    /// The identifier is a lookup key, not an authorization credential.
    /// A successful outcome is not permission to reopen AI traffic
    /// and is not evidence of physical data erasure.
    /// </summary>
    public async Task<AiRestoreRevocationOutcome>
        EnsureRevocationsReconciledAsync(
            Guid recoveryRunId,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (recoveryRunId == Guid.Empty)
            throw new ArgumentException(
                "A recovery run identifier is required.",
                nameof(recoveryRunId));

        // The repository must revalidate the current run, permissions,
        // revocation coverage and recovery fence on every invocation.
        var outcome = await _repository.EnsureRevocationsReconciledAsync(
            recoveryRunId, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        switch (outcome)
        {
            case AiRestoreRevocationOutcome.Reconciled:
            case AiRestoreRevocationOutcome.AlreadyReconciled:
                return outcome;

            case AiRestoreRevocationOutcome.NotReady:
                throw new InvalidOperationException(
                    "AI recovery reconciliation is not ready.");

            default:
                throw new InvalidOperationException(
                    "AI recovery reconciliation could not be verified.");
        }
    }
}
