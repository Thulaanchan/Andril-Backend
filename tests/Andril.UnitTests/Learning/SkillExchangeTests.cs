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
    [InlineData("reverse")]
    [InlineData("offered")]
    [InlineData("wanted")]
    [InlineData("recipient")]
    public void OtherPendingTerms_RemainPolicyDecisions_NotCertifiedDistinct(string difference)
    {
        var exchange = Create();
        var other = SkillExchange.Propose(Guid.NewGuid(), difference == "reverse" ? exchange.Recipient : exchange.Sender,
            difference == "reverse" ? exchange.Sender : difference == "recipient" ? Participant() : exchange.Recipient,
            difference == "offered" ? Guid.NewGuid() : exchange.OfferedSkillId,
            difference == "wanted" ? Guid.NewGuid() : exchange.WantedSkillId);
        Assert.Equal(PendingProposalComparison.PolicyDecisionRequired, exchange.ComparePendingProposal(other));
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
