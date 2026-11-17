
using System;
using System.Globalization;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;

namespace Andril.AI.Persistence;

/// <summary>
/// Proposed M1-owned, private AI message persistence entity.
/// No database table, mapping, migration or HTTP endpoint is created here.
/// Factories check conversation identity and lifecycle only: the repository
/// MUST independently verify current server-side authorization and perform
/// atomic inserts, operation-id uniqueness and expiry updates.
/// </summary>
public sealed class AiMessageEntity
{
    private const int MaxUserTextElements = 500;

    // Reserved for a future ORM materializer;
    // do not use as an application factory.
    private AiMessageEntity() { }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid ModuleProfileId { get; private set; }
    public AiModule Module { get; private set; }
    public Guid OperationId { get; private set; }
    public AiStoredMessageRole Role { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; private set; }

    /// <summary>
    /// Constructs a user message after validating the
    /// 500-text-element input.
    ///
    /// For a NEW user operation, the repository must
    /// insert this message and update the parent expiry
    /// within the SAME database transaction.
    ///
    /// An idempotent retry must return the prior message
    /// without renewing expiry.
    /// </summary>
    public static AiMessageEntity CreateUserMessage(
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid operationId,
        string? userText,
        DateTimeOffset serverSavedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(userText) ||
            new StringInfo(userText).LengthInTextElements >
                MaxUserTextElements)
        {
            throw new ArgumentException(
                "A user message must contain 1-500 text elements.",
                nameof(userText));
        }

        return Create(
            conversation,
            actor,
            operationId,
            AiStoredMessageRole.User,
            userText.Trim(),
            serverSavedAtUtc);
    }

    /// <summary>
    /// Constructs an assistant reply without extending
    /// parent expiry.
    ///
    /// The repository MUST confirm that operationId
    /// belongs to an accepted user message in this
    /// conversation and prevent a second assistant reply
    /// for that operation.
    ///
    /// Clients must never call this factory directly.
    /// </summary>
    public static AiMessageEntity CreateAssistantMessage(
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid operationId,
        string? assistantText,
        DateTimeOffset serverSavedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
        {
            throw new ArgumentException(
                "A non-empty assistant message is required.",
                nameof(assistantText));
        }

        return Create(
            conversation,
            actor,
            operationId,
            AiStoredMessageRole.Assistant,
            assistantText,
            serverSavedAtUtc);
    }

    /// <summary>
    /// Converts this entity to the existing internal
    /// repository message DTO.
    ///
    /// Never use the DTO as proof of current authorization.
    /// </summary>
    public AiStoredAiMessage ToStoredMessage() => new(
        Id,
        ConversationId,
        Role,
        Text,
        SavedAtUtc);

    private static AiMessageEntity Create(
        AiConversationEntity conversation,
        AiAuthorizedActor actor,
        Guid operationId,
        AiStoredMessageRole role,
        string text,
        DateTimeOffset serverSavedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(actor);

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An operation ID is required.",
                nameof(operationId));
        }

        if (serverSavedAtUtc == default)
        {
            throw new ArgumentException(
                "A server timestamp is required.",
                nameof(serverSavedAtUtc));
        }

        var savedAtUtc = serverSavedAtUtc.ToUniversalTime();

        // A matching actor is necessary, but NOT
        // sufficient, for authorization.
        //
        // The database-backed repository must check
        // the trusted M2 access reader and parent row
        // within its transaction on every operation.
        if (!conversation.IsAccessibleBy(actor, savedAtUtc))
        {
            throw new UnauthorizedAccessException(
                "AI conversation unavailable.");
        }

        if (savedAtUtc < conversation.CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serverSavedAtUtc),
                "A message cannot predate its conversation.");
        }

        return new AiMessageEntity
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            AccountId = conversation.AccountId,
            ModuleProfileId = conversation.ModuleProfileId,
            Module = conversation.Module,
            OperationId = operationId,
            Role = role,
            Text = text,
            SavedAtUtc = savedAtUtc
        };
    }
}
