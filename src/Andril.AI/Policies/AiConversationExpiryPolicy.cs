
namespace Andril.AI.Policies;

/// <summary>
/// Calculates the approved inactivity expiry of a saved AI conversation.
/// The clock starts at the last successfully persisted USER message.
/// Reading, resuming, or saving an assistant reply must not renew it.
/// </summary>
public static class AiConversationExpiryPolicy
{
    public static TimeSpan InactivityWindow { get; } =
        TimeSpan.FromDays(14);

    /// <summary>
    /// Returns the UTC instant when the conversation becomes inaccessible.
    /// The caller must supply the server-recorded timestamp of the last
    /// successfully saved user message, never a client-supplied timestamp.
    /// </summary>
    public static DateTimeOffset GetExpiresAtUtc(
        DateTimeOffset lastSavedUserMessageAtUtc)
    {
        if (lastSavedUserMessageAtUtc == default)
        {
            throw new ArgumentException(
                "A persisted user-message timestamp is required.",
                nameof(lastSavedUserMessageAtUtc));
        }

        return lastSavedUserMessageAtUtc.ToUniversalTime()
            .Add(InactivityWindow);
    }

    /// <summary>
    /// Expiry is inclusive: access is denied at the expiry instant.
    /// A deleted conversation is denied separately by the repository/service.
    /// </summary>
    public static bool IsExpired(
        DateTimeOffset lastSavedUserMessageAtUtc,
        DateTimeOffset nowUtc)
    {
        return nowUtc.ToUniversalTime() >=
            GetExpiresAtUtc(lastSavedUserMessageAtUtc);
    }
}
