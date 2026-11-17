
using System;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

public enum AiConversationEntryIntent
{
    IntentionalEntry = 1,
    ContinueOpenChat = 2,
    ExplicitResume = 3,
    NewChat = 4
}

public enum AiConversationEntryAction
{
    Denied = 0,
    StartFresh = 1,
    ContinueSelected = 2
}

public sealed record AiConversationEntryDecision(
    AiConversationEntryAction Action,
    Guid? ConversationId);

/// <summary>
/// Pure AI chat entry routing for AI-PRE-02 and AI-MEM-05.
/// All inputs represent trusted backend state after fresh authorization.
/// Never infer authorization from an actor record or a client-provided ID.
/// This policy does not load messages, create rows, or expose an endpoint.
/// </summary>
public static class AiConversationEntryPolicy
{
    public static AiConversationEntryDecision Decide(
        AiAuthorizedActor? actor,
        AiConversationEntryIntent intent,
        AiConversationEntity? selectedConversation,
        DateTimeOffset serverNowUtc,
        TimeZoneInfo? profileTimeZone = null,
        DateTimeOffset? previousIntentionalEntryUtc = null)
    {
        if (actor is null || actor.UserId == Guid.Empty ||
            actor.ModuleProfileId == Guid.Empty ||
            !Enum.IsDefined(actor.Module) ||
            !Enum.IsDefined(intent) || serverNowUtc == default)
        {
            return Denied();
        }

        var now = serverNowUtc.ToUniversalTime();

        // A fresh entry never imports another conversation or its context.
        if (intent == AiConversationEntryIntent.NewChat)
            return Fresh();

        if (intent == AiConversationEntryIntent.IntentionalEntry)
        {
            // These inputs must be resolved for this exact profile/module.
            // Missing time-zone data must not silently fall back to UTC.
            if (profileTimeZone is null)
                return Denied();

            if (previousIntentionalEntryUtc is null)
                return Fresh();

            var previous = previousIntentionalEntryUtc.Value;
            if (previous == default || previous > now)
                return Denied();

            var previousDay = TimeZoneInfo.ConvertTime(
                previous, profileTimeZone).Date;
            var currentDay = TimeZoneInfo.ConvertTime(
                now, profileTimeZone).Date;

            if (previousDay != currentDay)
                return Fresh();

            // Same-day entry may reopen only the already selected chat.
            // No automatic selection from saved history.
            if (selectedConversation is null)
                return Fresh();
        }

        // Explicit resume or continuation of an open chat must refer to
        // the selected conversation, even across local midnight.
        if (selectedConversation is null ||
            !selectedConversation.IsAccessibleBy(actor, now) ||
            AiConversationRetentionPolicy.MustHideFromHistory(
                selectedConversation, now) ||
            selectedConversation.LastSavedUserMessageAtUtc > now)
        {
            return Denied();
        }

        return new AiConversationEntryDecision(
            AiConversationEntryAction.ContinueSelected,
            selectedConversation.Id);
    }

    private static AiConversationEntryDecision Fresh() =>
        new(AiConversationEntryAction.StartFresh, null);

    private static AiConversationEntryDecision Denied() =>
        new(AiConversationEntryAction.Denied, null);
}
