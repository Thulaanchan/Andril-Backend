
using System;
using Andril.AI.Persistence;

namespace Andril.AI.Policies;

/// <summary>
/// A conversation's retention state, independent of its module.
/// Deleted takes precedence over expired if both conditions apply.
/// </summary>
public enum AiConversationRetentionState
{
    Active = 1,
    Expired = 2,
    Deleted = 3
}

/// <summary>
/// Pure, M1-owned retention decisions for private AI conversations.
///
/// A conversation expires 14 days after its last successfully saved user
/// message. Explicit deletion revokes access immediately and sets a target
/// to purge active storage within 24 hours.
///
/// This policy does not authorize an actor, query a database, perform purges,
/// delete backups, grant legal exceptions, or register an HTTP endpoint.
/// Repositories must deny access independently and run authorized, audited,
/// idempotent purge operations under the approved retention/legal rules.
/// </summary>
public static class AiConversationRetentionPolicy
{
    /// <summary>
    /// Target for removal from active storage after explicit deletion.
    /// Not a guarantee that a job ran, nor a provider-backup retention rule.
    /// </summary>
    public static TimeSpan ManualDeletionActivePurgeTarget { get; } =
        TimeSpan.FromHours(24);

    /// <summary>
    /// Classifies a persisted conversation at the server's supplied time.
    /// A malformed persistence record fails closed by throwing.
    /// Reads and classifications never extend conversation expiry.
    /// </summary>
    public static AiConversationRetentionState Classify(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        ValidateConversation(conversation);
        var nowUtc = NormalizeUtc(serverNowUtc, nameof(serverNowUtc));

        if (conversation.DeletedAtUtc is not null)
        {
            return AiConversationRetentionState.Deleted;
        }

        return nowUtc >= conversation.ExpiresAtUtc
            ? AiConversationRetentionState.Expired
            : AiConversationRetentionState.Active;
    }

    /// <summary>
    /// A deleted or expired conversation must not appear in accessible history.
    /// A returned false still does NOT prove current actor authorization.
    /// </summary>
    public static bool MustHideFromHistory(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc) =>
        Classify(conversation, serverNowUtc) !=
        AiConversationRetentionState.Active;

    /// <summary>
    /// Identifies rows eligible for evaluation by a future cleanup worker.
    /// This is NOT permission to bypass legal holds or retention exceptions.
    /// No data is deleted here.
    /// </summary>
    public static bool RequiresActivePurgeEvaluation(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc) =>
        Classify(conversation, serverNowUtc) !=
        AiConversationRetentionState.Active;

    /// <summary>
    /// Gets the target time for active-storage purge after explicit deletion.
    /// Returns null when the conversation has not been marked deleted.
    /// Automatic inactivity expiry is NOT assigned an invented purge SLA.
    /// </summary>
    public static DateTimeOffset? GetManualDeletionPurgeTargetUtc(
        AiConversationEntity conversation)
    {
        ValidateConversation(conversation);

        return conversation.DeletedAtUtc is { } deletedAtUtc
            ? deletedAtUtc.Add(ManualDeletionActivePurgeTarget)
            : null;
    }

    /// <summary>
    /// True when the manual-deletion target time has been reached.
    /// This does not establish whether the actual data purge was completed.
    /// </summary>
    public static bool IsManualDeletionPurgeTargetReached(
        AiConversationEntity conversation,
        DateTimeOffset serverNowUtc)
    {
        var target = GetManualDeletionPurgeTargetUtc(conversation);
        var nowUtc = NormalizeUtc(serverNowUtc, nameof(serverNowUtc));

        return target is { } dueAtUtc && nowUtc >= dueAtUtc;
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
            conversation.LastSavedUserMessageAtUtc.Offset != TimeSpan.Zero ||
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
