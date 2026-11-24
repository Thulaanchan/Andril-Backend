using System;
using System.Text.Json;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialRelationshipRevocationPolicyTests
{
    private static (Guid ProfileA, Guid ProfileB, SocialProfilePairKey PairKey) CreatePair()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        return (idA, idB, SocialProfilePairKey.Create(idA, idB));
    }

    private static SocialRelationshipSnapshot CreateSnapshot(
        SocialProfilePairKey pairKey,
        SocialRelationshipState state,
        SocialMatchEntity? match = null)
    {
        return new SocialRelationshipSnapshot(pairKey, state, match, DateTime.UtcNow);
    }

    [Fact]
    public void EvaluateRevocation_InitiatedByParticipantA_TerminatesActiveMatch()
    {
        var (idA, idB, pair) = CreatePair();
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Matched, match);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, idA, snapshot, SocialRevocationReason.UserInitiated);

        Assert.Equal(SocialRevocationStatus.Success, decision.Status);
        Assert.Equal(SocialRelationshipState.Revoked, decision.ResultingState);
        Assert.True(decision.IsAllowed);
        Assert.True(decision.MatchTerminated);
        Assert.False(decision.IsDenied);
        Assert.Equal(SocialRevocationReason.UserInitiated, decision.Reason);
    }

    [Fact]
    public void EvaluateRevocation_InitiatedByParticipantB_TerminatesActiveMatch()
    {
        var (idA, idB, pair) = CreatePair();
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Matched, match);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, idB, snapshot, SocialRevocationReason.UserInitiated);

        Assert.Equal(SocialRevocationStatus.Success, decision.Status);
        Assert.Equal(SocialRelationshipState.Revoked, decision.ResultingState);
        Assert.True(decision.IsAllowed);
        Assert.True(decision.MatchTerminated);
        Assert.False(decision.IsDenied);
        Assert.Equal(SocialRevocationReason.UserInitiated, decision.Reason);
    }

    [Fact]
    public void EvaluateRevocation_OutsiderThirdParty_StrictlyDenied()
    {
        var (idA, idB, pair) = CreatePair();
        var outsiderId = Guid.NewGuid();
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Matched, match);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, outsiderId, snapshot, SocialRevocationReason.UserInitiated);

        Assert.Equal(SocialRevocationStatus.UnauthorizedOutsider, decision.Status);
        Assert.Equal(SocialRelationshipState.Matched, decision.ResultingState);
        Assert.False(decision.IsAllowed);
        Assert.False(decision.MatchTerminated);
        Assert.True(decision.IsDenied);
    }

    [Fact]
    public void EvaluateRevocation_NullOrEmptyParameters_ReturnsInvalidPair()
    {
        var (idA, idB, pair) = CreatePair();
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Matched);

        var nullPair = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            null, idA, snapshot);
        Assert.Equal(SocialRevocationStatus.InvalidPair, nullPair.Status);
        Assert.True(nullPair.IsDenied);

        var emptyActor = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, Guid.Empty, snapshot);
        Assert.Equal(SocialRevocationStatus.InvalidPair, emptyActor.Status);
        Assert.True(emptyActor.IsDenied);
    }

    [Fact]
    public void EvaluateRevocation_SnapshotMismatch_ReturnsInvalidPair()
    {
        var (idA, idB, pair1) = CreatePair();
        var (_, _, pair2) = CreatePair();
        var snapshotOther = CreateSnapshot(pair2, SocialRelationshipState.Matched);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair1, idA, snapshotOther);

        Assert.Equal(SocialRevocationStatus.InvalidPair, decision.Status);
        Assert.True(decision.IsDenied);
    }

    [Theory]
    [InlineData(SocialRevocationReason.Blocked)]
    [InlineData(SocialRevocationReason.OptedOut)]
    [InlineData(SocialRevocationReason.AccountDeactivated)]
    [InlineData(SocialRevocationReason.UnderageDetected)]
    public void EvaluateRevocation_SystemAndSafetyReasons_TerminatesActiveMatch(SocialRevocationReason reason)
    {
        var (idA, idB, pair) = CreatePair();
        var match = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Matched, match);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, idA, snapshot, reason);

        Assert.Equal(SocialRevocationStatus.Success, decision.Status);
        Assert.Equal(SocialRelationshipState.Revoked, decision.ResultingState);
        Assert.True(decision.MatchTerminated);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void EvaluateRevocation_AlreadyRevokedState_IdempotentSuccess()
    {
        var (idA, idB, pair) = CreatePair();
        var snapshot = CreateSnapshot(pair, SocialRelationshipState.Revoked);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, idA, snapshot, SocialRevocationReason.UserInitiated);

        Assert.Equal(SocialRevocationStatus.AlreadyRevoked, decision.Status);
        Assert.Equal(SocialRelationshipState.Revoked, decision.ResultingState);
        Assert.True(decision.IsAllowed);
        Assert.False(decision.MatchTerminated);
        Assert.False(decision.IsDenied);
    }

    [Theory]
    [InlineData(SocialRelationshipState.None)]
    [InlineData(SocialRelationshipState.PendingUnilateral)]
    [InlineData(SocialRelationshipState.Passed)]
    public void EvaluateRevocation_NonMatchedState_IdempotentSafeOutcome(SocialRelationshipState state)
    {
        var (idA, idB, pair) = CreatePair();
        var snapshot = CreateSnapshot(pair, state);

        var decision = SocialRelationshipRevocationPolicy.EvaluateRevocation(
            pair, idA, snapshot, SocialRevocationReason.UserInitiated);

        Assert.Equal(SocialRevocationStatus.NotMatched, decision.Status);
        Assert.Equal(state, decision.ResultingState);
        Assert.True(decision.IsAllowed);
        Assert.False(decision.MatchTerminated);
        Assert.False(decision.IsDenied);
    }

    [Fact]
    public void SocialRevocationDecision_JsonSerialization_ConcealsReasonAndState()
    {
        var decision = new SocialRevocationDecision(
            SocialRevocationStatus.Success,
            SocialRelationshipState.Revoked,
            SocialRevocationReason.Blocked);

        var json = JsonSerializer.Serialize(decision);
        Assert.Equal("{}", json);
        Assert.Equal("SocialRevocationDecision [private]", decision.ToString());
    }
}
