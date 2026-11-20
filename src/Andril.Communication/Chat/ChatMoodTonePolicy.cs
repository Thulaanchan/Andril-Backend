using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Centralizes MoodTone rules for validated Chat modules. An approved Social
/// vocabulary must be integrated here before any non-null tone can be accepted.
/// Caller-provided labels or allowlists cannot establish vocabulary approval.
/// </summary>
internal static class ChatMoodTonePolicy
{
    internal static void Validate(ChatModule module, string? moodTone)
    {
        if (moodTone is null)
        {
            return;
        }

        if (module != ChatModule.Social)
        {
            throw new ArgumentException("MoodTone is only supported in Social.", nameof(moodTone));
        }

        // Fail closed until the authoritative vocabulary is approved; do not
        // normalize, infer, or silently discard an unapproved value.
        throw new ArgumentException("The Social MoodTone vocabulary is pending approval.", nameof(moodTone));
    }
}
