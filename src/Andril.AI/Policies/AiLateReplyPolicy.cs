
using System;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// Defense-in-depth consistency checks for a new assistant reply
/// completing an already admitted AI operation.
/// Not an authentication boundary, operation ledger or recovery gate.
/// </summary>
public static class AiLateReplyPolicy
{
    /// <summary>
    /// Evaluate trusted server-side snapshots immediately before saving
    /// a new assistant reply. The repository must independently verify
    /// account/profile eligibility, operation ownership and current status,
    /// cancellation, uniqueness and recovery/revocation fences under
    /// transactional concurrency controls. Missing evidence means false.
    /// Never set currentOperationVerified from client or provider input.
    /// A true result is not a permission to deliver or stream a response;
    /// delivery requires its own fresh authorization and lifecycle checks.
    /// </summary>
    public static bool CanAccept(
        AiAuthorizedActor? dispatchedActor,
        AiAuthorizedActor? currentActor,
        Guid conversationId,
        Guid operationId,
        AiConversationEntity? conversation,
        AiMessageEntity? acceptedUserMessage,
        AiMessageEntity? assistantReply,
        DateTimeOffset serverNowUtc,
        bool currentOperationVerified)
    {
        if (!currentOperationVerified ||
            dispatchedActor is null ||
            currentActor is null ||
            dispatchedActor != currentActor ||
            conversationId == Guid.Empty ||
            operationId == Guid.Empty ||
            conversation is null ||
            acceptedUserMessage is null ||
            assistantReply is null ||
            serverNowUtc == default)
        {
            return false;
        }

        var now = serverNowUtc.ToUniversalTime();

        try
        {
            // Fresh scope and lifecycle checks; no read renews expiry.
            if (AiConversationReadPolicy.TryProject(
                    currentActor, conversationId, conversation, now)
                is null)
            {
                return false;
            }

            // The new reply must belong to the exact admitted operation.
            // Do not accept an older operation after a newer user message.
            if (acceptedUserMessage.OperationId != operationId ||
                assistantReply.OperationId != operationId ||
                acceptedUserMessage.SavedAtUtc !=
                    conversation.LastSavedUserMessageAtUtc ||
                assistantReply.SavedAtUtc > now)
            {
                return false;
            }

            // Reuse existing role, scope, timestamp and consistency checks.
            AiMessagePersistencePolicy.ValidateAssistantReply(
                conversation, acceptedUserMessage, assistantReply);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
