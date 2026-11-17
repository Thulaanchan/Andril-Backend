
using System;
using System.Collections.Generic;
using System.Linq;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// Read-only defense-in-depth projection for private conversation metadata.
/// The caller must freshly authorize the actor and query only the exact
/// account/module/profile in the same authorized database operation.
/// This policy neither authenticates users nor queries storage.
/// </summary>
public static class AiConversationReadPolicy
{
    public const int MaxReadLimit = 100;

    /// <summary>
    /// Returns internal metadata only for an active, owned, valid row.
    /// Missing, mismatched, expired, deleted and malformed rows return null.
    /// Never use the returned record as proof of authorization.
    /// </summary>
    public static AiStoredConversation? TryProject(
        AiAuthorizedActor? actor,
        Guid requestedConversationId,
        AiConversationEntity? conversation,
        DateTimeOffset serverNowUtc)
    {
        if (!IsValidActor(actor) ||
            requestedConversationId == Guid.Empty ||
            conversation is null ||
            conversation.Id != requestedConversationId ||
            serverNowUtc == default)
        {
            return null;
        }

        var now = serverNowUtc.ToUniversalTime();

        if (!conversation.IsAccessibleBy(actor, now) ||
            conversation.LastSavedUserMessageAtUtc > now)
        {
            return null;
        }

        try
        {
            if (AiConversationRetentionPolicy.MustHideFromHistory(
                conversation, now))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // Invalid timestamps/expiry must never expose persisted data.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Malformed materialized row: deny rather than expose metadata.
            return null;
        }

        return new AiStoredConversation(
            conversation.Id,
            conversation.AccountId,
            conversation.ModuleProfileId,
            conversation.Module,
            conversation.CreatedAtUtc,
            conversation.LastSavedUserMessageAtUtc,
            conversation.ExpiresAtUtc);
    }

    /// <summary>
    /// Projects an already scoped and bounded repository result.
    /// This is not a substitute for SQL authorization or pagination.
    /// No message text is returned and no expiry timestamp is renewed.
    /// </summary>
    public static IReadOnlyList<AiStoredConversation> ProjectHistory(
        AiAuthorizedActor? actor,
        IReadOnlyList<AiConversationEntity> candidates,
        DateTimeOffset serverNowUtc,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (limit is < 1 or > MaxReadLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));

        if (!IsValidActor(actor) || serverNowUtc == default)
            return Array.Empty<AiStoredConversation>();

        if (candidates.Count > MaxReadLimit)
            throw new InvalidOperationException(
                "AI history candidate limit exceeded.");

        var visible = new List<AiStoredConversation>();
        var seen = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            if (candidate is null)
                continue;

            var projected = TryProject(
                actor, candidate.Id, candidate, serverNowUtc);

            if (projected is null)
                continue;

            if (!seen.Add(projected.ConversationId))
                throw new InvalidOperationException(
                    "AI history data is inconsistent.");

            visible.Add(projected);
        }

        return Array.AsReadOnly(visible
            .OrderByDescending(c => c.LastSavedUserMessageAtUtc)
            .ThenByDescending(c => c.CreatedAtUtc)
            .ThenBy(c => c.ConversationId)
            .Take(limit)
            .ToArray());
    }

    private static bool IsValidActor(AiAuthorizedActor? actor) =>
        actor is not null &&
        actor.UserId != Guid.Empty &&
        actor.ModuleProfileId != Guid.Empty &&
        Enum.IsDefined(actor.Module);
}
