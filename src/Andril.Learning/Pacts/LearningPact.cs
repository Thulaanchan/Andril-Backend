namespace Andril.Learning.Pacts;

/// <summary>Immutable internal-account/Learning-profile references, not verified identity,
/// ownership or access evidence. No general authoritative Learning actor type exists yet.</summary>
public sealed record LearningPactParticipant
{
    public Guid AccountId { get; }
    public Guid LearningProfileId { get; }

    public LearningPactParticipant(Guid accountId, Guid learningProfileId)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("An internal account ID is required.", nameof(accountId));
        if (learningProfileId == Guid.Empty) throw new ArgumentException("A Learning profile ID is required.", nameof(learningProfileId));
        AccountId = accountId; LearningProfileId = learningProfileId;
    }

    public override string ToString() => "Learning Pact participant [private references omitted]";
}

public enum LearningPactStatus { Pending = 1, Active = 2, Cancelled = 3 }
public enum PactInvitationDecision { Pending = 1, Accepted = 2, Declined = 3 }

/// <summary>
/// DEC-20261120-LRN03-01: local proposal/agreement invariants only, not an authorization boundary.
/// Protected callers must establish voluntary authenticated proposer/invitee intent, resolve active
/// Learning accounts/profiles, and check current M2 eligibility/privacy/blocking for each operation.
/// IDs and possession of this sensitive domain instance are not credentials. Never expose it directly
/// as an API response. Persistence, current private access and transaction/concurrency control are absent.
/// No progress, completion, XP, connections, communication grants or notification actions are supplied.
/// </summary>
public sealed class LearningPact
{
    public Guid Id { get; }
    public string Module => "learning";
    public LearningPactParticipant Proposer { get; }
    public LearningPactStatus Status { get; private set; } = LearningPactStatus.Pending;
    public IReadOnlyList<Participation> Participants { get; private set; }
    public IReadOnlyList<Commitment> Commitments { get; }

    private LearningPact(Guid id, LearningPactParticipant proposer,
        IReadOnlyList<Participation> participants, IReadOnlyList<Commitment> commitments)
    { Id = id; Proposer = proposer; Participants = participants; Commitments = commitments; }

    /// <summary>Propose fixed participant and commitment terms after external permission checks.
    /// The proposer agrees by proposing. Commitments can be empty: no minimum was approved.
    /// Text is retained exactly; no deadlines, attribution, categories, length or count caps.
    /// Commitments can only be supplied at proposal creation; later edits/additions/deletion
    /// would change agreed terms and require approval of their permission/lifecycle semantics.</summary>
    public static LearningPact Propose(Guid id, LearningPactParticipant proposer,
        IEnumerable<LearningPactParticipant> participants, IEnumerable<string> commitmentTexts)
    {
        if (id == Guid.Empty) throw new ArgumentException("A Pact ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(commitmentTexts);
        var people = participants.ToArray();
        if (people.Length < 2) throw new ArgumentException("At least two distinct Learning participants are required.", nameof(participants));
        var accounts = new HashSet<Guid>(); var profiles = new HashSet<Guid>();
        foreach (var person in people)
        {
            if (person is null || !accounts.Add(person.AccountId) || !profiles.Add(person.LearningProfileId))
                throw new ArgumentException("Participant account and profile identities must be distinct.", nameof(participants));
        }
        if (!people.Contains(proposer)) throw new ArgumentException("The proposer must be a designated participant.", nameof(participants));

        var texts = commitmentTexts.ToArray();
        if (texts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Commitment text is required.", nameof(commitmentTexts));
        var agreements = people.Select(person => new Participation(person,
            person == proposer ? PactInvitationDecision.Accepted : PactInvitationDecision.Pending)).ToArray();
        var commitments = texts.Select(text => new Commitment(Guid.NewGuid(), id, text)).ToArray();
        return new(id, proposer, Array.AsReadOnly(agreements), Array.AsReadOnly(commitments));
    }

    /// <summary>Matches domain references only; cannot authenticate the caller.</summary>
    public bool IsParticipant(LearningPactParticipant? participant) =>
        participant is not null && Participants.Any(item => item.Participant == participant);

    /// <summary>Record this invitee's own decision, after authoritative identity/access checks.</summary>
    public void AcceptInvitation(LearningPactParticipant participant) => Respond(participant, PactInvitationDecision.Accepted);

    /// <summary>Retain decline without inventing an overall terminal policy. The Pact remains
    /// pending and cannot achieve unanimity; changing a recorded decision is unsupported.</summary>
    public void DeclineInvitation(LearningPactParticipant participant) => Respond(participant, PactInvitationDecision.Declined);

    private void Respond(LearningPactParticipant participant, PactInvitationDecision decision)
    {
        var index = FindParticipant(participant);
        if (participant == Proposer) throw new InvalidOperationException("The proposer has already agreed through proposal creation.");
        RequirePending();
        if (Participants[index].Decision != PactInvitationDecision.Pending)
            throw new InvalidOperationException("An invitation decision is already recorded.");

        var next = Participants.ToArray();
        next[index] = new Participation(Participants[index].Participant, decision);
        var snapshot = Array.AsReadOnly(next);
        var nextStatus = next.All(item => item.Decision == PactInvitationDecision.Accepted)
            ? LearningPactStatus.Active : LearningPactStatus.Pending;
        // All validation/allocation finishes before publishing either state change.
        Participants = snapshot;
        Status = nextStatus;
    }

    public void Cancel(LearningPactParticipant participant)
    {
        if (participant is null || participant != Proposer)
            throw new UnauthorizedAccessException("Only the designated Pact proposer may cancel.");
        RequirePending();
        Status = LearningPactStatus.Cancelled;
    }

    private int FindParticipant(LearningPactParticipant participant)
    {
        for (var i = 0; i < Participants.Count; i++)
            if (participant is not null && Participants[i].Participant == participant) return i;
        throw new UnauthorizedAccessException("A designated Learning Pact participant is required.");
    }

    private void RequirePending()
    {
        if (Status != LearningPactStatus.Pending) throw new InvalidOperationException("Only a pending Pact proposal supports this operation.");
    }

    public override string ToString() => $"Learning Pact ({Status}) [private terms and identities omitted]";

    /// <summary>Immutable invitation snapshot. Decision is not an authentication receipt.</summary>
    public sealed class Participation
    {
        public LearningPactParticipant Participant { get; }
        public PactInvitationDecision Decision { get; }
        internal Participation(LearningPactParticipant participant, PactInvitationDecision decision)
        { Participant = participant; Decision = decision; }
        public override string ToString() => $"Pact invitation ({Decision}) [private identity omitted]";
    }

    /// <summary>Immutable initial commitment terms, not a progress or completion record.</summary>
    public sealed class Commitment
    {
        public Guid Id { get; }
        public Guid PactId { get; }
        public string Text { get; }
        internal Commitment(Guid id, Guid pactId, string text) { Id = id; PactId = pactId; Text = text; }
        public override string ToString() => "Pact commitment [private identity and text omitted]";
    }
}
