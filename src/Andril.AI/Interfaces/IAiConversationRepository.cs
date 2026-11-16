
using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// M1-owned persistence contract for private AI conversations.
/// Implementations must use backend-verified actors, not Flutter-supplied IDs.
/// Every read/write must recheck account ownership, module-profile eligibility,
/// deletion and 14-day expiry in the same database operation/transaction.
/// Return no data for inaccessible conversations; never cross module boundaries.
/// No implementation or HTTP endpoint is registered by this contract.
/// </summary>
public interface IAiConversationRepository
{
    /// <summary>
    /// Atomically creates a conversation AND saves its first validated user
    /// message. The server sets UTC timestamps and the 14-day expiry.
    /// A repeated operationId must not create another conversation/message.
    /// </summary>
    Task<AiStoredConversation> CreateWithUserMessageAsync(
        AiAuthorizedActor actor,
        Guid operationId,
        string validatedUserText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns null for missing, expired, deleted or unauthorized conversations.
    /// Reading or resuming never extends the expiry deadline.
    /// </summary>
    Task<AiStoredConversation?> FindAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists only active conversations of this exact account/module/profile.
    /// The implementation must enforce a bounded positive limit.
    /// </summary>
    Task<IReadOnlyList<AiStoredConversation>> ListAsync(
        AiAuthorizedActor actor,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns bounded recent messages in chronological order for an authorized
    /// active conversation. Does not renew the conversation's expiry.
    /// </summary>
    Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically appends a validated user message and advances expiry to
    /// 14 days after its server-recorded successful save. Idempotent retries
    /// with the same operationId must not insert or renew a second time.
    /// Deny writes to expired, deleted or no-longer-authorized conversations.
    /// </summary>
    Task<AiStoredAiMessage> AppendUserMessageAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        Guid operationId,
        string validatedUserText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves at most one assistant response for the matching accepted user
    /// operationId, after a fresh authorization/expiry check. This must never
    /// update LastSavedUserMessageAtUtc or ExpiresAtUtc.
    /// </summary>
    Task<AiStoredAiMessage> AppendAssistantMessageAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        Guid operationId,
        string assistantText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently revokes access immediately for this authorized actor;
    /// schedules active-data purge within the approved 24-hour target.
    /// Missing/inaccessible IDs must not disclose another user's existence.
    /// </summary>
    Task DeleteAsync(
        AiAuthorizedActor actor,
        Guid conversationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Internal, module-scoped conversation metadata (not a public API DTO).
/// All timestamps are server-generated UTC. The first user message is saved
/// atomically with creation, so expiry timestamps are always present.
/// </summary>
public sealed record AiStoredConversation(
    Guid ConversationId,
    Guid AccountId,
    Guid ModuleProfileId,
    AiModule Module,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSavedUserMessageAtUtc,
    DateTimeOffset ExpiresAtUtc);

public enum AiStoredMessageRole
{
    User = 1,
    Assistant = 2
}

/// <summary>
/// Private message record. Message text must never be written to audit,
/// quota or billing metadata. Clients cannot supply a trusted message role.
/// </summary>
public sealed record AiStoredAiMessage(
    Guid MessageId,
    Guid ConversationId,
    AiStoredMessageRole Role,
    string Text,
    DateTimeOffset SavedAtUtc);
