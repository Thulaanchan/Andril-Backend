using System.Reflection;
using Andril.Learning.SkillExchange;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class SkillExchangeTests
{
    private static SkillExchangeParticipant Participant() => new(Guid.NewGuid(), Guid.NewGuid());
    private static SkillExchange Create() => SkillExchange.Propose(Guid.NewGuid(), Participant(), Participant(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Proposal_PreservesExactLearningParticipantsAndSkills_AndStartsPending()
    {
        var id = Guid.NewGuid(); var sender = Participant(); var recipient = Participant();
        var offered = Guid.NewGuid(); var wanted = Guid.NewGuid();
        var exchange = SkillExchange.Propose(id, sender, recipient, offered, wanted);
        Assert.Equal(id, exchange.Id);
        Assert.Equal("learning", exchange.Module);
        Assert.Same(sender, exchange.Sender); Assert.Same(recipient, exchange.Recipient);
        Assert.Equal(offered, exchange.OfferedSkillId); Assert.Equal(wanted, exchange.WantedSkillId);
        Assert.Equal(SkillExchangeStatus.Pending, exchange.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Participant_RequiresNonemptyAccountAndProfile(bool emptyAccount)
    {
        Assert.Throws<ArgumentException>(() => new SkillExchangeParticipant(
            emptyAccount ? Guid.Empty : Guid.NewGuid(), emptyAccount ? Guid.NewGuid() : Guid.Empty));
    }

    [Theory]
    [InlineData("exchange")]
    [InlineData("offered")]
    [InlineData("wanted")]
    public void Proposal_RejectsEmptyIdentity(string invalid)
    {
        Assert.Throws<ArgumentException>(() => SkillExchange.Propose(
            invalid == "exchange" ? Guid.Empty : Guid.NewGuid(), Participant(), Participant(),
            invalid == "offered" ? Guid.Empty : Guid.NewGuid(), invalid == "wanted" ? Guid.Empty : Guid.NewGuid()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Proposal_RejectsMissingParticipant(bool missingSender)
    {
        Assert.Throws<ArgumentNullException>(() => SkillExchange.Propose(Guid.NewGuid(),
            missingSender ? null! : Participant(), missingSender ? Participant() : null!, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("account")]
    [InlineData("profile")]
    public void SelfExchangeOrContradictorySharedProfile_IsRejected(string shared)
    {
        var sender = Participant();
        var recipient = new SkillExchangeParticipant(shared is "both" or "account" ? sender.AccountId : Guid.NewGuid(),
            shared is "both" or "profile" ? sender.LearningProfileId : Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SkillExchange.Propose(Guid.NewGuid(), sender, recipient, Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void SameOfferedAndWantedSkill_IsUnsupportedUntilOverlapPolicyExists()
    {
        var skill = Guid.NewGuid();
        Assert.Throws<NotSupportedException>(() => SkillExchange.Propose(Guid.NewGuid(), Participant(), Participant(), skill, skill));
    }

    [Theory]
    [InlineData("accept", SkillExchangeStatus.Accepted)]
    [InlineData("decline", SkillExchangeStatus.Declined)]
    [InlineData("cancel", SkillExchangeStatus.Cancelled)]
    public void DesignatedParticipant_CanPerformApprovedPendingTransition(string action, SkillExchangeStatus expected)
    {
        var exchange = Create();
        Act(exchange, action, action == "cancel" ? exchange.Sender : exchange.Recipient);
        Assert.Equal(expected, exchange.Status);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void WrongRoleOrOutsider_CannotMutateProposal(string action)
    {
        var exchange = Create();
        var wrongRole = action == "cancel" ? exchange.Recipient : exchange.Sender;
        var expected = Snapshot(exchange);
        foreach (var participant in new[] { wrongRole, Participant(), null! })
        {
            Assert.Throws<UnauthorizedAccessException>(() => Act(exchange, action, participant));
            Assert.Equal(expected, Snapshot(exchange));
        }
    }

    [Theory]
    [InlineData("accept", true)]
    [InlineData("accept", false)]
    [InlineData("decline", true)]
    [InlineData("decline", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    public void MatchingOnlyAccountOrProfile_IsInsufficientForDomainRole(string action, bool sameAccount)
    {
        var exchange = Create(); var required = action == "cancel" ? exchange.Sender : exchange.Recipient;
        var partial = new SkillExchangeParticipant(sameAccount ? required.AccountId : Guid.NewGuid(),
            sameAccount ? Guid.NewGuid() : required.LearningProfileId);
        Assert.Throws<UnauthorizedAccessException>(() => Act(exchange, action, partial));
        Assert.Equal(SkillExchangeStatus.Pending, exchange.Status);
    }

    [Theory]
    [InlineData("accept", "accept")]
    [InlineData("accept", "decline")]
    [InlineData("accept", "cancel")]
    [InlineData("decline", "accept")]
    [InlineData("decline", "decline")]
    [InlineData("decline", "cancel")]
    [InlineData("cancel", "accept")]
    [InlineData("cancel", "decline")]
    [InlineData("cancel", "cancel")]
    public void TerminalProposal_RejectsEverySubsequentTransitionAndPreservesState(string first, string next)
    {
        var exchange = Create();
        Act(exchange, first, first == "cancel" ? exchange.Sender : exchange.Recipient);
        var expected = Snapshot(exchange);
        Assert.Throws<InvalidOperationException>(() => Act(exchange, next, next == "cancel" ? exchange.Sender : exchange.Recipient));
        Assert.Equal(expected, Snapshot(exchange));
    }

    [Fact]
    public void ParticipantIdentification_UsesBothImmutableReferences_NotAuthentication()
    {
        var exchange = Create();
        Assert.True(exchange.IsParticipant(new(exchange.Sender.AccountId, exchange.Sender.LearningProfileId)));
        Assert.True(exchange.IsParticipant(exchange.Recipient));
        Assert.False(exchange.IsParticipant(null)); Assert.False(exchange.IsParticipant(Participant()));
        Assert.False(exchange.IsParticipant(new(exchange.Sender.AccountId, Guid.NewGuid())));
        Assert.False(exchange.IsParticipant(new(Guid.NewGuid(), exchange.Recipient.LearningProfileId)));
        // Forged matching references compare equally. M2 authentication remains mandatory outside this domain.
    }

    [Fact]
    public void ExactPendingTerms_AreRecognizedRegardlessOfProposalId()
    {
        var exchange = Create();
        var copy = SkillExchange.Propose(Guid.NewGuid(), new(exchange.Sender.AccountId, exchange.Sender.LearningProfileId),
            new(exchange.Recipient.AccountId, exchange.Recipient.LearningProfileId), exchange.OfferedSkillId, exchange.WantedSkillId);
        Assert.NotEqual(exchange.Id, copy.Id);
        Assert.Equal(PendingProposalComparison.ExactDuplicate, exchange.ComparePendingProposal(copy));
        Assert.Equal(PendingProposalComparison.ExactDuplicate, copy.ComparePendingProposal(exchange));
        Assert.Equal(PendingProposalComparison.ExactDuplicate, exchange.ComparePendingProposal(exchange));
        Assert.Equal(SkillExchangeStatus.Pending, exchange.Status);
    }

    [Theory]
    [InlineData("reverse", PendingProposalComparison.PendingPairConflict)]
    [InlineData("offered", PendingProposalComparison.PendingPairConflict)]
    [InlineData("wanted", PendingProposalComparison.PendingPairConflict)]
    [InlineData("recipient", PendingProposalComparison.DifferentPair)]
    public void PendingTerms_UseApprovedUnorderedProfilePairRule(string difference, PendingProposalComparison expected)
    {
        var exchange = Create();
        var other = SkillExchange.Propose(Guid.NewGuid(), difference == "reverse" ? exchange.Recipient : exchange.Sender,
            difference == "reverse" ? exchange.Sender : difference == "recipient" ? Participant() : exchange.Recipient,
            difference == "offered" ? Guid.NewGuid() : exchange.OfferedSkillId,
            difference == "wanted" ? Guid.NewGuid() : exchange.WantedSkillId);
        Assert.Equal(expected, exchange.ComparePendingProposal(other));
        Assert.Equal(expected, other.ComparePendingProposal(exchange));
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void HistoricalTerms_AreNotAPermissionToRepeat(string terminal)
    {
        var exchange = Create();
        var copy = SkillExchange.Propose(Guid.NewGuid(), exchange.Sender, exchange.Recipient, exchange.OfferedSkillId, exchange.WantedSkillId);
        Act(exchange, terminal, terminal == "cancel" ? exchange.Sender : exchange.Recipient);
        Assert.Equal(PendingProposalComparison.NotBothPending, exchange.ComparePendingProposal(copy));
        Assert.Equal(PendingProposalComparison.NotBothPending, copy.ComparePendingProposal(exchange));
    }

    [Fact]
    public void Comparison_RejectsMissingReferenceWithoutMutatingState()
    {
        var exchange = Create(); var expected = Snapshot(exchange);
        Assert.Throws<ArgumentNullException>(() => exchange.ComparePendingProposal(null!));
        Assert.Equal(expected, Snapshot(exchange));
    }

    [Fact]
    public void PublicSurface_HasNoMutableIdentityCollectionsOrAutomaticPermissionSideEffects()
    {
        foreach (var type in new[] { typeof(SkillExchange), typeof(SkillExchangeParticipant) })
        {
            Assert.Empty(type.GetFields(BindingFlags.Instance | BindingFlags.Public));
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        }
        Assert.Equal(new[] { "Accept", "Cancel", "ComparePendingProposal", "Decline", "IsParticipant", "ToString" },
            typeof(SkillExchange).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { SkillExchangeStatus.Pending, SkillExchangeStatus.Accepted, SkillExchangeStatus.Declined, SkillExchangeStatus.Cancelled },
            Enum.GetValues<SkillExchangeStatus>());
        // This closed, dependency-free domain has no persistence, notification, XP, connection or chat APIs.
    }

    [Fact]
    public void Diagnostics_OmitPrivateReferences()
    {
        var exchange = Create(); var text = exchange.ToString() + exchange.Sender + exchange.Recipient;
        foreach (var id in new[] { exchange.Id, exchange.Sender.AccountId, exchange.Sender.LearningProfileId,
            exchange.Recipient.AccountId, exchange.Recipient.LearningProfileId, exchange.OfferedSkillId, exchange.WantedSkillId })
            Assert.DoesNotContain(id.ToString(), text);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("reverse")]
    [InlineData("different-terms")]
    [InlineData("reverse-different-terms")]
    [InlineData("changed-account-references")]
    public void PairGuard_RejectsEverySamePairPendingProposalWithoutMutatingHistory(string variant)
    {
        var existing = Create(); var before = Snapshot(existing);
        var reverse = variant.StartsWith("reverse", StringComparison.Ordinal);
        var sender = reverse ? existing.Recipient : existing.Sender;
        var recipient = reverse ? existing.Sender : existing.Recipient;
        if (variant == "changed-account-references")
        {
            sender = new(Guid.NewGuid(), sender.LearningProfileId);
            recipient = new(Guid.NewGuid(), recipient.LearningProfileId);
        }
        var changedTerms = variant.Contains("different-terms", StringComparison.Ordinal);
        var history = new[] { existing };
        var error = Assert.Throws<InvalidOperationException>(() => SkillExchange.Propose(Guid.NewGuid(), sender, recipient,
            changedTerms ? Guid.NewGuid() : existing.OfferedSkillId,
            changedTerms ? Guid.NewGuid() : existing.WantedSkillId, history));
        Assert.Equal(before, Snapshot(existing)); Assert.Same(existing, Assert.Single(history));
        foreach (var id in new[] { existing.Id, sender.AccountId, sender.LearningProfileId, recipient.AccountId,
            recipient.LearningProfileId, existing.OfferedSkillId, existing.WantedSkillId })
            Assert.DoesNotContain(id.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("accept", false)]
    [InlineData("accept", true)]
    [InlineData("decline", false)]
    [InlineData("decline", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    public void PairGuard_TerminalHistoryAllowsFreshExplicitProposalWithoutChangingHistory(string terminal, bool reverse)
    {
        var existing = Create();
        Act(existing, terminal, terminal == "cancel" ? existing.Sender : existing.Recipient);
        var before = Snapshot(existing); var id = Guid.NewGuid();
        var sender = reverse ? existing.Recipient : existing.Sender;
        var recipient = reverse ? existing.Sender : existing.Recipient;
        var next = SkillExchange.Propose(id, sender, recipient, Guid.NewGuid(), Guid.NewGuid(), [existing]);
        Assert.Equal(id, next.Id); Assert.NotEqual(existing.Id, next.Id);
        Assert.Equal(SkillExchangeStatus.Pending, next.Status);
        Assert.Same(sender, next.Sender); Assert.Same(recipient, next.Recipient);
        Assert.Equal(before, Snapshot(existing));
        // Fresh local construction is not a grant of current eligibility or production access.
    }

    [Theory]
    [InlineData("sender")]
    [InlineData("recipient")]
    [InlineData("neither")]
    [InlineData("same-accounts-new-profiles")]
    public void PairGuard_DifferentProfilePairsRemainIndependent(string shared)
    {
        var existing = Create(); var before = Snapshot(existing);
        var sender = shared == "sender" ? existing.Sender : Participant();
        var recipient = shared == "recipient" ? existing.Recipient : Participant();
        if (shared == "same-accounts-new-profiles")
        {
            sender = new(existing.Sender.AccountId, Guid.NewGuid());
            recipient = new(existing.Recipient.AccountId, Guid.NewGuid());
        }
        var next = SkillExchange.Propose(Guid.NewGuid(), sender, recipient, existing.OfferedSkillId,
            existing.WantedSkillId, [existing]);
        Assert.Equal(PendingProposalComparison.DifferentPair, existing.ComparePendingProposal(next));
        Assert.Equal(PendingProposalComparison.DifferentPair, next.ComparePendingProposal(existing));
        Assert.Equal(before, Snapshot(existing)); Assert.Equal(SkillExchangeStatus.Pending, next.Status);
    }

    [Fact]
    public void PairGuard_LaterPendingConflictIsNotHiddenByUnrelatedOrTerminalHistory()
    {
        var pending = Create();
        var terminal = SkillExchange.Propose(Guid.NewGuid(), pending.Sender, pending.Recipient,
            pending.OfferedSkillId, pending.WantedSkillId);
        terminal.Cancel(terminal.Sender);
        var history = new[] { Create(), terminal, pending };
        var before = history.Select(Snapshot).ToArray();
        Assert.Throws<InvalidOperationException>(() => SkillExchange.Propose(Guid.NewGuid(), pending.Recipient,
            pending.Sender, Guid.NewGuid(), Guid.NewGuid(), history));
        Assert.Equal(before, history.Select(Snapshot));
    }

    [Fact]
    public void PairGuard_EmptySuppliedHistoryAllowsOnlyLocalConstruction()
    {
        var sender = Participant(); var recipient = Participant();
        var proposal = SkillExchange.Propose(Guid.NewGuid(), sender, recipient, Guid.NewGuid(), Guid.NewGuid(), []);
        Assert.Equal(SkillExchangeStatus.Pending, proposal.Status);
        Assert.Same(sender, proposal.Sender); Assert.Same(recipient, proposal.Recipient);
        Assert.Equal("learning", proposal.Module);
        // Empty caller input does not prove that authoritative storage contains no pending pair.
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing-entry")]
    [InlineData("enumeration")]
    public void PairGuard_InvalidOrFailedHistoryCannotReturnAProposal(string defect)
    {
        var existing = Create(); var before = Snapshot(existing);
        IEnumerable<SkillExchange> FailingHistory()
        {
            yield return existing;
            throw new InvalidOperationException("Synthetic history enumeration failure.");
        }
        IEnumerable<SkillExchange>? history = defect switch
        {
            "null" => null, "missing-entry" => new[] { existing, null! }, _ => FailingHistory()
        };
        var error = Record.Exception(() => SkillExchange.Propose(Guid.NewGuid(), Participant(), Participant(),
            Guid.NewGuid(), Guid.NewGuid(), history!));
        if (defect == "null") Assert.IsType<ArgumentNullException>(error);
        else if (defect == "missing-entry") Assert.IsType<ArgumentException>(error);
        else
        {
            var failure = Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("Synthetic history enumeration failure.", failure.Message);
        }
        Assert.Equal(before, Snapshot(existing));
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("missing-sender")]
    [InlineData("self")]
    [InlineData("empty-skill")]
    [InlineData("same-skill")]
    public void PairGuard_PreservesExistingStructuralValidationBeforeReadingHistory(string defect)
    {
        var sender = Participant(); var recipient = defect == "self" ? sender : Participant();
        var offered = Guid.NewGuid(); var wanted = defect == "same-skill" ? offered : Guid.NewGuid();
        var reads = 0;
        IEnumerable<SkillExchange> History() { reads++; yield return Create(); }
        var error = Record.Exception(() => SkillExchange.Propose(defect == "empty-id" ? Guid.Empty : Guid.NewGuid(),
            defect == "missing-sender" ? null! : sender, recipient,
            defect == "empty-skill" ? Guid.Empty : offered, wanted, History()));
        Assert.NotNull(error);
        if (defect == "same-skill") Assert.IsType<NotSupportedException>(error);
        else Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void PairGuard_EnumeratesSuppliedHistoryOnceWithoutConsumingOrChangingItsEntries()
    {
        var existing = Create(); var before = Snapshot(existing); var reads = 0;
        IEnumerable<SkillExchange> History()
        {
            if (++reads != 1) throw new InvalidOperationException("History enumerated again.");
            yield return existing;
        }
        var next = SkillExchange.Propose(Guid.NewGuid(), Participant(), Participant(), Guid.NewGuid(), Guid.NewGuid(), History());
        Assert.Equal(1, reads); Assert.Equal(before, Snapshot(existing));
        Assert.Equal(SkillExchangeStatus.Pending, next.Status);
    }

    private static object Snapshot(SkillExchange exchange) =>
        (exchange.Id, exchange.Sender, exchange.Recipient, exchange.OfferedSkillId, exchange.WantedSkillId, exchange.Status, exchange.Module);

    private static void Act(SkillExchange exchange, string action, SkillExchangeParticipant participant)
    {
        switch (action)
        {
            case "accept": exchange.Accept(participant); break;
            case "decline": exchange.Decline(participant); break;
            case "cancel": exchange.Cancel(participant); break;
            default: throw new ArgumentException("Unknown test action.");
        }
    }
}
