using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.AI.Services;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.AI;

/// <summary>Unregistered Learning-only conversation store for disposable PostgreSQL. Joins
/// the existing Learning owner transaction and reuses the AI privacy guard. Actor arguments
/// are comparison bindings, never credentials. No generation, provider dispatch, public HTTP,
/// production schema/history, quota, new retention or AI tool permission is introduced.</summary>
internal sealed class IsolatedLearningAiConversationRepository : IAiConversationRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly AiPrivacyNoticeGuard privacy;
    private readonly TimeProvider clock;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal IsolatedLearningAiConversationRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles, AiPrivacyNoticeGuard privacy, TimeProvider clock)
    { boundary = new(options, user, access, profiles); this.privacy = privacy; this.clock = clock; }
    private sealed class Lease
    {
        internal readonly List<AiConversationEntity> Touched = [];
        internal AiConversationEntity Track(AiConversationEntity c) { Touched.Add(c); return c; }
    }
    private sealed class CommitUnknownException() : InvalidOperationException("AI persistence outcome unknown; do not replay automatically.");
    private async Task<T> Run<T>(AiAuthorizedActor actor, bool write, T closed,
        Func<IsolatedLearningAiDbContext, Lease, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (actor is null || actor.Module != AiModule.Learning || actor.UserId == Guid.Empty || actor.ModuleProfileId == Guid.Empty)
        { if (!write) return closed; throw new UnauthorizedAccessException("AI conversation unavailable."); }
        var lease = new Lease();
        try
        {
            await privacy.RequireSameActorSatisfiedAsync(actor, ct);
            return await boundary.Run(async (parent, owner, token) =>
            {
                var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                if (owner.Profile != actor.ModuleProfileId || account != actor.UserId.ToString()) throw new UnauthorizedAccessException("AI conversation unavailable.");
                await privacy.RequireSameActorSatisfiedAsync(actor, token);
                await using var db = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>().UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), token);
                return await action(db, lease, token);
            }, f =>
            {
                if (!write) return closed;
                if (f == LearningDocumentFailure.Denied) throw new UnauthorizedAccessException("AI conversation unavailable.");
                if (f == LearningDocumentFailure.CommitUnknown) throw new CommitUnknownException();
                throw new InvalidOperationException("AI persistence unavailable.");
            }, ct, write, async (phase, token) =>
            {
                if (TestCheckpoint is not null) await TestCheckpoint(phase, token);
                await privacy.RequireSameActorSatisfiedAsync(actor, token);
                if (lease.Touched.Any(c => !c.IsAccessibleBy(actor, clock.GetUtcNow()))) throw new UnauthorizedAccessException("AI conversation unavailable.");
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CommitUnknownException) { throw; }
        catch (UnauthorizedAccessException) { if (!write) return closed; throw new UnauthorizedAccessException("AI conversation unavailable."); }
        catch (Exception e) when (e is InvalidOperationException or NpgsqlException or DbUpdateException or IOException or TimeoutException)
        { if (!write) return closed; throw new InvalidOperationException("AI persistence unavailable."); }
    }
    private static IQueryable<AiConversationEntity> Conversations(IsolatedLearningAiDbContext db, AiAuthorizedActor actor) => db.Set<AiConversationEntity>().Where(c => c.AccountId == actor.UserId && c.ModuleProfileId == actor.ModuleProfileId && c.Module == AiModule.Learning);
    private static IQueryable<AiMessageEntity> Messages(IsolatedLearningAiDbContext db, AiAuthorizedActor actor) => db.Set<AiMessageEntity>().Where(c => c.AccountId == actor.UserId && c.ModuleProfileId == actor.ModuleProfileId && c.Module == AiModule.Learning);
    private static AiStoredConversation Stored(AiConversationEntity c) => new(c.Id, c.AccountId, c.ModuleProfileId, c.Module, c.CreatedAtUtc, c.LastSavedUserMessageAtUtc, c.ExpiresAtUtc);
    private static void ValidateInput(Guid operationId, string text)
    {
        var request = new AiChatSubmissionRequest(operationId, text);
        if (request.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(request)).Any()) throw new ArgumentException("AI user input invalid.");
    }
    private static void ValidateLimit(int limit)
    { if (limit < 1 || limit > AiConversationContextPolicy.MaxCandidateMessages) throw new ArgumentOutOfRangeException(nameof(limit)); }
    private Task Prepared(CancellationToken ct) => TestCheckpoint?.Invoke("prepared", ct) ?? Task.CompletedTask;
    private async Task<AiConversationEntity> Required(IsolatedLearningAiDbContext db, AiAuthorizedActor actor, Guid id, Lease lease, CancellationToken ct)
    {
        var c = await Conversations(db, actor).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !c.IsAccessibleBy(actor, clock.GetUtcNow())) throw new UnauthorizedAccessException("AI conversation unavailable.");
        return lease.Track(c);
    }
    public Task<AiStoredConversation> CreateWithUserMessageAsync(AiAuthorizedActor actor, Guid operationId, string validatedUserText, CancellationToken ct = default)
    {
        ValidateInput(operationId, validatedUserText);
        return Run<AiStoredConversation>(actor, true, null!, async (db, lease, token) =>
        {
            var existing = await Conversations(db, actor).SingleOrDefaultAsync(c => c.CreateOperationId == operationId, token);
            if (existing is not null)
            {
                if (!existing.IsAccessibleBy(actor, clock.GetUtcNow())) throw new UnauthorizedAccessException("AI conversation unavailable.");
                var saved = await Messages(db, actor).SingleAsync(m => m.OperationId == operationId && m.Role == AiStoredMessageRole.User, token);
                if (saved.Text != validatedUserText.Trim()) throw new InvalidOperationException("AI operation conflict.");
                return Stored(lease.Track(existing));
            }
            if (await Messages(db, actor).AnyAsync(m => m.OperationId == operationId, token)) throw new InvalidOperationException("AI operation conflict.");
            var now = clock.GetUtcNow(); var c = lease.Track(AiConversationEntity.CreateForFirstUserMessage(actor, operationId, now));
            db.Add(c); db.Add(AiMessageEntity.CreateUserMessage(c, actor, operationId, validatedUserText, now)); await db.SaveChangesAsync(token); await Prepared(token); return Stored(c);
        }, ct);
    }
    public Task<AiStoredConversation?> FindAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken ct = default) =>
        Run<AiStoredConversation?>(actor, false, null, async (db, lease, token) => Stored(await Required(db, actor, conversationId, lease, token)), ct);
    public Task<IReadOnlyList<AiStoredConversation>> ListAsync(AiAuthorizedActor actor, int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        return Run<IReadOnlyList<AiStoredConversation>>(actor, false, [], async (db, lease, token) =>
        {
            var now = clock.GetUtcNow(); var list = await Conversations(db, actor).Where(c => c.DeletedAtUtc == null && c.ExpiresAtUtc > now).OrderByDescending(c => c.LastSavedUserMessageAtUtc).ThenBy(c => c.Id).Take(limit).ToListAsync(token);
            return list.Select(c => Stored(lease.Track(c))).ToArray();
        }, ct);
    }
    public Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(AiAuthorizedActor actor, Guid conversationId, int limit, CancellationToken ct = default)
    {
        ValidateLimit(limit);
        return Run<IReadOnlyList<AiStoredAiMessage>>(actor, false, [], async (db, lease, token) =>
        {
            await Required(db, actor, conversationId, lease, token);
            var messages = await Messages(db, actor).Where(m => m.ConversationId == conversationId).OrderByDescending(m => EF.Property<long>(m, "StoreSequence")).Take(limit).ToListAsync(token);
            messages.Reverse(); return messages.Select(m => m.ToStoredMessage()).ToArray();
        }, ct);
    }
    public Task<AiStoredAiMessage> AppendUserMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string validatedUserText, CancellationToken ct = default)
    {
        ValidateInput(operationId, validatedUserText);
        return Run<AiStoredAiMessage>(actor, true, null!, async (db, lease, token) =>
        {
            var c = await Required(db, actor, conversationId, lease, token);
            var old = await Messages(db, actor).SingleOrDefaultAsync(m => m.OperationId == operationId && m.Role == AiStoredMessageRole.User, token);
            if (old is not null) { if (old.ConversationId != conversationId || old.Text != validatedUserText.Trim()) throw new InvalidOperationException("AI operation conflict."); return old.ToStoredMessage(); }
            var now = clock.GetUtcNow(); var message = AiMessageEntity.CreateUserMessage(c, actor, operationId, validatedUserText, now); c.ApplySavedUserMessage(now);
            db.Add(message); await db.SaveChangesAsync(token); await Prepared(token); return message.ToStoredMessage();
        }, ct);
    }
    public Task<AiStoredAiMessage> AppendAssistantMessageAsync(AiAuthorizedActor actor, Guid conversationId, Guid operationId, string assistantText, CancellationToken ct = default) =>
        Run<AiStoredAiMessage>(actor, true, null!, async (db, lease, token) =>
        {
            var c = await Required(db, actor, conversationId, lease, token);
            if (!await Messages(db, actor).AnyAsync(m => m.ConversationId == conversationId && m.OperationId == operationId && m.Role == AiStoredMessageRole.User, token)) throw new InvalidOperationException("AI operation unavailable.");
            var old = await Messages(db, actor).SingleOrDefaultAsync(m => m.OperationId == operationId && m.Role == AiStoredMessageRole.Assistant, token);
            if (old is not null) { if (old.ConversationId != conversationId || old.Text != assistantText) throw new InvalidOperationException("AI operation conflict."); return old.ToStoredMessage(); }
            var message = AiMessageEntity.CreateAssistantMessage(c, actor, operationId, assistantText, clock.GetUtcNow()); db.Add(message);
            await db.SaveChangesAsync(token); await Prepared(token); return message.ToStoredMessage();
        }, ct);
    public async Task DeleteAsync(AiAuthorizedActor actor, Guid conversationId, CancellationToken ct = default) =>
        await Run(actor, true, false, async (db, _, token) =>
        {
            var c = await Conversations(db, actor).SingleOrDefaultAsync(x => x.Id == conversationId, token);
            if (c is null || c.DeletedAtUtc is not null) return false;
            c.MarkDeleted(clock.GetUtcNow()); db.Add(new IsolatedAiPurgeWork { ConversationId = c.Id, AccountId = c.AccountId, ModuleProfileId = c.ModuleProfileId, Module = c.Module, RequestedAtUtc = c.DeletedAtUtc!.Value, PurgeByUtc = AiConversationRetentionPolicy.GetManualDeletionPurgeTargetUtc(c)!.Value });
            await db.SaveChangesAsync(token); await Prepared(token); return true;
        }, ct);
}
