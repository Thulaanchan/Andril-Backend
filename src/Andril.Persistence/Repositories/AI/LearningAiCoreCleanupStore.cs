using System.Data;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.AI;

public enum AiCoreCleanupOutcome { Unavailable, NotEligibleOrMissing, Deferred, CoreRemoved }

/// <summary>
/// Unregistered production-schema primitive, not a claim of whole-system purge completion.
/// Requires separately provisioned maintenance DELETE permissions and a real worker/retention
/// authority. Ordinary runtime grants deliberately cannot execute it. Existing FORCE RLS and
/// Learning lifecycle authority remain in force; inactive-profile maintenance is unsupported.
/// Never returns IAiConversationCleanupRepository.Purged for unverified external stores.
/// Options must supply a connection used exclusively by this call, never a shared live session.
/// </summary>
public sealed class LearningAiCoreCleanupStore(
    DbContextOptions<LearningAiDbContext> options,
    IAiCoreCleanupAuthority authority,
    TimeProvider clock)
{
    public Task<AiCoreCleanupOutcome> TryRemoveAsync(AiConversationCleanupCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        return authority.WithWorkerScopeAsync(async (scope, ct) =>
        {
            if (scope.Subject == Guid.Empty || scope.AccountId == Guid.Empty ||
                scope.LearningProfileId == Guid.Empty || candidate.Module != AiModule.Learning ||
                candidate.AccountId != scope.AccountId || candidate.ModuleProfileId != scope.LearningProfileId ||
                candidate.ConversationId == Guid.Empty)
                return AiCoreCleanupOutcome.NotEligibleOrMissing;

            await using var db = new LearningAiDbContext(options);
            await db.Database.OpenConnectionAsync(ct);
            var allowed = await db.Database.SqlQueryRaw<bool>("""
                SELECT has_table_privilege(current_user,'andril_ai.conversations','DELETE')
                   AND has_table_privilege(current_user,'andril_ai.messages','DELETE')
                   AND has_table_privilege(current_user,'andril_ai.purge_work','DELETE') AS "Value"
                """).SingleAsync(ct);
            if (!allowed) return AiCoreCleanupOutcome.Unavailable;
            // Same subject fence and account -> membership -> profile lock order as the
            // existing Learning document boundary. Acquire the fence before the snapshot.
            try
            {
                // Cover acquisition itself: cancellation can arrive after the server has
                // acquired a session lock but before the client observes completion.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_lock(hashtextextended({scope.Subject.ToString()},0))", ct);
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('andril.subject',{scope.Subject.ToString()},true),set_config('andril.module','learning',true)", ct);
                if (!await db.Database.SqlQuery<bool>(
                    $"SELECT andril_learning.lock_cv_owner({scope.Subject},{scope.LearningProfileId}) AS \"Value\"").SingleAsync(ct))
                    return AiCoreCleanupOutcome.NotEligibleOrMissing;
                var account = await db.Database.SqlQueryRaw<string>(
                    "SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
                if (account != scope.AccountId.ToString()) return AiCoreCleanupOutcome.NotEligibleOrMissing;

                var rows = await db.Set<AiConversationEntity>().FromSqlInterpolated($"""
                    SELECT * FROM andril_ai.conversations
                    WHERE id={candidate.ConversationId} AND account_id={scope.AccountId}
                      AND module_profile_id={scope.LearningProfileId} AND module='Learning'
                    """).ToListAsync(ct);
                var conversation = rows.SingleOrDefault();
                var now = clock.GetUtcNow();
                if (conversation is null || AiConversationRetentionPolicy.Classify(conversation, now) == AiConversationRetentionState.Active)
                    return AiCoreCleanupOutcome.NotEligibleOrMissing;
                var revoked = conversation.DeletedAtUtc is { } deleted && deleted < conversation.ExpiresAtUtc
                    ? deleted : conversation.ExpiresAtUtc;
                var target = AiConversationRetentionPolicy.GetActivePurgeTargetUtc(conversation, now);
                if (candidate.RevokedAtUtc != revoked || candidate.PurgeTargetUtc != target)
                    return AiCoreCleanupOutcome.NotEligibleOrMissing;
                if (!await authority.MayRemoveCoreAsync(candidate, ct)) return AiCoreCleanupOutcome.Deferred;

                await db.Set<AiMessageEntity>().Where(x => x.ConversationId == conversation.Id &&
                    x.AccountId == scope.AccountId && x.ModuleProfileId == scope.LearningProfileId && x.Module == AiModule.Learning)
                    .ExecuteDeleteAsync(ct);
                await db.Set<IsolatedAiPurgeWork>().Where(x => x.ConversationId == conversation.Id &&
                    x.AccountId == scope.AccountId && x.ModuleProfileId == scope.LearningProfileId && x.Module == AiModule.Learning)
                    .ExecuteDeleteAsync(ct);
                var count = await db.Set<AiConversationEntity>().Where(x => x.Id == conversation.Id &&
                    x.AccountId == scope.AccountId && x.ModuleProfileId == scope.LearningProfileId && x.Module == AiModule.Learning)
                    .ExecuteDeleteAsync(ct);
                if (count != 1) throw new InvalidOperationException("AI core cleanup requires reconciliation.");
                // The authority must keep its revocation protocol around the entire callback.
                // A last-moment legal hold/coverage change rolls back every deletion.
                if (!await authority.MayRemoveCoreAsync(candidate, ct)) return AiCoreCleanupOutcome.Deferred;
                ct.ThrowIfCancellationRequested();
                await tx.CommitAsync(ct);
                return AiCoreCleanupOutcome.CoreRemoved;
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock(hashtextextended({scope.Subject.ToString()},0))", CancellationToken.None);
            }
        }, cancellationToken);
    }
}
