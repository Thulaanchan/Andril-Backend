
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Policies;

namespace Andril.AI.Services;

/// <summary>
/// Internal orchestration for private, module-scoped AI conversation storage.
/// Uses server-resolved module identity; it does NOT invoke an AI provider.
/// Do not expose an HTTP endpoint or register a production repository until
/// M2-backed module authorization, privacy and quota gates are verified.
/// </summary>
public sealed class AiConversationLifecycleService
{
    private const int MaxReadLimit = 100; // Technical safeguard, not a product quota.

    private readonly IAiActorAuthorization _authorization;
    private readonly IAiConversationRepository _conversations;

    public AiConversationLifecycleService(
        IAiActorAuthorization authorization,
        IAiConversationRepository conversations)
    {
        _authorization = authorization
            ?? throw new ArgumentNullException(nameof(authorization));
        _conversations = conversations
            ?? throw new ArgumentNullException(nameof(conversations));
    }

    /// <summary>
    /// Disabled legacy creation path, retained for signature compatibility. Future protected
    /// admission must coordinate the first message, operation identity, quota and budget atomically.
    /// No proposed transaction design is approved or implemented by this service.
    /// </summary>
    public Task<AiStoredConversation> StartConversationAsync(
        AiModule module,
        Guid operationId,
        string? userMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No authorization lookup, repository write, admission delegation or accounting effect.
        return Task.FromException<AiStoredConversation>(new InvalidOperationException(
            "Legacy conversation creation is disabled; guarded admission integration is required."));
    }

    /// <summary>
    /// Disabled legacy continuation path, retained for signature compatibility. User messages
    /// and their expiry updates require guarded admission integration, never a separate append.
    /// </summary>
    public Task<AiStoredAiMessage> ContinueConversationAsync(
        AiModule module,
        Guid conversationId,
        Guid operationId,
        string? userMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No user append or expiry renewal. Caller input cannot enable this legacy path.
        return Task.FromException<AiStoredAiMessage>(new InvalidOperationException(
            "Legacy user-message continuation is disabled; guarded admission integration is required."));
    }

    /// <summary>
    /// Disabled legacy assistant write path, retained for signature compatibility only.
    /// Generated replies require guarded late-reply completion integration and its protected
    /// atomic repository boundary. No arguments or mock claims can enable this method.
    /// This failure does not establish provider cost, quota release or permission to retry.
    /// </summary>
    public Task<AiStoredAiMessage> SaveAssistantReplyAsync(
        AiModule module,
        Guid conversationId,
        Guid operationId,
        string? assistantText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Unconditional denial: do not resolve an actor, load a conversation, append, mutate
        // quota/budget or delegate to an incomplete completion integration. Never echo content.
        return Task.FromException<AiStoredAiMessage>(new InvalidOperationException(
            "Legacy assistant persistence is disabled; guarded completion integration is required."));
    }

    /// <summary>
    /// Finds an authorized, unexpired conversation.
    /// Reading does not renew its expiry.
    /// </summary>
    public async Task<AiStoredConversation?> FindConversationAsync(
        AiModule module,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConversationId(conversationId);

        var actor = await ResolveActorAsync(module, cancellationToken);

        return await _conversations.FindAsync(
            actor, conversationId, cancellationToken);
    }

    /// <summary>
    /// Lists conversations belonging to the authorized module profile.
    /// </summary>
    public async Task<IReadOnlyList<AiStoredConversation>> ListConversationsAsync(
        AiModule module,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireReadLimit(limit);

        var actor = await ResolveActorAsync(module, cancellationToken);

        return await _conversations.ListAsync(
            actor, limit, cancellationToken);
    }

    /// <summary>
    /// Loads bounded conversation messages without changing expiry.
    /// </summary>
    public async Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(
        AiModule module,
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RequireConversationId(conversationId);
        RequireReadLimit(limit);

        var actor = await ResolveActorAsync(module, cancellationToken);

        return await _conversations.ReadRecentMessagesAsync(
            actor,
            conversationId,
            limit,
            cancellationToken);
    }

    /// <summary>
    /// Requests immediate access revocation through the repository.
    /// Durable purge and backup cleanup require later implementation.
    /// </summary>
    public async Task DeleteConversationAsync(
        AiModule module,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireConversationId(conversationId);

        var actor = await ResolveActorAsync(module, cancellationToken);

        await _conversations.DeleteAsync(
            actor, conversationId, cancellationToken);
    }

    private async Task<AiAuthorizedActor> ResolveActorAsync(
        AiModule module,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(module))
        {
            throw new AiChatValidationException(
                "Unsupported AI module.");
        }

        // Account/profile IDs must be resolved on the server.
        var actor = await _authorization.GetAuthorizedActorAsync(
            module, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (actor is null ||
            actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            actor.Module != module)
        {
            throw new UnauthorizedAccessException(
                "AI module access denied.");
        }

        // Repository methods must recheck authorization.
        return actor;
    }

    private static void RequireConversationId(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new AiChatValidationException(
                "A conversation ID is required.");
        }
    }

    private static void RequireReadLimit(int limit)
    {
        if (limit is < 1 or > MaxReadLimit)
        {
            throw new AiChatValidationException(
                "Read limit must be between 1 and 100.");
        }
    }
}
