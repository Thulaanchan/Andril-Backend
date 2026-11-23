using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialDiscoveryEligibilityPreFilterTests
{
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CandidateB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SocialDiscoveryCallerEvidence CreateDefaultCaller(
        Guid? callerId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasEvidence = true)
    {
        return new SocialDiscoveryCallerEvidence(
            callerId ?? CallerId,
            age,
            isOptedIn,
            isActive,
            hasEvidence);
    }

    private static SocialDiscoveryCandidateEvidence CreateDefaultCandidate(
        Guid? candidateId = null,
        int age = 24,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasEvidence = true)
    {
        return new SocialDiscoveryCandidateEvidence(
            candidateId ?? CandidateA,
            age,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasEvidence);
    }

    #region 1. Argument & Construction Validation

    [Fact]
    public void Evaluate_NullCaller_ThrowsArgumentNullException()
    {
        var candidate = CreateDefaultCandidate();
        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryEligibilityPreFilter.Evaluate(null!, candidate));
    }

    [Fact]
    public void Evaluate_NullCandidate_ThrowsArgumentNullException()
    {
        var caller = CreateDefaultCaller();
        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryEligibilityPreFilter.Evaluate(caller, null!));
    }

    [Fact]
    public void CallerEvidence_EmptyGuid_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialDiscoveryCallerEvidence(Guid.Empty, 25, true, true));
        Assert.Equal("callerProfileId", ex.ParamName);
    }

    [Fact]
    public void CandidateEvidence_EmptyGuid_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialDiscoveryCandidateEvidence(Guid.Empty, 25, true, true, true, false, false));
        Assert.Equal("candidateProfileId", ex.ParamName);
    }

    [Fact]
    public void FilterEligible_NullCandidates_ThrowsArgumentNullException()
    {
        var caller = CreateDefaultCaller();
        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryEligibilityPreFilter.FilterEligible(caller, null!));
    }

    [Fact]
    public void FilterEligible_CollectionWithNullElement_ThrowsArgumentException()
    {
        var caller = CreateDefaultCaller();
        var candidates = new SocialDiscoveryCandidateEvidence[] { CreateDefaultCandidate(), null! };
        Assert.Throws<ArgumentException>(() =>
            SocialDiscoveryEligibilityPreFilter.FilterEligible(caller, candidates));
    }

    #endregion

    #region 2. Positive Baseline Eligibility

    [Fact]
    public void Evaluate_AllCriteriaSatisfied_ReturnsEligible()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate();

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.True(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Eligible, result.Status);
        Assert.Equal(SocialDiscoveryIneligibilityReason.None, result.Reasons);
    }

    #endregion

    #region 3. Age Eligibility Invariants

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(0)]
    public void Evaluate_CallerUnderage_ReturnsIneligibleWithCallerUnderageReason(int callerAge)
    {
        var caller = CreateDefaultCaller(age: callerAge);
        var candidate = CreateDefaultCandidate();

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(15)]
    [InlineData(10)]
    public void Evaluate_CandidateUnderage_ReturnsIneligibleWithCandidateUnderageReason(int candidateAge)
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(age: candidateAge);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage));
    }

    [Fact]
    public void Evaluate_BothUnderage_ReturnsIneligibleWithBothUnderageReasons()
    {
        var caller = CreateDefaultCaller(age: 17);
        var candidate = CreateDefaultCandidate(age: 17);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage));
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage));
    }

    #endregion

    #region 4. Identity Scope Invariants

    [Fact]
    public void Evaluate_SameProfileIdentity_ReturnsIneligibleWithSameProfileReason()
    {
        var caller = CreateDefaultCaller(callerId: CallerId);
        var candidate = CreateDefaultCandidate(candidateId: CallerId);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.SameProfile));
    }

    #endregion

    #region 5. Opt-In & Active Standing Invariants

    [Fact]
    public void Evaluate_CallerNotOptedIn_ReturnsIneligibleWithCallerNotOptedIn()
    {
        var caller = CreateDefaultCaller(isOptedIn: false);
        var candidate = CreateDefaultCandidate();

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotOptedIn));
    }

    [Fact]
    public void Evaluate_CallerNotActive_ReturnsIneligibleWithCallerNotActive()
    {
        var caller = CreateDefaultCaller(isActive: false);
        var candidate = CreateDefaultCandidate();

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotActive));
    }

    [Fact]
    public void Evaluate_CandidateNotOptedIn_ReturnsIneligibleWithCandidateNotOptedIn()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isOptedIn: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotOptedIn));
    }

    [Fact]
    public void Evaluate_CandidateNotActive_ReturnsIneligibleWithCandidateNotActive()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isActive: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotActive));
    }

    #endregion

    #region 6. Visibility & Ghost Mode Invariants

    [Fact]
    public void Evaluate_CandidateHiddenFromDiscovery_ReturnsIneligibleWithCandidateHidden()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isDiscoverable: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateHidden));
    }

    #endregion

    #region 7. Bidirectional Block Invariants

    [Fact]
    public void Evaluate_CandidateBlockedByCaller_ReturnsIneligibleWithBlockedByCaller()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isBlockedByCaller: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller));
    }

    [Fact]
    public void Evaluate_CallerBlockedByCandidate_ReturnsIneligibleWithBlockedByCandidate()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate));
    }

    [Fact]
    public void Evaluate_MutualBlock_ReturnsIneligibleWithBothBlockReasons()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(isBlockedByCaller: true, isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller));
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate));
    }

    #endregion

    #region 8. Fail-Closed Missing Evidence Invariants

    [Fact]
    public void Evaluate_CallerMissingAuthoritativeEvidence_FailsClosedWithIndeterminate()
    {
        var caller = CreateDefaultCaller(hasEvidence: false);
        var candidate = CreateDefaultCandidate();

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Indeterminate, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence));
    }

    [Fact]
    public void Evaluate_CandidateMissingAuthoritativeEvidence_FailsClosedWithIndeterminate()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate(hasEvidence: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Indeterminate, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence));
    }

    #endregion

    #region 9. Batch Filtering Invariants

    [Fact]
    public void FilterEligible_BatchWithMixedCandidates_YieldsOnlyEligibleCandidates()
    {
        var caller = CreateDefaultCaller();
        var eligible1 = CreateDefaultCandidate(candidateId: CandidateA);
        var eligible2 = CreateDefaultCandidate(candidateId: CandidateB);
        var underage = CreateDefaultCandidate(candidateId: Guid.NewGuid(), age: 16);
        var blocked = CreateDefaultCandidate(candidateId: Guid.NewGuid(), isBlockedByCaller: true);
        var hidden = CreateDefaultCandidate(candidateId: Guid.NewGuid(), isDiscoverable: false);

        var inputBatch = new[] { eligible1, underage, eligible2, blocked, hidden };

        var filtered = SocialDiscoveryEligibilityPreFilter.FilterEligible(caller, inputBatch);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(eligible1, filtered);
        Assert.Contains(eligible2, filtered);
        Assert.DoesNotContain(underage, filtered);
        Assert.DoesNotContain(blocked, filtered);
        Assert.DoesNotContain(hidden, filtered);
    }

    #endregion

    #region 10. Privacy, Diagnostic, and Equality Safeguards

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveIdentitiesAndAttributes()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate();
        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.Equal("SocialDiscoveryCallerEvidence [private]", caller.ToString());
        Assert.Equal("SocialDiscoveryCandidateEvidence [private]", candidate.ToString());
        Assert.Equal("SocialDiscoveryEligibilityResult [private]", result.ToString());

        Assert.DoesNotContain(CallerId.ToString(), caller.ToString());
        Assert.DoesNotContain(CandidateA.ToString(), candidate.ToString());
    }

    [Fact]
    public void JsonSerialization_IgnoresPrivateEvidenceProperties()
    {
        var caller = CreateDefaultCaller();
        var candidate = CreateDefaultCandidate();

        var callerJson = JsonSerializer.Serialize(caller);
        var candidateJson = JsonSerializer.Serialize(candidate);

        Assert.Equal("{}", callerJson);
        Assert.Equal("{}", candidateJson);
        Assert.DoesNotContain(CallerId.ToString(), callerJson);
        Assert.DoesNotContain(CandidateA.ToString(), candidateJson);
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var caller1 = CreateDefaultCaller();
        var caller2 = CreateDefaultCaller();
        Assert.Equal(caller1, caller2);
        Assert.Equal(caller1.GetHashCode(), caller2.GetHashCode());

        var cand1 = CreateDefaultCandidate();
        var cand2 = CreateDefaultCandidate();
        Assert.Equal(cand1, cand2);
        Assert.Equal(cand1.GetHashCode(), cand2.GetHashCode());

        var res1 = SocialDiscoveryEligibilityResult.Success();
        var res2 = SocialDiscoveryEligibilityResult.Success();
        Assert.Equal(res1, res2);
        Assert.Equal(res1.GetHashCode(), res2.GetHashCode());
    }

    #endregion
}
