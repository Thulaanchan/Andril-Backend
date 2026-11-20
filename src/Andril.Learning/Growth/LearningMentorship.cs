namespace Andril.Learning.Growth;

/// <summary>Internal account and Learning-profile references only. M2 must authenticate and
/// freshly authorize their ownership/eligibility. This is not a public mentor profile or qualification.</summary>
public sealed record LearningMentorshipParticipant
{
    public Guid AccountId { get; }
    public Guid LearningProfileId { get; }
    public LearningMentorshipParticipant(Guid accountId, Guid learningProfileId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An internal account ID is required.", nameof(accountId));
        if (learningProfileId == Guid.Empty) throw new ArgumentException("A Learning profile ID is required.", nameof(learningProfileId));
        AccountId = accountId; LearningProfileId = learningProfileId;
    }
    public override string ToString() => "Learning mentorship participant [private references omitted]";
}

/// <summary>Immutable declaration of voluntary offering intent, not evidence of authentication,
/// current availability, mentor eligibility, experience or credentials. No registry/discovery authority.
/// Future protected callers must verify fresh voluntary availability before request creation/acceptance.
/// Withdrawal, expiry, topics and qualifications are not defined by this snapshot.</summary>
public sealed class LearningMentorshipOffer
{
    public Guid Id { get; }
    public string Module => "learning";
    public LearningMentorshipParticipant Mentor { get; }
    private LearningMentorshipOffer(Guid id, LearningMentorshipParticipant mentor) { Id = id; Mentor = mentor; }
    public static LearningMentorshipOffer Create(Guid id, LearningMentorshipParticipant mentor)
    {
        if (id == Guid.Empty) throw new ArgumentException("A mentorship offer ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(mentor);
        return new(id, mentor);
    }
    public override string ToString() => "Voluntary Learning mentorship offer [private references omitted]";
}

public enum LearningMentorshipStatus { Pending = 1, Accepted = 2, Declined = 3, Cancelled = 4 }
public enum PendingMentorshipComparison { ExactDuplicate = 1, NotBothPending = 2, PolicyDecisionRequired = 3 }

/// <summary>
/// DEC-20261120-LRN04-01: topic-free local mentorship request/agreement lifecycle only.
/// M2 access, privacy/blocking, current offer validity and authenticated learner/mentor intent
/// must be checked by future protected callers. Supplied IDs are never permission evidence.
/// Accepting the learner's request records mutual agreement locally, not a chat/connection/XP grant.
/// No persistence, automatic evidence verification, discovery, API registration or external effects.
/// </summary>
public sealed class LearningMentorship
{
    public Guid Id { get; }
    public string Module => "learning";
    public LearningMentorshipParticipant Learner { get; }
    public LearningMentorshipOffer Offer { get; }
    public LearningMentorshipParticipant Mentor => Offer.Mentor;
    public LearningMentorshipStatus Status { get; private set; } = LearningMentorshipStatus.Pending;
    public bool IsActive => Status == LearningMentorshipStatus.Accepted;

    private LearningMentorship(Guid id, LearningMentorshipParticipant learner, LearningMentorshipOffer offer)
    { Id = id; Learner = learner; Offer = offer; }

    /// <summary>Represent an authenticated learner's voluntary request only after external checks.
    /// The offer snapshot does not prove that the mentor is currently offering or eligible.</summary>
    public static LearningMentorship Request(Guid id, LearningMentorshipParticipant learner, LearningMentorshipOffer offer)
    {
        if (id == Guid.Empty) throw new ArgumentException("A mentorship request ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(learner); ArgumentNullException.ThrowIfNull(offer);
        if (learner.AccountId == offer.Mentor.AccountId || learner.LearningProfileId == offer.Mentor.LearningProfileId)
            throw new ArgumentException("Self-mentorship or a shared participant profile is not permitted.");
        return new(id, learner, offer);
    }

    public bool IsParticipant(LearningMentorshipParticipant? participant) =>
        participant is not null && (participant == Learner || participant == Mentor);
    public void Accept(LearningMentorshipParticipant participant) => Transition(participant, Mentor, LearningMentorshipStatus.Accepted);
    public void Decline(LearningMentorshipParticipant participant) => Transition(participant, Mentor, LearningMentorshipStatus.Declined);
    public void Cancel(LearningMentorshipParticipant participant) => Transition(participant, Learner, LearningMentorshipStatus.Cancelled);

    private void Transition(LearningMentorshipParticipant participant, LearningMentorshipParticipant required, LearningMentorshipStatus next)
    {
        if (participant is null || participant != required)
            throw new UnauthorizedAccessException("The designated mentorship participant is required.");
        if (Status != LearningMentorshipStatus.Pending)
            throw new InvalidOperationException("Only a pending mentorship request supports this operation.");
        Status = next;
    }

    /// <summary>Compare only exact directed participants and the same offer identity in this
    /// topic-free model. Both pending and identical terms are a known exact duplicate.
    /// Other pending combinations require policy; different skills/topics are unsupported and
    /// must never be certified distinct by this comparison. Nonpending is not retry permission.
    /// No storage search, uniqueness constraint or concurrency guarantee is implemented.</summary>
    public PendingMentorshipComparison ComparePendingRequest(LearningMentorship other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Status != LearningMentorshipStatus.Pending || other.Status != LearningMentorshipStatus.Pending)
            return PendingMentorshipComparison.NotBothPending;
        return Learner == other.Learner && Mentor == other.Mentor && Offer.Id == other.Offer.Id
            ? PendingMentorshipComparison.ExactDuplicate : PendingMentorshipComparison.PolicyDecisionRequired;
    }
    public override string ToString() => $"Learning mentorship ({Status}) [private references omitted]";
}
