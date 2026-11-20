using System.Reflection;
using Andril.Learning.Growth;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningMentorshipTests
{
    private static LearningMentorshipParticipant Person() => new(Guid.NewGuid(), Guid.NewGuid());
    private static LearningMentorship Create() => LearningMentorship.Request(Guid.NewGuid(), Person(), LearningMentorshipOffer.Create(Guid.NewGuid(), Person()));

    [Fact]
    public void VoluntaryOffer_BindsMentorAndLearningScopeWithoutEligibilityClaims()
    {
        var id = Guid.NewGuid(); var mentor = Person(); var offer = LearningMentorshipOffer.Create(id, mentor);
        Assert.Equal(id, offer.Id); Assert.Same(mentor, offer.Mentor); Assert.Equal("learning", offer.Module);
        Assert.Equal(new[] { "Id", "Mentor", "Module" }, typeof(LearningMentorshipOffer).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Participant_RequiresNonemptyAccountAndLearningProfile(bool emptyAccount)
    {
        Assert.Throws<ArgumentException>(() => new LearningMentorshipParticipant(
            emptyAccount ? Guid.Empty : Guid.NewGuid(), emptyAccount ? Guid.NewGuid() : Guid.Empty));
    }

    [Fact]
    public void Offer_RejectsEmptyIdentityOrMissingMentor()
    {
        Assert.Throws<ArgumentException>("id", () => LearningMentorshipOffer.Create(Guid.Empty, Person()));
        Assert.Throws<ArgumentNullException>("mentor", () => LearningMentorshipOffer.Create(Guid.NewGuid(), null!));
    }

    [Fact]
    public void LearnerRequest_IsBoundToSpecificOfferAndParticipants_AndInitiallyInactivePending()
    {
        var id = Guid.NewGuid(); var learner = Person(); var offer = LearningMentorshipOffer.Create(Guid.NewGuid(), Person());
        var request = LearningMentorship.Request(id, learner, offer);
        Assert.Equal(id, request.Id); Assert.Same(learner, request.Learner); Assert.Same(offer, request.Offer);
        Assert.Same(offer.Mentor, request.Mentor); Assert.Equal("learning", request.Module);
        Assert.Equal(LearningMentorshipStatus.Pending, request.Status); Assert.False(request.IsActive);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("learner")]
    [InlineData("offer")]
    public void Request_RejectsMissingIdentityOrReferences(string invalid)
    {
        var offer = LearningMentorshipOffer.Create(Guid.NewGuid(), Person());
        if (invalid == "id") Assert.Throws<ArgumentException>(() => LearningMentorship.Request(Guid.Empty, Person(), offer));
        else Assert.Throws<ArgumentNullException>(() => LearningMentorship.Request(Guid.NewGuid(),
            invalid == "learner" ? null! : Person(), invalid == "offer" ? null! : offer));
    }

    [Theory]
    [InlineData("both")]
    [InlineData("account")]
    [InlineData("profile")]
    public void SelfMentorshipOrContradictorySharedProfile_IsRejected(string shared)
    {
        var mentor = Person(); var learner = new LearningMentorshipParticipant(
            shared is "both" or "account" ? mentor.AccountId : Guid.NewGuid(),
            shared is "both" or "profile" ? mentor.LearningProfileId : Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => LearningMentorship.Request(Guid.NewGuid(), learner, LearningMentorshipOffer.Create(Guid.NewGuid(), mentor)));
    }

    [Theory]
    [InlineData("accept", LearningMentorshipStatus.Accepted, true)]
    [InlineData("decline", LearningMentorshipStatus.Declined, false)]
    [InlineData("cancel", LearningMentorshipStatus.Cancelled, false)]
    public void DesignatedParticipant_PerformsApprovedTransition_WithActivationOnlyOnMentorAcceptance(string action, LearningMentorshipStatus expected, bool active)
    {
        var request = Create(); var offer = request.Offer;
        Act(request, action, action == "cancel" ? request.Learner : request.Mentor);
        Assert.Equal(expected, request.Status); Assert.Equal(active, request.IsActive); Assert.Same(offer, request.Offer);
    }

    [Theory]
    [InlineData("accept", "role")]
    [InlineData("accept", "outsider")]
    [InlineData("accept", "account")]
    [InlineData("accept", "profile")]
    [InlineData("accept", "null")]
    [InlineData("decline", "role")]
    [InlineData("decline", "outsider")]
    [InlineData("decline", "account")]
    [InlineData("decline", "profile")]
    [InlineData("decline", "null")]
    [InlineData("cancel", "role")]
    [InlineData("cancel", "outsider")]
    [InlineData("cancel", "account")]
    [InlineData("cancel", "profile")]
    [InlineData("cancel", "null")]
    public void WrongRoleOrPartialParticipant_CannotMutateRequest(string action, string defect)
    {
        var request = Create(); var required = action == "cancel" ? request.Learner : request.Mentor;
        var actor = defect switch
        {
            "role" => action == "cancel" ? request.Mentor : request.Learner,
            "null" => null!,
            _ => new LearningMentorshipParticipant(defect == "account" ? required.AccountId : Guid.NewGuid(),
                defect == "profile" ? required.LearningProfileId : Guid.NewGuid())
        };
        var snapshot = Snapshot(request);
        Assert.Throws<UnauthorizedAccessException>(() => Act(request, action, actor));
        Assert.Equal(snapshot, Snapshot(request));
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
    public void RepeatedOrUnsupportedTransitions_PreserveEveryReferenceAndStatus(string initial, string next)
    {
        var request = Create(); Act(request, initial, initial == "cancel" ? request.Learner : request.Mentor);
        var snapshot = Snapshot(request);
        Assert.Throws<InvalidOperationException>(() => Act(request, next, next == "cancel" ? request.Learner : request.Mentor));
        Assert.Equal(snapshot, Snapshot(request));
    }

    [Fact]
    public void ExactPendingTerms_AreRecognizedIndependentOfRequestIdentity()
    {
        var request = Create();
        var other = LearningMentorship.Request(Guid.NewGuid(), new(request.Learner.AccountId, request.Learner.LearningProfileId),
            LearningMentorshipOffer.Create(request.Offer.Id, new(request.Mentor.AccountId, request.Mentor.LearningProfileId)));
        Assert.NotEqual(request.Id, other.Id);
        Assert.Equal(PendingMentorshipComparison.ExactDuplicate, request.ComparePendingRequest(other));
        Assert.Equal(PendingMentorshipComparison.ExactDuplicate, other.ComparePendingRequest(request));
    }

    [Theory]
    [InlineData("offer")]
    [InlineData("learner")]
    [InlineData("mentor")]
    [InlineData("reverse")]
    public void OtherPendingTerms_ArePolicyDecisions_NotPermissionForAnotherRequest(string difference)
    {
        var request = Create();
        var learner = difference == "reverse" ? request.Mentor : difference == "learner" ? Person() : request.Learner;
        var mentor = difference == "reverse" ? request.Learner : difference == "mentor" ? Person() : request.Mentor;
        var offer = LearningMentorshipOffer.Create(difference == "offer" ? Guid.NewGuid() : request.Offer.Id, mentor);
        var other = LearningMentorship.Request(Guid.NewGuid(), learner, offer);
        Assert.Equal(PendingMentorshipComparison.PolicyDecisionRequired, request.ComparePendingRequest(other));
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public void HistoricalRequests_AreNotCertifiedRepeatable(string terminal)
    {
        var request = Create(); var other = LearningMentorship.Request(Guid.NewGuid(), request.Learner, request.Offer);
        Act(request, terminal, terminal == "cancel" ? request.Learner : request.Mentor);
        Assert.Equal(PendingMentorshipComparison.NotBothPending, request.ComparePendingRequest(other));
        Assert.Equal(PendingMentorshipComparison.NotBothPending, other.ComparePendingRequest(request));
    }

    [Fact]
    public void NullComparison_IsRejectedWithoutMutation()
    {
        var request = Create(); var snapshot = Snapshot(request);
        Assert.Throws<ArgumentNullException>(() => request.ComparePendingRequest(null!));
        Assert.Equal(snapshot, Snapshot(request));
    }

    [Fact]
    public void ParticipantMatching_IsStructuralOnly_AndIndependentRequestsDoNotShareMutableState()
    {
        var request = Create(); var other = LearningMentorship.Request(request.Id, request.Learner, request.Offer);
        Assert.True(request.IsParticipant(new(request.Learner.AccountId, request.Learner.LearningProfileId)));
        Assert.True(request.IsParticipant(request.Mentor)); Assert.False(request.IsParticipant(null)); Assert.False(request.IsParticipant(Person()));
        request.Accept(request.Mentor);
        Assert.Equal(LearningMentorshipStatus.Pending, other.Status); Assert.False(other.IsActive);
        // Neither matching reference construction nor IsActive proves current M2 authorization or availability.
    }

    [Fact]
    public void PublicSurface_HasNoMutableIdentityOrUnapprovedEligibilityRewardAndCommunicationOperations()
    {
        foreach (var type in new[] { typeof(LearningMentorship), typeof(LearningMentorshipParticipant), typeof(LearningMentorshipOffer) })
        {
            Assert.True(type.IsSealed); Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), property => Assert.False(property.SetMethod?.IsPublic == true));
        }
        Assert.Empty(typeof(LearningMentorship).GetConstructors()); Assert.Empty(typeof(LearningMentorshipOffer).GetConstructors());
        Assert.Equal(new[] { "Accept", "Cancel", "ComparePendingRequest", "Decline", "IsParticipant", "ToString" },
            typeof(LearningMentorship).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { LearningMentorshipStatus.Pending, LearningMentorshipStatus.Accepted, LearningMentorshipStatus.Declined, LearningMentorshipStatus.Cancelled },
            Enum.GetValues<LearningMentorshipStatus>());
    }

    [Fact]
    public void Diagnostics_OmitPrivateRequestOfferAndParticipantIdentities()
    {
        var request = Create(); var diagnostic = request.ToString() + request.Offer + request.Mentor + request.Learner;
        foreach (var id in new[] { request.Id, request.Offer.Id, request.Mentor.AccountId, request.Mentor.LearningProfileId,
            request.Learner.AccountId, request.Learner.LearningProfileId }) Assert.DoesNotContain(id.ToString(), diagnostic);
    }

    private static object Snapshot(LearningMentorship request) =>
        (request.Id, request.Module, request.Learner, request.Mentor, request.Offer, request.Status, request.IsActive);
    private static void Act(LearningMentorship request, string action, LearningMentorshipParticipant actor)
    {
        switch (action)
        {
            case "accept": request.Accept(actor); break;
            case "decline": request.Decline(actor); break;
            case "cancel": request.Cancel(actor); break;
            default: throw new ArgumentException("Unknown test action.");
        }
    }
}
