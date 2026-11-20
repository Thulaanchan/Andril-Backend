using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Server-side structural reference to a private profile in exactly one module.
/// AccountId is an internal account reference, not the authentication subject or
/// ModuleProfileId. These identifiers are unverified and never grant access.
/// This is not an API DTO and contains no profile content or other-module data.
/// M2 must independently verify ownership, active state and eligibility.
/// </summary>
public sealed class ChatParticipantReference
{
    public ChatModule Module { get; }

    public Guid AccountId { get; }

    public Guid ModuleProfileId { get; }

    private ChatParticipantReference(ChatModule module, Guid accountId, Guid moduleProfileId)
    {
        Module = module;
        AccountId = accountId;
        ModuleProfileId = moduleProfileId;
    }

    public static ChatParticipantReference Create(ChatModule module, Guid accountId, Guid moduleProfileId)
    {
        if (!Enum.IsDefined(module))
        {
            throw new ArgumentOutOfRangeException(nameof(module), "Unsupported ANDRIL module.");
        }

        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("An account identifier is required.", nameof(accountId));
        }

        if (moduleProfileId == Guid.Empty)
        {
            throw new ArgumentException("A module profile identifier is required.", nameof(moduleProfileId));
        }

        return new ChatParticipantReference(module, accountId, moduleProfileId);
    }
}
