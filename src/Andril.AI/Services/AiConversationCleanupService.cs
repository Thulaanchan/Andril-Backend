
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Services;

/// <summary>
/// Aggregate result of one successfully completed maintenance batch.
/// Deferred or missing candidates are not counted as purged.
/// No private content or individual identifiers are returned.
/// </summary>
public sealed record AiConversationCleanupBatchResult(
    int CandidateCount,
    int PurgedCount,
    int NotEligibleOrMissingCount,
    int DeferredCount);

/// <summary>
/// One bounded cleanup pass. Not a hosted worker or HTTP endpoint.
/// The repository must enforce worker permissions, fresh transactional
/// eligibility, legal retention rules and verified durable removal.
/// Do not register or schedule in production until those gates are met.
/// </summary>
public sealed class AiConversationCleanupService
{
    private readonly IAiConversationCleanupRepository _repository;
    private readonly TimeProvider _clock;

    public AiConversationCleanupService(
        IAiConversationCleanupRepository repository,
        TimeProvider? clock = null)
    {
        _repository = repository ??
            throw new ArgumentNullException(nameof(repository));

        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AiConversationCleanupBatchResult> RunBatchAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (limit is < 1 or > IAiConversationCleanupRepository.MaxBatchSize)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var pending = await _repository.ListPendingAsync(
            limit, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (pending is null || pending.Count > limit)
            throw InvalidBatch();

        // Validate the entire snapshot before requesting any purge.
        // A candidate is a hint, not proof of authorization or eligibility.
        var candidates = pending.ToArray();
        var now = _clock.GetUtcNow().ToUniversalTime();

        if (now == default || candidates.Length > limit)
            throw InvalidBatch();

        var seen = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (candidate is null ||
                candidate.ConversationId == Guid.Empty ||
                candidate.AccountId == Guid.Empty ||
                candidate.ModuleProfileId == Guid.Empty ||
                !Enum.IsDefined(candidate.Module) ||
                candidate.RevokedAtUtc == default ||
                candidate.RevokedAtUtc.Offset != TimeSpan.Zero ||
                candidate.PurgeTargetUtc.Offset != TimeSpan.Zero ||
                candidate.RevokedAtUtc > now ||
                candidate.PurgeTargetUtc - candidate.RevokedAtUtc !=
                    AiConversationRetentionPolicy.ActiveDataPurgeTarget ||
                !seen.Add(candidate.ConversationId))
            {
                throw InvalidBatch();
            }
        }

        var purged = 0;
        var unavailable = 0;
        var deferred = 0;

        // Start cleanup promptly: the 24-hour target is a deadline,
        // not a minimum waiting period. Earliest deadlines go first.
        foreach (var candidate in candidates
            .OrderBy(c => c.PurgeTargetUtc)
            .ThenBy(c => c.ConversationId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await _repository.TryPurgeAsync(
                candidate, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            switch (outcome)
            {
                case AiConversationCleanupOutcome.Purged:
                    purged++;
                    break;

                case AiConversationCleanupOutcome.NotEligibleOrMissing:
                    unavailable++;
                    break;

                case AiConversationCleanupOutcome.DeferredByRetentionPolicy:
                    deferred++;
                    break;

                default:
                    throw new InvalidOperationException(
                        "AI cleanup outcome could not be verified.");
            }
        }

        // Storage/authorization errors and cancellation propagate.
        // Earlier purges may already be durable if a later call fails.
        // Never report a failed or uncertain batch as successful.
        return new AiConversationCleanupBatchResult(
            candidates.Length, purged, unavailable, deferred);
    }

    private static InvalidOperationException InvalidBatch() =>
        new("AI cleanup candidate batch is invalid.");
}
