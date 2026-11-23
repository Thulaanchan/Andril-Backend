using System;
using System.Text.Json;
using Andril.Social.Matching;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Security boundary and authorization tests verifying that Social directional interest write preflight
/// operates strictly fail-closed, protects underage users, enforces module opt-in and active standing,
/// requires bidirectional block clearance, validates participant evidence binding, prevents mismatched pair scope,
/// conceals private identity state, and never authorizes chat, messaging, or mutual matching (CF-20 / SOC-02).
/// </summary>
public sealed class SocialInterestWritePreflightSecurityTests
{
    private static readonly Guid ActorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ForeignId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime DecisionTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static SocialProfilePairKey CreatePair(Guid? id1 = null, Guid? id2 = null) =>
        new(id1 ?? ActorId, id2 ?? CandidateId);

    private static SocialDiscoveryCallerEvidence CreateValidActor(
        Guid? profileId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasEvidence = true) =>
        new(profileId ?? ActorId, age, isOptedIn, isActive, hasEvidence);

    private static SocialDiscoveryCandidateEvidence CreateValidCandidate(
        Guid? profileId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasEvidence = true) =>
        new(profileId ?? CandidateId, age, isOptedIn, isActive, isDiscoverable, isBlockedByCaller, isCallerBlockedByCandidate, hasEvidence);

    private static SocialDirectionalInterest CreateInterest(
        SocialProfilePairKey pairKey,
        Guid? actingProfileId = null,
        bool isPositive = true,
        DateTime? recordedAtUtc = null) =>
        new(pairKey, actingProfileId ?? ActorId, isPositive, recordedAtUtc ?? DecisionTime);

    #region 1. Baseline Valid Adult Pairs & No Communication Grant

    [Theory]
    [InlineData(true)]  // Positive decision (like)
    [InlineData(false)] // Negative decision (pass)
    public void ValidAdultActivePair_EvaluatesEligible_WithoutGrantingChatOrMatch(bool isPositive)
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey, isPositive: isPositive);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.True(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.Eligible, result.Status);
        Assert.NotNull(result.DiscoveryResult);
        Assert.True(result.DiscoveryResult.IsEligible);

        // Security invariant: A preflight positive or negative decision is strictly structural/policy eligibility.
        // It must NOT grant chat permissions, messaging evidence, or mutual match tokens.
        Assert.DoesNotContain("ChatAllowed", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuthorizedMatch", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OverloadWithoutExpectedPair_ValidAdultActivePair_EvaluatesEligible()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey, isPositive: true);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(interest, actor, candidate);

        Assert.True(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.Eligible, result.Status);
    }

    #endregion

    #region 2. Underage Protection (Fails Closed for < 18)

    [Fact]
    public void UnderageActor_FailsClosedWithActorUnderage()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(age: 17);
        var candidate = CreateValidCandidate(age: 22);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.ActorUnderage, result.Status);
    }

    [Fact]
    public void UnderageCandidate_FailsClosedWithCandidateUnderage()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(age: 24);
        var candidate = CreateValidCandidate(age: 17);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.CandidateUnderage, result.Status);
    }

    #endregion

    #region 3. Module Opt-In & Active Profile Standing

    [Fact]
    public void ActorNotOptedIn_FailsClosedWithActorNotOptedIn()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(isOptedIn: false);
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.ActorNotOptedIn, result.Status);
    }

    [Fact]
    public void CandidateNotOptedIn_FailsClosedWithCandidateNotOptedIn()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(isOptedIn: false);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.CandidateNotOptedIn, result.Status);
    }

    [Fact]
    public void ActorNotActive_FailsClosedWithActorNotActive()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(isActive: false);
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.ActorNotActive, result.Status);
    }

    [Fact]
    public void CandidateNotActive_FailsClosedWithCandidateNotActive()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(isActive: false);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.CandidateNotActive, result.Status);
    }

    #endregion

    #region 4. Discoverability & Bidirectional Blocks

    [Fact]
    public void CandidateHidden_FailsClosedWithCandidateHidden()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(isDiscoverable: false);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.CandidateHidden, result.Status);
    }

    [Fact]
    public void CandidateBlockedByActor_FailsClosedWithBlockedByActor()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(isBlockedByCaller: true);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.BlockedByActor, result.Status);
    }

    [Fact]
    public void ActorBlockedByCandidate_FailsClosedWithBlockedByCandidate()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(isCallerBlockedByCandidate: true);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.BlockedByCandidate, result.Status);
    }

    #endregion

    #region 5. Identity Alignment, Mismatched Evidence & Pair Key Scoping

    [Fact]
    public void ForeignActorEvidence_FailsClosedWithActingProfileMismatch()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(profileId: ForeignId);
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.ActingProfileMismatch, result.Status);
    }

    [Fact]
    public void ForeignCandidateEvidence_FailsClosedWithTargetProfileMismatch()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(profileId: ForeignId);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.TargetProfileMismatch, result.Status);
    }

    [Fact]
    public void MismatchedExpectedPairKey_FailsClosedWithPairMismatch()
    {
        var pairKey = CreatePair();
        var foreignPairKey = CreatePair(ActorId, ForeignId);
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(foreignPairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.PairMismatch, result.Status);
    }

    #endregion

    #region 6. Authoritative Evidence Requirement (Fail-Closed)

    [Fact]
    public void MissingActorAuthoritativeEvidence_FailsClosedWithMissingEvidence()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor(hasEvidence: false);
        var candidate = CreateValidCandidate();

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.MissingEvidence, result.Status);
    }

    [Fact]
    public void MissingCandidateAuthoritativeEvidence_FailsClosedWithMissingEvidence()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate(hasEvidence: false);

        var result = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialInterestWritePreflightStatus.MissingEvidence, result.Status);
    }

    #endregion

    #region 7. Private ID Confidentiality & Diagnostics Privacy

    [Fact]
    public void ResultToString_ConcealsConfidentialIdentities()
    {
        var result = SocialInterestWritePreflightResult.Success();

        var str = result.ToString();

        Assert.Equal("SocialInterestWritePreflightResult [private]", str);
        Assert.DoesNotContain(ActorId.ToString(), str);
        Assert.DoesNotContain(CandidateId.ToString(), str);
    }

    [Fact]
    public void ResultSerialization_ConcealsConfidentialIdentities()
    {
        var result = SocialInterestWritePreflightResult.Success();

        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"IsEligible\":true", json);
        Assert.DoesNotContain(ActorId.ToString(), json);
        Assert.DoesNotContain(CandidateId.ToString(), json);
    }

    #endregion

    #region 8. Argument Validation & Pure Offline Invariants

    [Fact]
    public void NullArguments_ThrowArgumentNullException()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate();

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(null!, interest, actor, candidate));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(pairKey, null!, actor, candidate));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, null!, candidate));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, null!));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(null!, actor, candidate));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(interest, null!, candidate));

        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestWritePreflightPolicy.Evaluate(interest, actor, null!));
    }

    [Fact]
    public void PolicyEvaluation_HasNoSideEffects_IsIdempotent()
    {
        var pairKey = CreatePair();
        var interest = CreateInterest(pairKey);
        var actor = CreateValidActor();
        var candidate = CreateValidCandidate();

        var result1 = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);
        var result2 = SocialInterestWritePreflightPolicy.Evaluate(pairKey, interest, actor, candidate);

        Assert.True(result1.IsEligible);
        Assert.True(result2.IsEligible);
        Assert.Equal(result1.Status, result2.Status);
        Assert.Equal(result1, result2);
    }

    #endregion
}
