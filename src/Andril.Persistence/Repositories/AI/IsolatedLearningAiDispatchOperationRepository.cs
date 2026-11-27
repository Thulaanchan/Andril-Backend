using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Services;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.AI;

internal sealed class IsolatedAiDispatchOperationRow
{
    public Guid AccountId { get; set; }
    public Guid ModuleProfileId { get; set; }
    public AiModule Module { get; set; }
    public Guid OperationId { get; set; }
    public Guid AcknowledgementGeneration { get; set; }
    public long AcknowledgementVersion { get; set; }
    public string PolicyVersion { get; set; } = "";
    public AiDispatchOperationState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public override string ToString() => "AI operation journal [private bindings omitted]";
}

/// <summary>
/// Unregistered isolated TD-04 journal. All operations use Account/Membership/Profile locking
/// and the same serializable transaction as acknowledgement revocation. No external calls occur.
/// This is not atomic conversation persistence or a transport/dispatch revocation fence.
/// </summary>
internal sealed class IsolatedLearningAiDispatchOperationRepository : IAiDispatchOperationRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly IAiPrivacyPolicyVersionSource policies;
    private readonly AiPrivacyNoticeGuard privacy;
    private readonly TimeProvider clock;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }

    internal IsolatedLearningAiDispatchOperationRepository(DbContextOptions<LearningDbContext> options,
        ICurrentUser user, IPrivateModuleAccess access, IPrivateProfileService profiles,
        IAiPrivacyPolicyVersionSource policies, AiPrivacyNoticeGuard privacy, TimeProvider clock)
    {
        boundary = new(options, user, access, profiles); this.policies = policies; this.privacy = privacy; this.clock = clock;
    }

    private enum Action { Prepare, Claim, ValidateRelease, Cancel, Indeterminate, Reconcile, Complete }
    public Task<AiDispatchJournalResult> PrepareAsync(AiAuthorizedActor actor, Guid operationId,
        AiAcknowledgementRevision reviewedAcknowledgement, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Prepare, reviewedAcknowledgement, cancellationToken);
    public Task<AiDispatchJournalResult> ClaimAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Claim, null, cancellationToken);
    public Task<AiDispatchJournalResult> ValidateForReleaseAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.ValidateRelease, null, cancellationToken);
    public Task<AiDispatchJournalResult> CancelAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Cancel, null, cancellationToken);
    public Task<AiDispatchJournalResult> MarkIndeterminateAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Indeterminate, null, cancellationToken);
    public Task<AiDispatchJournalResult> ReconcileAsync(AiAuthorizedActor actor, Guid operationId, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Reconcile, null, cancellationToken);
    public Task<AiDispatchJournalResult> CompleteConversationResultAsync(AiAuthorizedActor actor, Guid operationId,
        Guid conversationId, string assistantText, CancellationToken cancellationToken = default)
        => Run(actor, operationId, Action.Complete, null, cancellationToken, conversationId, assistantText);

    private async Task<AiDispatchJournalResult> Run(AiAuthorizedActor actor, Guid operationId, Action action,
        AiAcknowledgementRevision? reviewed, CancellationToken ct, Guid? conversationId = null, string? assistantText = null)
    {
        ct.ThrowIfCancellationRequested();
        if (actor is null || actor.Module != AiModule.Learning || actor.UserId == Guid.Empty || actor.ModuleProfileId == Guid.Empty)
            return new(AiDispatchJournalOutcome.Denied);
        if (operationId == Guid.Empty || (action == Action.Prepare && (reviewed is null || reviewed.Generation == Guid.Empty || reviewed.Version < 1)))
            return new(AiDispatchJournalOutcome.Conflict);
        if (action == Action.Complete && (conversationId is null || conversationId == Guid.Empty || string.IsNullOrWhiteSpace(assistantText)))
            return new(AiDispatchJournalOutcome.Conflict);
        var write = action is not (Action.ValidateRelease or Action.Reconcile);
        var protectedAction = action is Action.Prepare or Action.Claim or Action.ValidateRelease or Action.Complete;
        LearningDbContext? transactionContext = null;
        AiAcknowledgementRevision? lease = null;
        string? policyVersion = null;
        AiConversationEntity? completedConversation = null;

        return await boundary.Run(async (parent, owner, token) =>
        {
            transactionContext = parent;
            var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
            if (owner.Profile != actor.ModuleProfileId || account != actor.UserId.ToString()) throw new ProfileAccessDeniedException();
            await using var db = Open(parent);
            await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), token);
            var row = await db.Set<IsolatedAiDispatchOperationRow>().SingleOrDefaultAsync(x =>
                x.AccountId == actor.UserId && x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning && x.OperationId == operationId, token);
            if (protectedAction)
            {
                await RequirePrivacyAsync(actor, token);
                var ack = await db.Set<IsolatedAiAcknowledgementRow>().SingleOrDefaultAsync(x =>
                    x.AccountId == actor.UserId && x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning, token);
                if (ack is null || !ack.Acknowledged) return new AiDispatchJournalResult(AiDispatchJournalOutcome.Denied);
                policyVersion = await policies.ReadCurrentVersionAsync(actor, token);
                if (string.IsNullOrWhiteSpace(policyVersion)) return new(AiDispatchJournalOutcome.Unavailable);
                if (policyVersion != ack.PolicyVersion) return new(AiDispatchJournalOutcome.Denied);
                lease = new(ack.Generation, ack.Version);
                var expected = action == Action.Prepare ? reviewed : row is null ? null : new AiAcknowledgementRevision(row.AcknowledgementGeneration, row.AcknowledgementVersion);
                if (expected != lease) return new(AiDispatchJournalOutcome.Conflict);
            }
            if (action == Action.Prepare)
            {
                if (row is not null)
                    return row.State == AiDispatchOperationState.Prepared && row.AcknowledgementGeneration == reviewed!.Generation && row.AcknowledgementVersion == reviewed.Version
                        ? new(AiDispatchJournalOutcome.Saved, Snapshot(row)) : new(AiDispatchJournalOutcome.Conflict);
                row = new() { AccountId = actor.UserId, ModuleProfileId = actor.ModuleProfileId, Module = AiModule.Learning,
                    OperationId = operationId, AcknowledgementGeneration = lease!.Generation, AcknowledgementVersion = lease.Version,
                    PolicyVersion = policyVersion!, State = AiDispatchOperationState.Prepared, CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow() };
                db.Add(row);
            }
            else
            {
                if (row is null) return new(AiDispatchJournalOutcome.Available);
                if (action == Action.Reconcile) return new(AiDispatchJournalOutcome.Available, Snapshot(row));
                if (action == Action.ValidateRelease)
                    return row.State == AiDispatchOperationState.Claimed ? new(AiDispatchJournalOutcome.Available, Snapshot(row)) : new(AiDispatchJournalOutcome.Conflict);
                if (action == Action.Complete)
                {
                    if (row.State != AiDispatchOperationState.Claimed) return new(AiDispatchJournalOutcome.Conflict);
                    var conversation = await db.Set<AiConversationEntity>().SingleOrDefaultAsync(x =>
                        x.Id == conversationId && x.AccountId == actor.UserId && x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning, token);
                    if (conversation is null || !conversation.IsAccessibleBy(actor, clock.GetUtcNow())) return new(AiDispatchJournalOutcome.Denied);
                    var messages = db.Set<AiMessageEntity>().Where(x => x.AccountId == actor.UserId &&
                        x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning && x.OperationId == operationId);
                    if (!await messages.AnyAsync(x => x.ConversationId == conversationId && x.Role == AiStoredMessageRole.User, token) ||
                        await messages.AnyAsync(x => x.Role == AiStoredMessageRole.Assistant, token)) return new(AiDispatchJournalOutcome.Conflict);
                    db.Add(AiMessageEntity.CreateAssistantMessage(conversation, actor, operationId, assistantText, clock.GetUtcNow()));
                    completedConversation = conversation;
                }
                if (action == Action.Claim && row.State != AiDispatchOperationState.Prepared) return new(AiDispatchJournalOutcome.Conflict);
                if (action == Action.Indeterminate && row.State != AiDispatchOperationState.Claimed) return new(AiDispatchJournalOutcome.Conflict);
                if (action == Action.Cancel && row.State is AiDispatchOperationState.Indeterminate or AiDispatchOperationState.Completed) return new(AiDispatchJournalOutcome.Conflict);
                row.State = action switch { Action.Claim => AiDispatchOperationState.Claimed, Action.Indeterminate => AiDispatchOperationState.Indeterminate,
                    Action.Complete => AiDispatchOperationState.Completed, _ => AiDispatchOperationState.Cancelled };
                row.UpdatedAtUtc = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(token);
            if (TestCheckpoint is not null) await TestCheckpoint("prepared", token);
            return new(AiDispatchJournalOutcome.Saved, Snapshot(row));
        }, failure => new AiDispatchJournalResult(failure switch
        {
            LearningDocumentFailure.Denied => AiDispatchJournalOutcome.Denied,
            LearningDocumentFailure.Conflict => AiDispatchJournalOutcome.Conflict,
            LearningDocumentFailure.CommitUnknown => AiDispatchJournalOutcome.CommitUnknown,
            _ => AiDispatchJournalOutcome.Unavailable
        }), ct, write, async (phase, token) =>
        {
            if (TestCheckpoint is not null) await TestCheckpoint(phase, token);
            if (protectedAction && lease is not null && phase is "before-commit" or "read-release")
            {
                await RequirePrivacyAsync(actor, token);
                if (await policies.ReadCurrentVersionAsync(actor, token) != policyVersion) throw new ProfileContractPendingException();
                await using var db = Open(transactionContext!);
                await db.Database.UseTransactionAsync(transactionContext!.Database.CurrentTransaction!.GetDbTransaction(), token);
                var ack = await db.Set<IsolatedAiAcknowledgementRow>().SingleOrDefaultAsync(x => x.AccountId == actor.UserId && x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning, token);
                if (ack is null || !ack.Acknowledged || ack.Generation != lease.Generation || ack.Version != lease.Version) throw new ProfileAccessDeniedException();
                if (completedConversation is not null && !completedConversation.IsAccessibleBy(actor, clock.GetUtcNow())) throw new ProfileAccessDeniedException();
            }
        });
    }

    // Translate only the privacy guard's documented denial into the transaction boundary's
    // existing denial contract. The boundary disposes/rolls back before returning Denied.
    // Database, policy-source, checkpoint and other operations remain outside this catch.
    private async Task RequirePrivacyAsync(AiAuthorizedActor actor, CancellationToken ct)
    {
        try { await privacy.RequireSameActorSatisfiedAsync(actor, ct); }
        catch (UnauthorizedAccessException) { throw new ProfileAccessDeniedException(); }
    }

    private static IsolatedLearningAiDbContext Open(LearningDbContext parent)
        => new(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>().UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
    private static AiDispatchOperationSnapshot Snapshot(IsolatedAiDispatchOperationRow row)
        => new(row.OperationId, row.State, new(row.AcknowledgementGeneration, row.AcknowledgementVersion));
}
