
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;

namespace Andril.AI.Services;

/// <summary>
/// Internal orchestration for AI-PRIV-01 account deletion cleanup.
/// Does not confirm account deletion, revoke accounts, or purge data.
/// The repository must independently verify service authorization and
/// the authoritative M2 deletion event on every operation.
/// Do not expose or register this service in production until those
/// integration and privacy gates are verified.
/// </summary>
public sealed class AiAccountDeletionService
{
    private readonly IAiAccountDeletionRepository _repository;

    public AiAccountDeletionService(
        IAiAccountDeletionRepository repository)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// Request durable cleanup scheduling for an already confirmed
    /// account deletion. Neither identifier is an authorization credential.
    /// A successful outcome acknowledges scheduling, not physical erasure.
    /// </summary>
    public async Task<AiAccountDeletionScheduleOutcome>
        EnsureCleanupScheduledAsync(
            Guid accountId,
            Guid deletionEventId,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (accountId == Guid.Empty)
            throw new ArgumentException(
                "An account identifier is required.", nameof(accountId));

        if (deletionEventId == Guid.Empty)
            throw new ArgumentException(
                "A deletion event identifier is required.",
                nameof(deletionEventId));

        // The repository independently verifies worker permissions,
        // event ownership and confirmation against authoritative state.
        // The original deadline must survive retries and reconciliation.
        var outcome = await _repository.EnsureCleanupScheduledAsync(
            accountId, deletionEventId, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        switch (outcome)
        {
            case AiAccountDeletionScheduleOutcome.Scheduled:
            case AiAccountDeletionScheduleOutcome.AlreadyRecorded:
                return outcome;

            case AiAccountDeletionScheduleOutcome.NotConfirmedOrMissing:
                throw new UnauthorizedAccessException(
                    "AI account deletion cleanup is not authorized.");

            default:
                throw new InvalidOperationException(
                    "AI account deletion scheduling could not be verified.");
        }
    }
}
