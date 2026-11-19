
using System;
using System.Collections.Generic;
using System.Globalization;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// Recent-only prototype context (AI-PRE-06). All inputs must come
/// from authorized backend storage, never from a client-built history.
/// This policy is not authentication, a tokenizer or provider dispatch.
/// Recheck authorization and retention before dispatch and delivery.
/// </summary>
public static class AiConversationContextPolicy
{
    public const int MaxInputTokens = 2000;
    public const int MaxCandidateMessages = 100; // Technical read bound.

    public static IReadOnlyList<AiStoredAiMessage> SelectRecentMessages(
        AiAuthorizedActor? actor,
        AiConversationEntity conversation,
        IReadOnlyList<AiMessageEntity> orderedMessages,
        Guid currentUserMessageId,
        DateTimeOffset serverNowUtc,
        int maxMessages,
        Func<IReadOnlyList<AiStoredAiMessage>, int> countFullInputTokens)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(orderedMessages);
        ArgumentNullException.ThrowIfNull(countFullInputTokens);

        if (maxMessages is < 1 or > MaxCandidateMessages)
            throw new ArgumentOutOfRangeException(nameof(maxMessages));

        if (serverNowUtc == default || currentUserMessageId == Guid.Empty)
            throw Unavailable();

        var now = serverNowUtc.ToUniversalTime();

        if (!conversation.IsAccessibleBy(actor, now) ||
            AiConversationRetentionPolicy.MustHideFromHistory(conversation, now) ||
            conversation.LastSavedUserMessageAtUtc > now)
            throw Unavailable();

        if (orderedMessages.Count is < 1 or > MaxCandidateMessages)
            throw Unavailable();

        // Validate ALL candidate records before measuring or returning any
        // context. Repository ordering must be stable, including time ties.
        var ids = new HashSet<Guid>();
        var operations = new HashSet<(Guid, AiStoredMessageRole)>();
        var snapshot = new AiStoredAiMessage[orderedMessages.Count];
        var previous = conversation.CreatedAtUtc;

        for (var i = 0; i < snapshot.Length; i++)
        {
            var message = orderedMessages[i];

            if (message is null ||
                message.ConversationId != conversation.Id ||
                message.AccountId != conversation.AccountId ||
                message.ModuleProfileId != conversation.ModuleProfileId ||
                message.Module != conversation.Module ||
                message.Id == Guid.Empty ||
                message.OperationId == Guid.Empty ||
                !Enum.IsDefined(message.Role) ||
                string.IsNullOrWhiteSpace(message.Text) ||
                message.SavedAtUtc.Offset != TimeSpan.Zero ||
                message.SavedAtUtc < previous ||
                message.SavedAtUtc > now ||
                message.SavedAtUtc >= conversation.ExpiresAtUtc ||
                (message.Role == AiStoredMessageRole.User &&
                 (message.SavedAtUtc > conversation.LastSavedUserMessageAtUtc ||
                  new StringInfo(message.Text).LengthInTextElements > 500)) ||
                !ids.Add(message.Id) ||
                !operations.Add((message.OperationId, message.Role)))
                throw Unavailable();

            snapshot[i] = message.ToStoredMessage();
            previous = message.SavedAtUtc;
        }

        var current = orderedMessages[^1];

        if (snapshot[^1].MessageId != currentUserMessageId ||
            current.Role != AiStoredMessageRole.User ||
            current.SavedAtUtc != conversation.LastSavedUserMessageAtUtc ||
            (current.OperationId == conversation.CreateOperationId &&
             (snapshot.Length != 1 ||
              current.SavedAtUtc != conversation.CreatedAtUtc)))
            throw Unavailable();

        // Always keep the complete current user message exactly once.
        // A server-side, model-aware counter must measure the WHOLE request,
        // including trusted instructions, selected messages and framing.
        // It must be pure/local, not an AI request or network call.
        var selected = Array.AsReadOnly(new[] { snapshot[^1] });

        if (!Fits(selected, countFullInputTokens))
            throw new InvalidOperationException(
                "AI input exceeds the permitted budget.");

        // Try progressively larger contiguous recent windows. Start on
        // a user message so a window never begins with an assistant reply.
        var earliest = Math.Max(0, snapshot.Length - maxMessages);

        for (var start = snapshot.Length - 2; start >= earliest; start--)
        {
            if (snapshot[start].Role != AiStoredMessageRole.User)
                continue;

            var candidate = Array.AsReadOnly(snapshot[start..]);

            if (!Fits(candidate, countFullInputTokens))
                break;

            selected = candidate;
        }

        return selected;
    }

    private static bool Fits(
        IReadOnlyList<AiStoredAiMessage> messages,
        Func<IReadOnlyList<AiStoredAiMessage>, int> countFullInputTokens)
    {
        var tokens = countFullInputTokens(messages);

        if (tokens <= 0)
            throw new InvalidOperationException(
                "AI input token count unavailable.");

        return tokens <= MaxInputTokens;
    }

    private static InvalidOperationException Unavailable() =>
        new("AI conversation context unavailable.");
}
