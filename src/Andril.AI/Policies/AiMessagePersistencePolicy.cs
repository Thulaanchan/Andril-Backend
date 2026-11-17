
using System;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// Pure consistency checks for proposed AI conversation/message persistence.
/// Call within an authorized database transaction after loading real rows.
/// These checks DO NOT authenticate an actor, prove a user message was saved,
/// implement unique constraints, or replace transactional repository checks.
/// No production dependency injection or HTTP endpoint is needed.
/// </summary>
public static class AiMessagePersistencePolicy
{
    /// <summary>
    /// Verify that the first user message matches the new conversation.
    /// The repository must insert BOTH records atomically.
    /// </summary>
    public static void ValidateFirstUserMessage(
        AiConversationEntity conversation,
        AiMessageEntity firstUserMessage)
    {
        RequireMatchingMessage(
            conversation, firstUserMessage, AiStoredMessageRole.User);

        if (conversation.DeletedAtUtc is not null ||
            firstUserMessage.OperationId != conversation.CreateOperationId ||
            firstUserMessage.SavedAtUtc != conversation.CreatedAtUtc ||
            conversation.LastSavedUserMessageAtUtc !=
                firstUserMessage.SavedAtUtc)
        {
            throw Inconsistent();
        }
    }

    /// <summary>
    /// Check a NEW user operation before appending it.
    /// The repository must first enforce operation uniqueness.
    /// Only a newly persisted user message may renew expiry,
    /// within the same database transaction.
    /// </summary>
    public static void ValidateNewUserMessage(
        AiConversationEntity conversation,
        AiMessageEntity userMessage)
    {
        RequireMatchingMessage(
            conversation, userMessage, AiStoredMessageRole.User);

        if (conversation.DeletedAtUtc is not null ||
            userMessage.OperationId == conversation.CreateOperationId ||
            userMessage.SavedAtUtc <
                conversation.LastSavedUserMessageAtUtc ||
            !WithinConversationWindow(conversation, userMessage.SavedAtUtc))
        {
            throw Inconsistent();
        }
    }

    /// <summary>
    /// Verify an assistant reply against an already-persisted user message.
    /// The repository MUST load acceptedUserMessage from authorized storage;
    /// receiving an entity from a caller does not prove persistence.
    /// At most one assistant reply is allowed per accepted operation.
    /// Assistant replies must never renew conversation expiry.
    /// </summary>
    public static void ValidateAssistantReply(
        AiConversationEntity conversation,
        AiMessageEntity acceptedUserMessage,
        AiMessageEntity assistantMessage)
    {
        RequireMatchingMessage(
            conversation, acceptedUserMessage, AiStoredMessageRole.User);

        RequireMatchingMessage(
            conversation, assistantMessage, AiStoredMessageRole.Assistant);

        if (conversation.DeletedAtUtc is not null ||
            acceptedUserMessage.OperationId != assistantMessage.OperationId ||
            acceptedUserMessage.Id == assistantMessage.Id ||
            acceptedUserMessage.SavedAtUtc > assistantMessage.SavedAtUtc ||
            acceptedUserMessage.SavedAtUtc >
                conversation.LastSavedUserMessageAtUtc ||
            !WithinConversationWindow(
                conversation, acceptedUserMessage.SavedAtUtc) ||
            !WithinConversationWindow(
                conversation, assistantMessage.SavedAtUtc))
        {
            throw Inconsistent();
        }
    }

    private static void RequireMatchingMessage(
        AiConversationEntity conversation,
        AiMessageEntity message,
        AiStoredMessageRole expectedRole)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);

        if (conversation.Id == Guid.Empty ||
            conversation.AccountId == Guid.Empty ||
            conversation.ModuleProfileId == Guid.Empty ||
            conversation.CreateOperationId == Guid.Empty ||
            !Enum.IsDefined(conversation.Module) ||
            message.Id == Guid.Empty ||
            message.OperationId == Guid.Empty ||
            message.ConversationId != conversation.Id ||
            message.AccountId != conversation.AccountId ||
            message.ModuleProfileId != conversation.ModuleProfileId ||
            message.Module != conversation.Module ||
            message.Role != expectedRole ||
            string.IsNullOrWhiteSpace(message.Text) ||
            message.SavedAtUtc == default ||
            message.SavedAtUtc.Offset != TimeSpan.Zero ||
            conversation.CreatedAtUtc == default ||
            conversation.CreatedAtUtc.Offset != TimeSpan.Zero ||
            conversation.LastSavedUserMessageAtUtc <
                conversation.CreatedAtUtc ||
            conversation.LastSavedUserMessageAtUtc.Offset != TimeSpan.Zero ||
            conversation.ExpiresAtUtc !=
                AiConversationExpiryPolicy.GetExpiresAtUtc(
                    conversation.LastSavedUserMessageAtUtc))
        {
            throw Inconsistent();
        }
    }

    private static bool WithinConversationWindow(
        AiConversationEntity conversation,
        DateTimeOffset instantUtc) =>
        instantUtc >= conversation.CreatedAtUtc &&
        instantUtc < conversation.ExpiresAtUtc;

    private static InvalidOperationException Inconsistent() =>
        new("AI message persistence consistency check failed.");
}
