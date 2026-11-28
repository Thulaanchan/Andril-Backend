using Andril.Learning.Activities;

namespace Andril.Learning.Connections;

public enum LearningConnectionStatus { Pending = 1, Accepted = 2, Declined = 3, Cancelled = 4, Disconnected = 5 }

/// <summary>
/// User-approved B4-07 relationship lifecycle. Participant references and stored
/// status are never identity, eligibility, privacy, blocking or communication
/// authorization. Protected callers must freshly verify all those authorities.
/// </summary>
public sealed class LearningConnection
{
    public Guid Id { get; }
    public string Module => "learning";
    public LearningActivityMember Sender { get; }
    public LearningActivityMember Recipient { get; }
    public LearningConnectionStatus Status { get; private set; } = LearningConnectionStatus.Pending;
    private LearningConnection(Guid id, LearningActivityMember sender, LearningActivityMember recipient)
        => (Id, Sender, Recipient) = (id, sender, recipient);

    public static LearningConnection Request(Guid id, LearningActivityMember sender, LearningActivityMember recipient)
    {
        ArgumentNullException.ThrowIfNull(sender); ArgumentNullException.ThrowIfNull(recipient);
        if (id == Guid.Empty || sender.AccountId == Guid.Empty || sender.ProfileId == Guid.Empty ||
            recipient.AccountId == Guid.Empty || recipient.ProfileId == Guid.Empty)
            throw new ArgumentException("Non-empty Learning relationship references are required.");
        if (sender.AccountId == recipient.AccountId || sender.ProfileId == recipient.ProfileId)
            throw new ArgumentException("Self connection requests are not permitted.");
        return new(id, sender, recipient);
    }

    public bool IsParticipant(LearningActivityMember? actor) => actor is not null && (actor == Sender || actor == Recipient);
    public void Accept(LearningActivityMember actor) => Respond(actor, Recipient, LearningConnectionStatus.Accepted);
    public void Decline(LearningActivityMember actor) => Respond(actor, Recipient, LearningConnectionStatus.Declined);
    public void Cancel(LearningActivityMember actor) => Respond(actor, Sender, LearningConnectionStatus.Cancelled);
    public void Disconnect(LearningActivityMember actor)
    {
        if (!IsParticipant(actor)) throw new UnauthorizedAccessException("Learning connection access denied.");
        if (Status != LearningConnectionStatus.Accepted) throw new InvalidOperationException("An accepted connection is required.");
        Status = LearningConnectionStatus.Disconnected;
    }
    private void Respond(LearningActivityMember actor, LearningActivityMember required, LearningConnectionStatus next)
    {
        if (actor != required) throw new UnauthorizedAccessException("The designated Learning participant is required.");
        if (Status != LearningConnectionStatus.Pending) throw new InvalidOperationException("A pending connection request is required.");
        Status = next;
    }
    public override string ToString() => $"Learning connection ({Status}) [private references omitted]";
}
