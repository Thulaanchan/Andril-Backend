using Andril.Communication.Interfaces;

namespace Andril.Communication.Chat;

/// <summary>
/// Proposed internal reader result, not an approved M2/M5 adapter, API DTO or
/// reusable access token. Nullable flags deliberately distinguish missing evidence
/// from explicit confirmation. Only authoritative server readers may populate it.
/// </summary>
internal sealed record ChatConversationAccessEvidence
{
    internal Guid RequestId { get; init; }
    internal Guid AccountSubjectId { get; init; }
    internal Guid AccountId { get; init; }
    internal Guid ProfileAccountId { get; init; }
    internal Guid ModuleProfileId { get; init; }
    internal ChatModule ProfileModule { get; init; }
    internal ChatConversation? Conversation { get; init; }
    internal DateTimeOffset ObservedAtUtc { get; init; }
    internal DateTimeOffset ValidUntilUtc { get; init; }
    internal bool? IsCurrent { get; init; }
    internal bool? IsProfileActive { get; init; }
    internal bool? IsProfileEligible { get; init; }
    internal bool? IsParticipationAuthorized { get; init; }
    internal bool? IsModuleCommunicationEligible { get; init; }
    internal bool? IsBlocked { get; init; }
    internal bool? IsRevoked { get; init; }
    internal bool? IsAdult { get; init; }
    internal bool? HasSocialOptIn { get; init; }
    internal bool? HasMutualMatch { get; init; }

    public override string ToString() => nameof(ChatConversationAccessEvidence);
}
