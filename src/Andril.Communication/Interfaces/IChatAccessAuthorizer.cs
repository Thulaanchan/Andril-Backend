namespace Andril.Communication.Interfaces;

/// <summary>The three independent ANDRIL product modules.</summary>
public enum ChatModule
{
    Learning = 1,
    Hobbies = 2,
    Social = 3
}

/// <summary>
/// Server-only result for one authorized conversation access check.
/// VerifiedSubjectId is the authentication subject, AccountId is the internal
/// account identity, and ModuleProfileId is the private identity in Module.
/// This is not an API DTO or a reusable authorization token.
/// </summary>
public sealed record ChatAuthorizedActor(
    Guid VerifiedSubjectId,
    Guid AccountId,
    Guid ModuleProfileId,
    ChatModule Module,
    Guid ConversationId)
{
    public override string ToString() => nameof(ChatAuthorizedActor);
}

/// <summary>
/// Backend-only authorization boundary for existing human-to-human conversations.
/// Implementations must resolve the verified subject from trusted server context,
/// then verify the internal account, owned active private profile, conversation
/// module and participation using authoritative M2/M5 data. Caller-supplied
/// identifiers, profile records and previously returned actors are not proof.
/// Social match-based access additionally requires 18+, explicit opt-in, an active
/// mutual match and no applicable blocks. Missing, invalid or unavailable evidence
/// must deny access. Authorization must be checked again for each operation.
/// These requirements are a contract; real M2/M5 checks are not implemented here.
/// Message validation must separately reject non-null MoodTone in Learning/Hobbies.
/// </summary>
public interface IChatAccessAuthorizer
{
    /// <summary>
    /// Returns a server-resolved actor for this exact module and conversation,
    /// or null for any denial without disclosing identities or resource existence.
    /// Module and conversationId select a target; neither grants access.
    /// Cancellation propagates as OperationCanceledException.
    /// </summary>
    Task<ChatAuthorizedActor?> AuthorizeConversationAsync(
        ChatModule module,
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
