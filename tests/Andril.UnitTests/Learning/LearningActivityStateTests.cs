using System.Text.Json;
using System.Text.Json.Nodes;
using Andril.Learning.Activities;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningActivityStateTests
{
    private readonly LearningActivityMember owner = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly LearningActivityMember peer = new(Guid.NewGuid(), Guid.NewGuid());
    private readonly LearningActivityRevision revision = new(Guid.NewGuid(), 1);
    private CreateLearningActivityRequest Request(LearningActivityKind kind) => kind switch
    {
        LearningActivityKind.SkillExchange => new([peer.ProfileId], Guid.NewGuid(), Guid.NewGuid()),
        LearningActivityKind.Pact => new([peer.ProfileId], Commitments: ["first private commitment", "second private commitment"]),
        LearningActivityKind.GrowthSelfReport => new(SkillIds: [Guid.NewGuid()]),
        LearningActivityKind.MentorshipRequest => new([peer.ProfileId], RelatedOfferId: Guid.NewGuid()),
        LearningActivityKind.Circle => new(CircleId: Guid.NewGuid()),
        _ => new()
    };
    private LearningActivityState Create(LearningActivityKind kind)
    {
        var paired = kind is LearningActivityKind.SkillExchange or LearningActivityKind.Pact or LearningActivityKind.MentorshipRequest;
        return LearningActivityState.Create(kind, owner, paired ? [owner, peer] : [owner], Request(kind));
    }

    [Fact]
    public void UnknownStoredFieldsCannotBeSilentlyDiscardedDuringRehydration()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(Create(LearningActivityKind.GrowthSelfReport)))!;
        document["unapprovedLegacyField"] = "private data without a current field contract";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LearningActivityState>(document.ToJsonString()));
    }

    [Theory]
    [InlineData(LearningActivityKind.SkillExchange)]
    [InlineData(LearningActivityKind.Pact)]
    [InlineData(LearningActivityKind.GrowthSelfReport)]
    [InlineData(LearningActivityKind.MentorshipOffer)]
    [InlineData(LearningActivityKind.MentorshipRequest)]
    [InlineData(LearningActivityKind.Circle)]
    public void UnapprovedCrossKindContentIsRejectedInsteadOfSilentlyDiscarded(LearningActivityKind kind)
    {
        var paired = kind is LearningActivityKind.SkillExchange or LearningActivityKind.Pact or LearningActivityKind.MentorshipRequest;
        var request = kind == LearningActivityKind.SkillExchange ? Request(kind) with { Commitments = ["unapproved content"] }
            : kind == LearningActivityKind.Pact ? Request(kind) with { SkillIds = [Guid.NewGuid()] }
            : Request(kind) with { OfferedSkillId = Guid.NewGuid() };
        var error = Assert.Throws<ArgumentException>(() => LearningActivityState.Create(kind, owner, paired ? [owner, peer] : [owner], request));
        Assert.Contains("Fields are not supported", error.Message);
    }

    [Fact]
    public void UnrelatedPrivateMutationTextIsNeverStoredInCommandHistory()
    {
        var exchange = Create(LearningActivityKind.SkillExchange);
        Assert.Throws<ArgumentException>(() => exchange.Apply(owner, new(LearningActivityAction.Cancel, revision, Text: "not an approved exchange message")));
        Assert.Empty(exchange.Events);
        var circle = Create(LearningActivityKind.Circle); circle.Apply(owner, new(LearningActivityAction.CreateGoal, revision, Text: "goal"));
        var goal = Assert.Single(circle.Project(revision).Goals); var count = circle.Events.Count;
        Assert.Throws<ArgumentException>(() => circle.Apply(owner, new(LearningActivityAction.UpdateGoalProgress, revision, goal.Id, "unapproved progress note", 50)));
        Assert.Equal(count, circle.Events.Count); Assert.Equal(0, Assert.Single(circle.Project(revision).Goals).ProgressPercent);
    }

    [Theory]
    [InlineData(LearningActivityKind.SkillExchange)]
    [InlineData(LearningActivityKind.Pact)]
    [InlineData(LearningActivityKind.GrowthSelfReport)]
    [InlineData(LearningActivityKind.MentorshipOffer)]
    [InlineData(LearningActivityKind.MentorshipRequest)]
    [InlineData(LearningActivityKind.Circle)]
    public void CreationAndReopeningKeepCanonicalIdentityAndPrivateProjection(LearningActivityKind kind)
    {
        var state = Create(kind); var first = state.Project(revision);
        var reopened = JsonSerializer.Deserialize<LearningActivityState>(JsonSerializer.Serialize(state))!;
        var result = reopened.Project(revision);
        Assert.Equal(state.Id, result.Id); Assert.Equal(kind, result.Kind); Assert.Equal(revision, result.Revision);
        Assert.Equal(first.Status, result.Status); Assert.Equal(first.Commitments, result.Commitments);
        Assert.Equal(first.SkillIds, result.SkillIds);
        Assert.DoesNotContain(owner.AccountId.ToString(), result.ToString());
        Assert.DoesNotContain("private commitment", result.ToString());
        Assert.DoesNotContain("AccountId", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(LearningActivityAction.Accept, "Accepted")]
    [InlineData(LearningActivityAction.Decline, "Declined")]
    [InlineData(LearningActivityAction.Cancel, "Cancelled")]
    public void ExchangeTransitionsPreserveDesignatedParticipantRules(LearningActivityAction action, string status)
    {
        var state = Create(LearningActivityKind.SkillExchange);
        state.Apply(action == LearningActivityAction.Cancel ? owner : peer, new(action, revision));
        Assert.Equal(status, state.Project(revision).Status);
        Assert.Throws<InvalidOperationException>(() => state.Apply(owner, new(LearningActivityAction.Cancel, revision)));
        Assert.Single(state.Events);
    }

    [Fact]
    public void DeniedExchangeTransitionPreservesExistingPendingState()
    {
        var state = Create(LearningActivityKind.SkillExchange);
        Assert.Throws<UnauthorizedAccessException>(() => state.Apply(owner, new(LearningActivityAction.Accept, revision)));
        Assert.Empty(state.Events); Assert.Equal("Pending", state.Project(revision).Status);
        Assert.Throws<UnauthorizedAccessException>(() => state.Apply(new(Guid.NewGuid(), Guid.NewGuid()), new(LearningActivityAction.Cancel, revision)));
        Assert.Empty(state.Events);
    }

    [Theory]
    [InlineData(LearningActivityAction.Read)]
    [InlineData(LearningActivityAction.List)]
    [InlineData(LearningActivityAction.Create)]
    public void ReadAndCreationOperationsCannotBeSmuggledIntoMutationCommands(LearningActivityAction action)
    {
        var state = Create(LearningActivityKind.SkillExchange);
        Assert.Throws<ArgumentException>(() => state.Apply(owner, new(action, revision)));
        Assert.Empty(state.Events); Assert.Equal("Pending", state.Project(revision).Status);
    }

    [Fact]
    public void PactRequiresInvitationAcceptanceAndEveryCompletionConfirmation()
    {
        var state = Create(LearningActivityKind.Pact); var commitments = state.Project(revision).Commitments.ToArray();
        state.Apply(peer, new(LearningActivityAction.Accept, revision)); Assert.Equal("Active", state.Project(revision).Status);
        state.Apply(owner, new(LearningActivityAction.ConfirmCompletion, revision)); Assert.Equal("Active", state.Project(revision).Status);
        state.Apply(peer, new(LearningActivityAction.ConfirmCompletion, revision)); Assert.Equal("Completed", state.Project(revision).Status);
        Assert.Equal(commitments, state.Project(revision).Commitments);
        var reopened = JsonSerializer.Deserialize<LearningActivityState>(JsonSerializer.Serialize(state))!;
        Assert.Equal("Completed", reopened.Project(revision).Status); Assert.Equal(commitments, reopened.Project(revision).Commitments);
    }

    [Theory]
    [InlineData(LearningActivityKind.GrowthSelfReport)]
    [InlineData(LearningActivityKind.MentorshipOffer)]
    public void UndefinedImmutableRecordMutationRemainsUnavailable(LearningActivityKind kind)
    {
        var state = Create(kind);
        Assert.Throws<NotSupportedException>(() => state.Apply(owner, new(LearningActivityAction.Cancel, revision)));
        Assert.Empty(state.Events);
    }

    [Theory]
    [InlineData(LearningActivityAction.Accept, "Accepted")]
    [InlineData(LearningActivityAction.Decline, "Declined")]
    [InlineData(LearningActivityAction.Cancel, "Cancelled")]
    public void MentorshipUsesExistingVoluntaryOfferAndParticipantTransitions(LearningActivityAction action, string status)
    {
        var state = Create(LearningActivityKind.MentorshipRequest);
        state.Apply(action == LearningActivityAction.Cancel ? owner : peer, new(action, revision));
        Assert.Equal(status, state.Project(revision).Status);
    }

    [Fact]
    public void CircleGoalCanonicalIdsOrderAndContentSurviveReopeningAndEditing()
    {
        var state = Create(LearningActivityKind.Circle);
        state.Apply(owner, new(LearningActivityAction.CreateGoal, revision, Text: "first"));
        state.Apply(owner, new(LearningActivityAction.CreateGoal, revision, Text: "second"));
        var before = state.Project(revision).Goals;
        state = JsonSerializer.Deserialize<LearningActivityState>(JsonSerializer.Serialize(state))!;
        state.Apply(owner, new(LearningActivityAction.EditGoal, revision, before[0].Id, "edited"));
        state.Apply(owner, new(LearningActivityAction.UpdateGoalProgress, revision, before[0].Id, ProgressPercent: 75));
        var after = state.Project(revision).Goals;
        Assert.Equal(before.Select(x => x.Id), after.Select(x => x.Id)); Assert.Equal("edited", after[0].Text);
        Assert.Equal(75, after[0].ProgressPercent); Assert.Equal(before[1], after[1]);
        var count = state.Events.Count;
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Apply(owner, new(LearningActivityAction.UpdateGoalProgress, revision, before[0].Id, ProgressPercent: 101)));
        Assert.Equal(count, state.Events.Count); Assert.Equal(after, state.Project(revision).Goals);
    }
}
