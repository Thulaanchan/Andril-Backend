using System.Reflection;
using Andril.Learning.Pacts;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningPactTests
{
    private static LearningPactParticipant Person() => new(Guid.NewGuid(), Guid.NewGuid());
    private static LearningPact Create(int count = 3)
    {
        var people = Enumerable.Range(0, count).Select(_ => Person()).ToArray();
        return LearningPact.Propose(Guid.NewGuid(), people[0], people, ["Learn together", "Practice together"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Proposal_PreservesLearningIdentityAndTerms_AndStartsPendingWithOnlyProposerAgreed(int proposerIndex)
    {
        var people = new[] { Person(), Person(), Person() }; var id = Guid.NewGuid();
        var pact = LearningPact.Propose(id, people[proposerIndex], people, ["Initial commitment"]);
        Assert.Equal(id, pact.Id); Assert.Equal("learning", pact.Module);
        Assert.Equal(people[proposerIndex], pact.Proposer);
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        Assert.Equal(people, pact.Participants.Select(item => item.Participant));
        Assert.All(pact.Participants, item => Assert.Equal(item.Participant == pact.Proposer
            ? PactInvitationDecision.Accepted : PactInvitationDecision.Pending, item.Decision));
    }

    [Fact]
    public void EmptyPactIdentity_IsRejected()
    {
        var proposer = Person();
        Assert.Throws<ArgumentException>("id", () => LearningPact.Propose(Guid.Empty, proposer, [proposer, Person()], []));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Participant_RequiresNonemptyAccountAndProfile(bool emptyAccount)
    {
        Assert.Throws<ArgumentException>(() => new LearningPactParticipant(
            emptyAccount ? Guid.Empty : Guid.NewGuid(), emptyAccount ? Guid.NewGuid() : Guid.Empty));
    }

    [Theory]
    [InlineData("proposer")]
    [InlineData("participants")]
    [InlineData("commitments")]
    public void MissingProposalInput_IsRejected(string missing)
    {
        var proposer = Person();
        Assert.Throws<ArgumentNullException>(() => LearningPact.Propose(Guid.NewGuid(), missing == "proposer" ? null! : proposer,
            missing == "participants" ? null! : new[] { proposer, Person() }, missing == "commitments" ? null! : Array.Empty<string>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FewerThanTwoParticipants_IsRejected(int count)
    {
        var proposer = Person();
        Assert.Throws<ArgumentException>("participants", () => LearningPact.Propose(Guid.NewGuid(), proposer,
            count == 0 ? Array.Empty<LearningPactParticipant>() : new[] { proposer }, []));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("null")]
    public void DuplicateOrInvalidParticipantEntries_AreRejected(string defect)
    {
        var proposer = Person();
        var other = defect == "null" ? null! : new LearningPactParticipant(
            defect is "both" or "account" ? proposer.AccountId : Guid.NewGuid(),
            defect is "both" or "profile" ? proposer.LearningProfileId : Guid.NewGuid());
        Assert.Throws<ArgumentException>("participants", () => LearningPact.Propose(Guid.NewGuid(), proposer, [proposer, other], []));
    }

    [Theory]
    [InlineData("neither")]
    [InlineData("account")]
    [InlineData("profile")]
    public void ProposerMustBeIncludedWithExactAccountAndProfile(string shared)
    {
        var proposer = Person();
        var partial = new LearningPactParticipant(shared == "account" ? proposer.AccountId : Guid.NewGuid(),
            shared == "profile" ? proposer.LearningProfileId : Guid.NewGuid());
        Assert.Throws<ArgumentException>("participants", () => LearningPact.Propose(Guid.NewGuid(), proposer, [partial, Person()], []));
    }

    [Fact]
    public void TwoParticipants_ActivateOnlyWhenInviteeAccepts()
    {
        var pact = Create(2); var invitee = pact.Participants[1].Participant;
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        pact.AcceptInvitation(invitee);
        Assert.Equal(LearningPactStatus.Active, pact.Status);
        Assert.All(pact.Participants, item => Assert.Equal(PactInvitationDecision.Accepted, item.Decision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialAgreement_DoesNotActivate_AndLastRequiredAgreementDoes(bool reverseOrder)
    {
        var pact = Create(); var before = pact.Participants;
        var first = reverseOrder ? 2 : 1; var last = reverseOrder ? 1 : 2;
        pact.AcceptInvitation(before[first].Participant);
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        Assert.Equal(PactInvitationDecision.Accepted, pact.Participants[first].Decision);
        Assert.Equal(PactInvitationDecision.Pending, pact.Participants[last].Decision);
        Assert.All(before.Skip(1), item => Assert.Equal(PactInvitationDecision.Pending, item.Decision));
        pact.AcceptInvitation(before[last].Participant);
        Assert.Equal(LearningPactStatus.Active, pact.Status);
    }

    [Fact]
    public void Decline_IsRetainedWithoutInventingOverallTermination_AndCannotCountTowardUnanimity()
    {
        var pact = Create(); var declined = pact.Participants[1].Participant;
        pact.DeclineInvitation(declined);
        Assert.Equal(PactInvitationDecision.Declined, pact.Participants[1].Decision);
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        pact.AcceptInvitation(pact.Participants[2].Participant);
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        Assert.Equal(PactInvitationDecision.Declined, pact.Participants[1].Decision);
    }

    [Theory]
    [InlineData("accept", "accept")]
    [InlineData("accept", "decline")]
    [InlineData("decline", "accept")]
    [InlineData("decline", "decline")]
    public void RecordedDecision_CannotBeRepeatedOrChanged(string first, string next)
    {
        var pact = Create(); var invitee = pact.Participants[1].Participant;
        Act(pact, first, invitee); var snapshot = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => Act(pact, next, invitee));
        Assert.Equal(snapshot, Snapshot(pact));
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public void ProposerCannotActAsAnUndecidedInvitee(string action)
    {
        var pact = Create(); var snapshot = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => Act(pact, action, pact.Proposer));
        Assert.Equal(snapshot, Snapshot(pact));
    }

    [Theory]
    [InlineData("accept", "outsider")]
    [InlineData("accept", "account")]
    [InlineData("accept", "profile")]
    [InlineData("accept", "null")]
    [InlineData("decline", "outsider")]
    [InlineData("decline", "account")]
    [InlineData("decline", "profile")]
    [InlineData("decline", "null")]
    [InlineData("cancel", "outsider")]
    [InlineData("cancel", "account")]
    [InlineData("cancel", "profile")]
    [InlineData("cancel", "null")]
    public void WrongOrPartialParticipantReference_CannotMutatePact(string action, string defect)
    {
        var pact = Create(); var required = action == "cancel" ? pact.Proposer : pact.Participants[1].Participant;
        var actor = defect == "null" ? null! : new LearningPactParticipant(
            defect == "account" ? required.AccountId : Guid.NewGuid(), defect == "profile" ? required.LearningProfileId : Guid.NewGuid());
        var snapshot = Snapshot(pact);
        Assert.Throws<UnauthorizedAccessException>(() => Act(pact, action, actor));
        Assert.Equal(snapshot, Snapshot(pact));
    }

    [Fact]
    public void DecisionAffectsOnlyTheMatchingInvitee_AndMixedIdentitiesCannotActForAnother()
    {
        var pact = Create(); var first = pact.Participants[1].Participant; var second = pact.Participants[2].Participant;
        Assert.Throws<UnauthorizedAccessException>(() => pact.AcceptInvitation(new(first.AccountId, second.LearningProfileId)));
        pact.AcceptInvitation(first);
        Assert.Equal(PactInvitationDecision.Accepted, pact.Participants[1].Decision);
        Assert.Equal(PactInvitationDecision.Pending, pact.Participants[2].Decision);
        Assert.Equal(LearningPactStatus.Pending, pact.Status);
        // Only one actor reference is accepted; no separate target permits accepting on another's behalf.
        // Forged fully matching references still need M2 authentication outside this domain.
    }

    [Fact]
    public void InviteeCannotCancel_AndProposerCanCancelPendingWithPriorDecisionsPreserved()
    {
        var pact = Create(); var invitee = pact.Participants[1].Participant;
        Assert.Throws<UnauthorizedAccessException>(() => pact.Cancel(invitee));
        pact.DeclineInvitation(invitee); var decisions = pact.Participants; var terms = pact.Commitments;
        pact.Cancel(pact.Proposer);
        Assert.Equal(LearningPactStatus.Cancelled, pact.Status);
        Assert.Same(decisions, pact.Participants); Assert.Same(terms, pact.Commitments);
    }

    [Theory]
    [InlineData(false, "accept")]
    [InlineData(false, "decline")]
    [InlineData(false, "cancel")]
    [InlineData(true, "accept")]
    [InlineData(true, "decline")]
    [InlineData(true, "cancel")]
    public void ActiveOrCancelledPact_RejectsFurtherTransitionsAndPreservesState(bool active, string action)
    {
        var pact = Create(2); var invitee = pact.Participants[1].Participant;
        if (active) pact.AcceptInvitation(invitee); else pact.Cancel(pact.Proposer);
        var snapshot = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => Act(pact, action, action == "cancel" ? pact.Proposer : invitee));
        Assert.Equal(snapshot, Snapshot(pact));
    }

    [Fact]
    public void PendingPactWithPartialAgreement_CanBeCancelledWithoutActivation()
    {
        var pact = Create(); pact.AcceptInvitation(pact.Participants[1].Participant);
        pact.Cancel(pact.Proposer);
        var snapshot = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => pact.AcceptInvitation(pact.Participants[2].Participant));
        Assert.Equal(snapshot, Snapshot(pact));
    }

    [Fact]
    public void InitialCommitments_SupportMultipleImmutableIdentitiesAndExactTextWithoutProgressClaims()
    {
        var proposer = Person(); var texts = new[] { "  Learn தமிழ்  ", "සිංහල e\u0301 👩🏽‍💻" };
        var pact = LearningPact.Propose(Guid.NewGuid(), proposer, [proposer, Person()], texts);
        Assert.Equal(texts, pact.Commitments.Select(item => item.Text));
        Assert.Equal(2, pact.Commitments.Select(item => item.Id).Distinct().Count());
        Assert.All(pact.Commitments, item => { Assert.NotEqual(Guid.Empty, item.Id); Assert.Equal(pact.Id, item.PactId); });
        texts[0] = "Changed outside";
        Assert.Equal("  Learn தமிழ்  ", pact.Commitments[0].Text);
        var terms = pact.Commitments;
        pact.AcceptInvitation(pact.Participants[1].Participant);
        Assert.Same(terms, pact.Commitments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void InvalidCommitmentText_IsRejectedAtConstruction(string? text)
    {
        var proposer = Person();
        Assert.Throws<ArgumentException>("commitmentTexts", () => LearningPact.Propose(Guid.NewGuid(), proposer, [proposer, Person()], ["Valid", text!]));
    }

    [Fact]
    public void NoCommitmentMinimumIsInvented()
    {
        var proposer = Person();
        Assert.Empty(LearningPact.Propose(Guid.NewGuid(), proposer, [proposer, Person()], []).Commitments);
    }

    [Fact]
    public void Participants_AreCopiedAndAllReturnedCollectionsRejectExternalMutations()
    {
        var people = new[] { Person(), Person(), Person() }; var proposer = people[0]; var invited = people[1];
        var pact = LearningPact.Propose(Guid.NewGuid(), proposer, people, ["Keep"]);
        people[1] = Person();
        Assert.Equal(invited, pact.Participants[1].Participant);
        var invitations = (IList<LearningPact.Participation>)pact.Participants;
        Assert.Throws<NotSupportedException>(() => invitations.Clear());
        Assert.Throws<NotSupportedException>(() => invitations.Add(invitations[0]));
        Assert.Throws<NotSupportedException>(() => invitations[1] = invitations[0]);
        var commitments = (IList<LearningPact.Commitment>)pact.Commitments;
        Assert.Throws<NotSupportedException>(() => commitments.Clear());
        Assert.Throws<NotSupportedException>(() => commitments.Add(commitments[0]));
        Assert.Throws<NotSupportedException>(() => commitments[0] = commitments[0]);
    }

    [Fact]
    public void ParticipantIdentification_IsStructuralOnly_AndDoesNotShareStateBetweenPacts()
    {
        var pact = Create();
        Assert.True(pact.IsParticipant(new(pact.Proposer.AccountId, pact.Proposer.LearningProfileId)));
        Assert.False(pact.IsParticipant(Person())); Assert.False(pact.IsParticipant(null));
        var people = pact.Participants.Select(item => item.Participant).ToArray();
        var other = LearningPact.Propose(pact.Id, pact.Proposer, people, []);
        pact.AcceptInvitation(people[1]);
        Assert.Equal(PactInvitationDecision.Pending, other.Participants[1].Decision);
    }

    [Fact]
    public void PublicSurface_HasNoMutableStateAndOnlyApprovedCompletionAndWithdrawalOperations()
    {
        foreach (var type in new[] { typeof(LearningPact), typeof(LearningPactParticipant), typeof(LearningPact.Participation), typeof(LearningPact.Commitment) })
        {
            Assert.True(type.IsSealed); Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        }
        Assert.Empty(typeof(LearningPact).GetConstructors());
        Assert.Empty(typeof(LearningPact.Participation).GetConstructors());
        Assert.Empty(typeof(LearningPact.Commitment).GetConstructors());
        Assert.Equal(new[] { "AcceptInvitation", "Cancel", "ConfirmCompletion", "DeclineInvitation", "IsParticipant", "ToString", "Withdraw" },
            typeof(LearningPact).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { LearningPactStatus.Pending, LearningPactStatus.Active, LearningPactStatus.Cancelled, LearningPactStatus.Completed }, Enum.GetValues<LearningPactStatus>());
    }

    [Fact]
    public void Diagnostics_OmitPrivateParticipantIdentitiesAndCommitmentText()
    {
        var pact = Create(); var text = pact.ToString() + pact.Proposer + string.Join(" ", pact.Participants) + string.Join(" ", pact.Commitments);
        Assert.DoesNotContain(pact.Id.ToString(), text);
        foreach (var item in pact.Participants)
        {
            Assert.DoesNotContain(item.Participant.AccountId.ToString(), text);
            Assert.DoesNotContain(item.Participant.LearningProfileId.ToString(), text);
        }
        foreach (var commitment in pact.Commitments)
        {
            Assert.DoesNotContain(commitment.Id.ToString(), text); Assert.DoesNotContain(commitment.Text, text);
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    public void Completion_RequiresEveryExplicitReportIncludingProposer(int count, bool reverse)
    {
        var pact = CreateActive(count); var terms = pact.Commitments; var initial = pact.Participants;
        Assert.All(initial, item => Assert.False(item.CompletionConfirmed));
        var order = reverse ? initial.Reverse().ToArray() : initial.ToArray();
        for (var i = 0; i < order.Length; i++)
        {
            var before = pact.Participants;
            var participant = order[i].Participant;
            pact.ConfirmCompletion(participant);
            Assert.Equal(i == order.Length - 1 ? LearningPactStatus.Completed : LearningPactStatus.Active, pact.Status);
            Assert.Equal(i + 1, pact.Participants.Count(item => item.CompletionConfirmed));
            Assert.All(pact.Participants, item => Assert.Equal(PactInvitationDecision.Accepted, item.Decision));
            for (var j = 0; j < before.Count; j++)
            {
                if (before[j].Participant == participant)
                {
                    Assert.False(before[j].CompletionConfirmed); Assert.True(pact.Participants[j].CompletionConfirmed);
                    Assert.NotSame(before[j], pact.Participants[j]);
                }
                else Assert.Same(before[j], pact.Participants[j]);
            }
            Assert.Same(terms, pact.Commitments);
        }
        Assert.All(initial, item => Assert.False(item.CompletionConfirmed));
        Assert.Equal("learning", pact.Module);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DuplicateConfirmation_CannotInflateReportsOrChangeState(int selected)
    {
        var pact = CreateActive(); var participant = pact.Participants[selected].Participant;
        pact.ConfirmCompletion(participant); var before = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => pact.ConfirmCompletion(
            new(participant.AccountId, participant.LearningProfileId)));
        Assert.Equal(before, Snapshot(pact));
        Assert.Equal(1, pact.Participants.Count(item => item.CompletionConfirmed));
        Assert.Equal(LearningPactStatus.Active, pact.Status);
        foreach (var other in pact.Participants.Where(item => !item.CompletionConfirmed).ToArray())
            pact.ConfirmCompletion(other.Participant);
        Assert.Equal(LearningPactStatus.Completed, pact.Status);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ActiveParticipantWithdrawal_CancelsWithoutChangingTermsOrReports(int selected, bool partial)
    {
        var pact = CreateActive();
        if (partial) pact.ConfirmCompletion(pact.Participants[(selected + 1) % 3].Participant);
        var participants = pact.Participants; var commitments = pact.Commitments;
        pact.Withdraw(participants[selected].Participant);
        Assert.Equal(LearningPactStatus.Cancelled, pact.Status);
        Assert.Same(participants, pact.Participants); Assert.Same(commitments, pact.Commitments);
        Assert.Equal(partial ? 1 : 0, pact.Participants.Count(item => item.CompletionConfirmed));
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("withdraw")]
    public void PendingPact_RejectsCompletionAndWithdrawalWithoutChangingState(string action)
    {
        var pact = Create(); var before = Snapshot(pact);
        Assert.Throws<InvalidOperationException>(() => Act(pact, action, pact.Proposer));
        Assert.Equal(before, Snapshot(pact));
        Assert.All(pact.Participants, item => Assert.False(item.CompletionConfirmed));
    }

    [Theory]
    [InlineData("confirm", "outsider")]
    [InlineData("confirm", "account")]
    [InlineData("confirm", "profile")]
    [InlineData("confirm", "mixed")]
    [InlineData("confirm", "null")]
    [InlineData("withdraw", "outsider")]
    [InlineData("withdraw", "account")]
    [InlineData("withdraw", "profile")]
    [InlineData("withdraw", "mixed")]
    [InlineData("withdraw", "null")]
    public void CompletionAndWithdrawal_RejectNonparticipantReferences(string action, string defect)
    {
        var pact = CreateActive(); var person = pact.Participants[1].Participant;
        var actor = defect switch
        {
            "null" => null!,
            "account" => new LearningPactParticipant(person.AccountId, Guid.NewGuid()),
            "profile" => new LearningPactParticipant(Guid.NewGuid(), person.LearningProfileId),
            "mixed" => new LearningPactParticipant(person.AccountId, pact.Proposer.LearningProfileId),
            _ => Person()
        };
        var before = Snapshot(pact);
        Assert.Throws<UnauthorizedAccessException>(() => Act(pact, action, actor));
        Assert.Equal(before, Snapshot(pact));
        Assert.All(pact.Participants, item => Assert.False(item.CompletionConfirmed));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled-pending")]
    [InlineData("cancelled-active")]
    public void TerminalPact_RejectsAllInvitationCompletionAndWithdrawalOperations(string terminal)
    {
        var pact = terminal == "cancelled-pending" ? Create() : CreateActive();
        if (terminal == "completed")
            foreach (var person in pact.Participants.ToArray()) pact.ConfirmCompletion(person.Participant);
        else if (terminal == "cancelled-active") pact.Withdraw(pact.Proposer);
        else pact.Cancel(pact.Proposer);
        var before = Snapshot(pact);
        foreach (var action in new[] { "accept", "decline", "cancel", "confirm", "withdraw" })
        {
            var actor = action is "accept" or "decline" ? pact.Participants[1].Participant : pact.Proposer;
            Assert.Throws<InvalidOperationException>(() => Act(pact, action, actor));
            Assert.Equal(before, Snapshot(pact));
        }
    }

    [Fact]
    public void CompletionReports_AreImmutableAndDoNotShareStateAcrossPactsWithMatchingReferences()
    {
        var pact = CreateActive(); var people = pact.Participants.Select(item => item.Participant).ToArray();
        var other = LearningPact.Propose(pact.Id, pact.Proposer, people, []);
        foreach (var person in people.Skip(1)) other.AcceptInvitation(person);
        var old = pact.Participants;
        pact.ConfirmCompletion(pact.Proposer);
        Assert.All(old, item => Assert.False(item.CompletionConfirmed));
        Assert.All(other.Participants, item => Assert.False(item.CompletionConfirmed));
        Assert.Throws<NotSupportedException>(() => ((IList<LearningPact.Participation>)pact.Participants).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<LearningPact.Participation>)pact.Participants)[0] = old[0]);
        other.Withdraw(other.Proposer);
        Assert.Equal(LearningPactStatus.Active, pact.Status); Assert.Equal(LearningPactStatus.Cancelled, other.Status);
        // Fully matching supplied references still do not authenticate or authorize a production caller.
    }

    [Fact]
    public async Task ConcurrentFinalConfirmationAndWithdrawal_CannotOverwriteATerminalOutcome()
    {
        var pact = CreateActive();
        foreach (var person in pact.Participants.Take(2).ToArray()) pact.ConfirmCompletion(person.Participant);
        var before = pact.Participants; var last = before[2].Participant;
        var outcomes = await Task.WhenAll(
            Task.Run(() => Record.Exception(() => pact.ConfirmCompletion(last))),
            Task.Run(() => Record.Exception(() => pact.Withdraw(last))));
        Assert.Single(outcomes, error => error is null);
        Assert.IsType<InvalidOperationException>(Assert.Single(outcomes, error => error is not null));
        Assert.Contains(pact.Status, new[] { LearningPactStatus.Completed, LearningPactStatus.Cancelled });
        Assert.Equal(pact.Status == LearningPactStatus.Completed ? 3 : 2,
            pact.Participants.Count(item => item.CompletionConfirmed));
        Assert.False(before[2].CompletionConfirmed);
    }

    [Fact]
    public async Task ConcurrentDuplicateConfirmations_CountTheParticipantOnce()
    {
        var pact = CreateActive(); var person = pact.Proposer;
        var outcomes = await Task.WhenAll(
            Task.Run(() => Record.Exception(() => pact.ConfirmCompletion(person))),
            Task.Run(() => Record.Exception(() => pact.ConfirmCompletion(person))));
        Assert.Single(outcomes, error => error is null);
        Assert.IsType<InvalidOperationException>(Assert.Single(outcomes, error => error is not null));
        Assert.Equal(1, pact.Participants.Count(item => item.CompletionConfirmed));
        Assert.Equal(LearningPactStatus.Active, pact.Status);
    }

    [Fact]
    public async Task ConcurrentDistinctConfirmations_PreserveEveryReportAndCompleteUnanimously()
    {
        var pact = CreateActive(); var before = pact.Participants;
        await Task.WhenAll(before.Select(item => Task.Run(() => pact.ConfirmCompletion(item.Participant))));
        Assert.Equal(LearningPactStatus.Completed, pact.Status);
        Assert.All(pact.Participants, item => Assert.True(item.CompletionConfirmed));
        Assert.All(before, item => Assert.False(item.CompletionConfirmed));
    }

    private static LearningPact CreateActive(int count = 3)
    {
        var pact = Create(count);
        foreach (var person in pact.Participants.Where(item => item.Participant != pact.Proposer).ToArray())
            pact.AcceptInvitation(person.Participant);
        return pact;
    }

    private static object Snapshot(LearningPact pact) => (pact.Id, pact.Module, pact.Proposer, pact.Status, pact.Participants, pact.Commitments);
    private static void Act(LearningPact pact, string action, LearningPactParticipant actor)
    {
        switch (action)
        {
            case "accept": pact.AcceptInvitation(actor); break;
            case "decline": pact.DeclineInvitation(actor); break;
            case "cancel": pact.Cancel(actor); break;
            case "confirm": pact.ConfirmCompletion(actor); break;
            case "withdraw": pact.Withdraw(actor); break;
            default: throw new ArgumentException("Unknown test action.");
        }
    }
}
