using System;
using System.Text.Json;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public class SocialRelationshipTransitionPolicyTests
{
    private static (Guid ProfileA, Guid ProfileB, SocialProfilePairKey PairKey) CreatePair()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        return (idA, idB, SocialProfilePairKey.Create(idA, idB));
    }

    private static SocialDiscoveryCallerEvidence CreateCallerEvidence(
        Guid callerProfileId,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasAuthoritativeEvidence = true)
    {
        return new SocialDiscoveryCallerEvidence(
            callerProfileId,
            age,
            isOptedIn,
            isActive,
            hasAuthoritativeEvidence);
    }

    private static SocialDiscoveryCandidateEvidence CreateCandidateEvidence(
        Guid candidateProfileId,
        int age = 26,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasAuthoritativeEvidence = true)
    {
        return new SocialDiscoveryCandidateEvidence(
            candidateProfileId,
            age,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasAuthoritativeEvidence);
    }

    private static SocialDirectionalInterest CreateInterest(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive)
    {
        return new SocialDirectionalInterest(
            pairKey,
            actingProfileId,
            isPositive,
            DateTime.UtcNow);
    }

    [Fact]
    public void EvaluateTransition_ReciprocalPositive_ReturnsEligibleForMatch()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var reciprocal = CreateInterest(pair, idB, isPositive: true);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: reciprocal,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.EligibleForMatch, result.Status);
        Assert.Equal(SocialRelationshipState.Matched, result.ResultingState);
        Assert.True(result.CanFormMatch);
        Assert.False(result.IsIdempotent);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_PositiveActor_NullReciprocal_ReturnsPendingUnilateral()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.PendingUnilateral, result.Status);
        Assert.Equal(SocialRelationshipState.PendingUnilateral, result.ResultingState);
        Assert.False(result.CanFormMatch);
        Assert.False(result.IsIdempotent);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_PositiveActor_NegativeReciprocal_ReturnsPassed()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var reciprocal = CreateInterest(pair, idB, isPositive: false);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: reciprocal,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.Passed, result.Status);
        Assert.Equal(SocialRelationshipState.Passed, result.ResultingState);
        Assert.False(result.CanFormMatch);
        Assert.False(result.IsIdempotent);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_NegativeActor_AnyReciprocal_ReturnsPassed()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var reciprocal = CreateInterest(pair, idB, isPositive: true);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: false,
            existingActingInterest: null,
            reciprocalInterest: reciprocal,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.Passed, result.Status);
        Assert.Equal(SocialRelationshipState.Passed, result.ResultingState);
        Assert.False(result.CanFormMatch);
        Assert.False(result.IsIdempotent);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_ExistingMatch_PositiveSignal_ReturnsAlreadyMatchedIdempotent()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var existingMatch = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: existingMatch,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.AlreadyMatched, result.Status);
        Assert.Equal(SocialRelationshipState.Matched, result.ResultingState);
        Assert.False(result.CanFormMatch);
        Assert.True(result.IsIdempotent);
        Assert.False(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_ExistingMatch_NegativeSignal_ReturnsPassed()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var existingMatch = SocialMatchEntity.Create(Guid.NewGuid(), pair, DateTime.UtcNow);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: false,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: existingMatch,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.Passed, result.Status);
        Assert.Equal(SocialRelationshipState.Passed, result.ResultingState);
        Assert.False(result.CanFormMatch);
        Assert.False(result.IsIdempotent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvaluateTransition_BlockedParticipants_ReturnsBlockedAndDenied(bool callerBlockedCand)
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(
            idB,
            isBlockedByCaller: callerBlockedCand,
            isCallerBlockedByCandidate: !callerBlockedCand);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: CreateInterest(pair, idB, isPositive: true),
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.Blocked, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Theory]
    [InlineData(17, 25)]
    [InlineData(25, 17)]
    public void EvaluateTransition_UnderageParticipant_ReturnsUnderageAndDenied(int actorAge, int candAge)
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA, age: actorAge);
        var candEvidence = CreateCandidateEvidence(idB, age: candAge);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: CreateInterest(pair, idB, isPositive: true),
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.Underage, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EvaluateTransition_NotOptedInParticipant_ReturnsNotOptedInAndDenied(bool actorOptedIn, bool candOptedIn)
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA, isOptedIn: actorOptedIn);
        var candEvidence = CreateCandidateEvidence(idB, isOptedIn: candOptedIn);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: CreateInterest(pair, idB, isPositive: true),
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.NotOptedIn, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EvaluateTransition_InactiveParticipant_ReturnsNotActiveAndDenied(bool actorActive, bool candActive)
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA, isActive: actorActive);
        var candEvidence = CreateCandidateEvidence(idB, isActive: candActive);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: CreateInterest(pair, idB, isPositive: true),
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.NotActive, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Fact]
    public void EvaluateTransition_CandidateHidden_ReturnsCandidateHiddenAndDenied()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB, isDiscoverable: false);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: CreateInterest(pair, idB, isPositive: true),
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.CandidateHidden, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Fact]
    public void EvaluateTransition_MissingAuthoritativeEvidence_FailsClosed()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA, hasAuthoritativeEvidence: false);
        var candEvidence = CreateCandidateEvidence(idB, hasAuthoritativeEvidence: true);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.MissingEvidence, result.Status);
        Assert.True(result.IsDenied);
        Assert.False(result.CanFormMatch);
    }

    [Fact]
    public void EvaluateTransition_NullEvidence_FailsClosed()
    {
        var (idA, idB, pair) = CreatePair();
        var candEvidence = CreateCandidateEvidence(idB);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: null,
            null, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.MissingEvidence, result.Status);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_InvalidPairKey_ReturnsInvalidPair()
    {
        var (idA, idB, pair) = CreatePair();
        var outsiderId = Guid.NewGuid();
        var actorEvidence = CreateCallerEvidence(outsiderId);
        var candEvidence = CreateCandidateEvidence(idB);

        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, outsiderId, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: null,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.InvalidPair, result.Status);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public void EvaluateTransition_CompatibilityScoreIndependence_SucceedsOnConsentAlone()
    {
        var (idA, idB, pair) = CreatePair();
        var actorEvidence = CreateCallerEvidence(idA);
        var candEvidence = CreateCandidateEvidence(idB);
        var reciprocal = CreateInterest(pair, idB, isPositive: true);

        // Neither compatibility score nor personality profile is provided or required
        var result = SocialRelationshipTransitionPolicy.EvaluateTransition(
            pair, idA, actingInterestPositive: true,
            existingActingInterest: null,
            reciprocalInterest: reciprocal,
            existingMatch: null,
            actorEvidence, candEvidence);

        Assert.Equal(SocialRelationshipTransitionStatus.EligibleForMatch, result.Status);
        Assert.True(result.CanFormMatch);
    }

    [Fact]
    public void SocialRelationshipTransitionResult_JsonSerialization_ConcealsSensitiveState()
    {
        var result = new SocialRelationshipTransitionResult(
            SocialRelationshipTransitionStatus.EligibleForMatch,
            SocialRelationshipState.Matched);

        var json = JsonSerializer.Serialize(result);
        Assert.Equal("{}", json);
        Assert.Equal("SocialRelationshipTransitionResult [private]", result.ToString());
    }
}
