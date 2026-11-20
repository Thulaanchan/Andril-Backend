namespace Andril.Learning.SkillExchange;

/// <summary>References only, not authenticated identity, profile ownership or module eligibility.
/// M2 must freshly resolve and authorize the account and active Learning profile before use.</summary>
public sealed record SkillExchangeParticipant
{
    public Guid AccountId { get; }
    public Guid LearningProfileId { get; }

    public SkillExchangeParticipant(Guid accountId, Guid learningProfileId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An internal account ID is required.", nameof(accountId));
        if (learningProfileId == Guid.Empty) throw new ArgumentException("A Learning profile ID is required.", nameof(learningProfileId));
        AccountId = accountId;
        LearningProfileId = learningProfileId;
    }

    public override string ToString() => "Learning exchange participant [private references omitted]";
}

public enum SkillExchangeStatus { Pending = 1, Accepted = 2, Declined = 3, Cancelled = 4 }

/// <summary>Local comparison only, not a uniqueness check or permission to create another proposal.</summary>
public enum PendingProposalComparison { ExactDuplicate = 1, NotBothPending = 2, PolicyDecisionRequired = 3 }

/// <summary>
/// DEC-20261120-LRN01-01: local Learning proposal lifecycle, not an authorization or storage boundary.
/// A future protected service must establish voluntary sender intent, fresh M2 access/privacy/blocking,
/// partner eligibility and current skill validity. Supplied participant/skill IDs are not that evidence.
/// No discovery, history storage, concurrency guarantee, notifications, connection, chat grant or XP.
/// </summary>
public sealed class SkillExchange
{
    public Guid Id { get; }
    public string Module => "learning";
    public SkillExchangeParticipant Sender { get; }
    public SkillExchangeParticipant Recipient { get; }
    // Opaque identities use the same representation as LearningPreferences. No catalogue or
    // normalization is invented. Offered means the sender's offer; wanted means their request.
    public Guid OfferedSkillId { get; }
    public Guid WantedSkillId { get; }
    public SkillExchangeStatus Status { get; private set; } = SkillExchangeStatus.Pending;

    private SkillExchange(Guid id, SkillExchangeParticipant sender, SkillExchangeParticipant recipient,
        Guid offeredSkillId, Guid wantedSkillId)
    {
        Id = id; Sender = sender; Recipient = recipient;
        OfferedSkillId = offeredSkillId; WantedSkillId = wantedSkillId;
    }

    /// <summary>Construct the sender's voluntary proposal after external authorization.
    /// Construction cannot verify intent or whether any other pending proposal already exists.
    /// Multiple skills and a same-skill offer/request remain unsupported pending policy.</summary>
    public static SkillExchange Propose(Guid id, SkillExchangeParticipant sender,
        SkillExchangeParticipant recipient, Guid offeredSkillId, Guid wantedSkillId)
    {
        if (id == Guid.Empty) throw new ArgumentException("An exchange ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);
        if (sender.AccountId == recipient.AccountId || sender.LearningProfileId == recipient.LearningProfileId)
            throw new ArgumentException("A Skill Exchange requires distinct participants.");
        if (offeredSkillId == Guid.Empty) throw new ArgumentException("An offered skill reference is required.", nameof(offeredSkillId));
        if (wantedSkillId == Guid.Empty) throw new ArgumentException("A wanted skill reference is required.", nameof(wantedSkillId));
        if (offeredSkillId == wantedSkillId)
            throw new NotSupportedException("An identical offered and wanted skill requires an approved overlap policy.");
        return new(id, sender, recipient, offeredSkillId, wantedSkillId);
    }

    /// <summary>Matching references identify a domain participant, never authenticate a caller.</summary>
    public bool IsParticipant(SkillExchangeParticipant? participant) =>
        participant is not null && (participant == Sender || participant == Recipient);

    /// <summary>Recipient acceptance supplies the second agreement in this proposal lifecycle.
    /// A trusted caller must establish that the current actor actually is that recipient.</summary>
    public void Accept(SkillExchangeParticipant participant) => Transition(participant, Recipient, SkillExchangeStatus.Accepted);
    public void Decline(SkillExchangeParticipant participant) => Transition(participant, Recipient, SkillExchangeStatus.Declined);
    public void Cancel(SkillExchangeParticipant participant) => Transition(participant, Sender, SkillExchangeStatus.Cancelled);

    private void Transition(SkillExchangeParticipant participant, SkillExchangeParticipant required, SkillExchangeStatus target)
    {
        if (participant is null || participant != required)
            throw new UnauthorizedAccessException("The designated Skill Exchange participant is required.");
        if (Status != SkillExchangeStatus.Pending)
            throw new InvalidOperationException("Only a pending proposal can be accepted, declined or cancelled.");
        Status = target;
    }

    /// <summary>
    /// Exact same directed participants and skill terms while both pending are a known duplicate,
    /// irrespective of exchange ID. A protected coordinator must reject that candidate atomically.
    /// Every other pending pairing is PolicyDecisionRequired: reversed proposals and different
    /// skills are NOT certified distinct. NotBothPending does not authorize historical repetition.
    /// This comparison neither searches storage nor prevents two independently constructed copies.
    /// </summary>
    public PendingProposalComparison ComparePendingProposal(SkillExchange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Status != SkillExchangeStatus.Pending || other.Status != SkillExchangeStatus.Pending)
            return PendingProposalComparison.NotBothPending;
        return Sender == other.Sender && Recipient == other.Recipient &&
            OfferedSkillId == other.OfferedSkillId && WantedSkillId == other.WantedSkillId
            ? PendingProposalComparison.ExactDuplicate : PendingProposalComparison.PolicyDecisionRequired;
    }

    public override string ToString() => $"Learning Skill Exchange ({Status}) [private references omitted]";
}
