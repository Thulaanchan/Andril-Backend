using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.AI;

/// <summary>DORMANT preparation only: NonController excludes this type from default discovery,
/// irrespective of AddControllers/MapControllers, its name/base class or any live-enable flag.
/// No route attributes or activation registration. Removing this protection requires explicit
/// review of confirmed identity middleware coverage, authoritative adapters/repositories and
/// Section 05 endpoint tests. Direct calls still delegate to freshly scoped application services.
/// No start/submit/retry/cancel API until admission, dispatch and delivery contracts are approved.</summary>
[NonController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AiChatController : ControllerBase
{
    private readonly AiConversationLifecycleService _conversations;
    private readonly AiOperationService _operations;
    private readonly IAiActorAuthorization _authorization;
    private readonly TimeProvider _clock;

    public AiChatController(AiConversationLifecycleService conversations, AiOperationService operations,
        IAiActorAuthorization authorization, TimeProvider? clock = null)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ActionResult<AiHistoryPage<AiConversationSummary>>> ListAsync(string module, int limit,
        CancellationToken cancellationToken)
    {
        var identity = ParseModule(module); RequireLimit(limit);
        var actor = await ResolveAsync(identity, cancellationToken);
        var rows = await _conversations.ListConversationsAsync(identity, limit, cancellationToken);
        await RequireSameAsync(actor, cancellationToken);
        if (rows is null || rows.Count > limit || rows.Select(r => r?.ConversationId).Distinct().Count() != rows.Count)
            throw Unverified();
        var now = _clock.GetUtcNow();
        if (rows.Any(r => !ValidConversation(r, actor, now))) throw Unverified();
        return Ok(new AiHistoryPage<AiConversationSummary>(rows.Select(r =>
            new AiConversationSummary(r.ConversationId, r.CreatedAtUtc, r.LastSavedUserMessageAtUtc, r.ExpiresAtUtc))));
    }

    public async Task<ActionResult<AiHistoryPage<AiHistoryMessage>>> ReadMessagesAsync(string module,
        Guid conversationId, int limit, CancellationToken cancellationToken)
    {
        var identity = ParseModule(module); RequireId(conversationId); RequireLimit(limit);
        var actor = await ResolveAsync(identity, cancellationToken);
        var conversation = await _conversations.FindConversationAsync(identity, conversationId, cancellationToken);
        if (!ValidConversation(conversation, actor, _clock.GetUtcNow()) || conversation!.ConversationId != conversationId)
            throw new UnauthorizedAccessException("AI conversation access is unavailable.");
        var rows = await _conversations.ReadRecentMessagesAsync(identity, conversationId, limit, cancellationToken);
        await RequireSameAsync(actor, cancellationToken);
        var now = _clock.GetUtcNow();
        if (!ValidConversation(conversation, actor, now) || rows is null || rows.Count > limit) throw Unverified();
        var ids = new HashSet<Guid>();
        var previous = conversation.CreatedAtUtc;
        foreach (var row in rows)
        {
            if (row is null || row.ConversationId != conversationId || row.MessageId == Guid.Empty ||
                !ids.Add(row.MessageId) || !Enum.IsDefined(row.Role) || string.IsNullOrWhiteSpace(row.Text) ||
                !AiOperationPolicy.ValidUtc(row.SavedAtUtc) || row.SavedAtUtc < previous ||
                row.SavedAtUtc > now || row.SavedAtUtc >= conversation.ExpiresAtUtc) throw Unverified();
            previous = row.SavedAtUtc;
        }
        return Ok(new AiHistoryPage<AiHistoryMessage>(rows.Select(r => new AiHistoryMessage(r.MessageId,
            r.Role == AiStoredMessageRole.User ? "user" : "assistant", r.Text, r.SavedAtUtc))));
    }

    /// <summary>Existing service reconciliation may update authoritative accounting; this is
    /// not specified as HTTP GET and no route/verb contract is activated. No redispatch.</summary>
    public async Task<ActionResult<AiPublicOperationStatus>> ReconcileOperationAsync(string module,
        Guid conversationId, Guid operationId, CancellationToken cancellationToken)
    {
        var identity = ParseModule(module); RequireId(conversationId); RequireId(operationId);
        var actor = await ResolveAsync(identity, cancellationToken);
        var reference = new AiOperationReference(actor, conversationId, operationId);
        var result = await _operations.ReconcileAsync(reference, cancellationToken);
        await RequireSameAsync(actor, cancellationToken);
        // No distinction between missing, foreign or expired references, and no internal state
        // for denied outcomes. Snapshot shape/ownership is revalidated by the service.
        if (result.Outcome != AiOperationOutcome.AuthorizedStatus || result.Snapshot?.Operation != reference)
            throw new UnauthorizedAccessException("AI operation access is unavailable.");
        var status = result.Snapshot.Status switch
        {
            AiOperationStatus.Reserved => "reserved", AiOperationStatus.InProgress => "in_progress",
            AiOperationStatus.PendingReconciliation => "pending_reconciliation", AiOperationStatus.Completed => "completed",
            AiOperationStatus.ConfirmedFailed => "confirmed_failed", AiOperationStatus.Cancelled => "cancelled",
            _ => throw Unverified()
        };
        return Ok(new AiPublicOperationStatus(operationId, status));
    }

    public async Task<IActionResult> DeleteAsync(string module, Guid conversationId, CancellationToken cancellationToken)
    {
        var identity = ParseModule(module); RequireId(conversationId);
        var actor = await ResolveAsync(identity, cancellationToken);
        await _conversations.DeleteConversationAsync(identity, conversationId, cancellationToken);
        await RequireSameAsync(actor, cancellationToken);
        // Repository contract is idempotent and must not reveal inaccessible row existence.
        // No claim of backup/provider erasure or cancellation of provider execution.
        return NoContent();
    }

    private async Task<AiAuthorizedActor> ResolveAsync(AiModule module, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actor = await _authorization.GetAuthorizedActorAsync(module, token);
        token.ThrowIfCancellationRequested();
        if (!AiOperationPolicy.ValidActor(actor) || actor!.Module != module)
            throw new UnauthorizedAccessException("AI module access is unavailable.");
        return actor;
    }
    private async Task RequireSameAsync(AiAuthorizedActor expected, CancellationToken token)
    {
        if (await ResolveAsync(expected.Module, token) != expected)
            throw new UnauthorizedAccessException("AI module access is unavailable.");
    }
    private static bool ValidConversation(AiStoredConversation? r, AiAuthorizedActor actor, DateTimeOffset now) =>
        r is not null && r.ConversationId != Guid.Empty && r.AccountId == actor.UserId &&
        r.ModuleProfileId == actor.ModuleProfileId && r.Module == actor.Module &&
        AiOperationPolicy.ValidUtc(now) && AiOperationPolicy.ValidUtc(r.CreatedAtUtc) &&
        AiOperationPolicy.ValidUtc(r.LastSavedUserMessageAtUtc) && AiOperationPolicy.ValidUtc(r.ExpiresAtUtc) &&
        r.CreatedAtUtc <= r.LastSavedUserMessageAtUtc && r.LastSavedUserMessageAtUtc <= now && now < r.ExpiresAtUtc &&
        r.ExpiresAtUtc - r.LastSavedUserMessageAtUtc == AiPrototypeLimits.Duration;
    private static AiModule ParseModule(string module) => module switch
    {
        "learning" => AiModule.Learning, "hobbies" => AiModule.Hobbies, "social" => AiModule.Social,
        _ => throw new AiChatValidationException("Invalid AI module.")
    };
    private static void RequireId(Guid id)
    { if (id == Guid.Empty) throw new AiChatValidationException("A nonempty AI reference is required."); }
    private static void RequireLimit(int limit)
    { if (limit is < 1 or > AiConversationReadPolicy.MaxReadLimit) throw new AiChatValidationException("Invalid AI history limit."); }
    private static InvalidOperationException Unverified() => new("AI history or operation could not be verified.");
}
