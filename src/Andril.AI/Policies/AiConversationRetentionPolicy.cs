
using System;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// A conversation's retention state, independent of its module.
/// Deleted takes precedence over expired.
/// </summary>
public enum AiConversationRetentionState
{
    Active = 1,
    Expired = 2,
    Deleted = 3
}

/// <summary>
/// Pure M1-owned retention decisions for private AI conversations.
///
/// Manual deletion or automatic 14-day inactivity expiry revokes
/// access immediately and sets a 24-hour active-data purge target.
///
/// This policy does not authenticate actors, query databases,
/// execute deletion jobs, delete backups or expose HTTP endpoints.
/// </summary>
public static class AiConversationRetentionPolicy
{
    /// <summary>
    /// Approved active-database content purge target.
    /// Not a guarantee of physical deletion or backup erasure.
    /// </summary>
    public static TimeSpan ActiveDataPurgeTarget { get; } =
        TimeSpan.FromHours(24);

    // Retained for compatibility with existing callers and tests.
    public static TimeSpan ManualDeletionActivePurgeTarget =>
        ActiveDataPurgeTarget;

    public static AiConversationRetentionState Classify(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        ValidateConversation(conversation);

        var nowUtc = NormalizeUtc(
            serverNowUtc, nameof(serverNowUtc));

        if (conversation.DeletedAtUtc is not null)
        {
            return AiConversationRetentionState.Deleted;
        }

        return nowUtc >= conversation.ExpiresAtUtc
            ? AiConversationRetentionState.Expired
            : AiConversationRetentionState.Active;
    }

    public static bool MustHideFromHistory(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc) =>
        Classify(conversation, serverNowUtc) !=
        AiConversationRetentionState.Active;

    public static bool RequiresActivePurgeEvaluation(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc) =>
        Classify(conversation, serverNowUtc) !=
        AiConversationRetentionState.Active;

    /// <summary>
    /// Legacy manual-deletion-only deadline.
    /// New cleanup logic must use GetActivePurgeTargetUtc.
    /// </summary>
    public static DateTimeOffset? GetManualDeletionPurgeTargetUtc(
        AiConversationEntity conversation)
    {
        ValidateConversation(conversation);

        return conversation.DeletedAtUtc is { } deletedAtUtc
            ? deletedAtUtc.Add(ManualDeletionActivePurgeTarget)
            : null;
    }

    public static bool IsManualDeletionPurgeTargetReached(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        var target = GetManualDeletionPurgeTargetUtc(
            conversation);

        var nowUtc = NormalizeUtc(
            serverNowUtc, nameof(serverNowUtc));

        return target is { } dueAtUtc &&
            nowUtc >= dueAtUtc;
    }

    /// <summary>
    /// Returns the combined purge target for manual deletion
    /// or automatic inactivity expiry.
    ///
    /// Active conversations have no actionable purge target.
    /// The earliest revocation event starts the 24-hour clock.
    /// A later deletion cannot postpone an existing expiry target.
    ///
    /// This does not authorize physical purge or bypass legal holds.
    /// </summary>
    public static DateTimeOffset? GetActivePurgeTargetUtc(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        if (Classify(conversation, serverNowUtc) ==
            AiConversationRetentionState.Active)
        {
            return null;
        }

        var revokedAtUtc = conversation.ExpiresAtUtc;

        if (conversation.DeletedAtUtc is { } deletedAtUtc &&
            deletedAtUtc < revokedAtUtc)
        {
            revokedAtUtc = deletedAtUtc;
        }

        return revokedAtUtc.Add(ActiveDataPurgeTarget);
    }

    /// <summary>
    /// Determines whether the combined active-data purge target
    /// has been reached.
    /// Does not establish whether data was physically deleted.
    /// </summary>
    public static bool IsActivePurgeTargetReached(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        var target = GetActivePurgeTargetUtc(
            conversation, serverNowUtc);

        var nowUtc = NormalizeUtc(
            serverNowUtc, nameof(serverNowUtc));

        return target is { } dueAtUtc &&
            nowUtc >= dueAtUtc;
    }

    private static void ValidateConversation(
        AiConversationEntity conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Id == Guid.Empty ||
            conversation.AccountId == Guid.Empty ||
            conversation.ModuleProfileId == Guid.Empty ||
            conversation.CreateOperationId == Guid.Empty ||
            !Enum.IsDefined(conversation.Module) ||
            conversation.CreatedAtUtc == default ||
            conversation.CreatedAtUtc.Offset != TimeSpan.Zero ||
            conversation.LastSavedUserMessageAtUtc <
                conversation.CreatedAtUtc ||
            conversation.LastSavedUserMessageAtUtc.Offset !=
                TimeSpan.Zero ||
            conversation.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            conversation.ExpiresAtUtc !=
                AiConversationExpiryPolicy.GetExpiresAtUtc(
                    conversation.LastSavedUserMessageAtUtc) ||
            (conversation.DeletedAtUtc is { } deletedAtUtc &&
             (deletedAtUtc.Offset != TimeSpan.Zero ||
              deletedAtUtc < conversation.CreatedAtUtc)))
        {
            throw new InvalidOperationException(
                "Invalid AI conversation retention state.");
        }
    }

    private static DateTimeOffset NormalizeUtc(
        DateTimeOffset instant,
        string parameterName)
    {
        if (instant == default)
        {
            throw new ArgumentException(
                "A server timestamp is required.",
                parameterName);
        }

        return instant.ToUniversalTime();
    }
}
