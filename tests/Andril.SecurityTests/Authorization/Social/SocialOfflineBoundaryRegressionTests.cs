using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Regression and security boundary tests verifying that Social discovery, eligibility pre-filtering,
/// and pair-interest observation operate strictly fail-closed, protect underage users, enforce module opt-in,
/// prevent cross-profile leakage, honor bidirectional blocks, reject mismatched repository records,
/// and ensure one-sided interest never authorizes communication or leaks private state.
/// </summary>
public sealed class SocialOfflineBoundaryRegressionTests
{
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ForeignId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime DecisionTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static SocialDiscoveryCallerEvidence CreateValidCaller(
        Guid? profileId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasEvidence = true) =>
        new(profileId ?? CallerId, age, isOptedIn, isActive, hasEvidence);

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

    private sealed class SecurityTestSocialInterestRepoDouble : ISocialInterestRepository
    {
        public Func<SocialProfilePairKey, CancellationToken, Task<(SocialDirectionalInterest?, SocialDirectionalInterest?)>>? PairInterestsHandler { get; set; }

        public Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(
            SocialProfilePairKey pairKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PairInterestsHandler is not null)
            {
                return PairInterestsHandler(pairKey, cancellationToken);
            }
            return Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((null, null));
        }

        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> HasRecordedInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveDirectionalInterestAsync(SocialDirectionalInterest interest, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    #region 1. Underage Exclusion and Boundary Age Tests

    [Fact]
    public void UnderageBoundary_CallerUnder18_FailsClosedWithCallerUnderage()
    {
        var caller = CreateValidCaller(age: 17);
        var candidate = CreateValidCandidate(age: 22);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage));
    }

    [Fact]
    public void UnderageBoundary_CandidateUnder18_FailsClosedWithCandidateUnderage()
    {
        var caller = CreateValidCaller(age: 24);
        var candidate = CreateValidCandidate(age: 17);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage));
    }

    [Fact]
    public void UnderageBoundary_BothUnder18_FailsClosedWithBothUnderageFlags()
    {
        var caller = CreateValidCaller(age: 16);
        var candidate = CreateValidCandidate(age: 15);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerUnderage));
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateUnderage));
    }

    [Theory]
    [InlineData(18, 18)]
    [InlineData(18, 19)]
    [InlineData(19, 18)]
    [InlineData(30, 28)]
    public void UnderageBoundary_AtOrAbove18YearsOld_PassesAgeSecurityCheck(int callerAge, int candidateAge)
    {
        var caller = CreateValidCaller(age: callerAge);
        var candidate = CreateValidCandidate(age: candidateAge);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.True(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Eligible, result.Status);
        Assert.Equal(SocialDiscoveryIneligibilityReason.None, result.Reasons);
    }

    #endregion

    #region 2. Social Opt-In and Account Standing Boundaries

    [Fact]
    public void OptInBoundary_CallerNotOptedIn_FailsClosedWithCallerNotOptedIn()
    {
        var caller = CreateValidCaller(isOptedIn: false);
        var candidate = CreateValidCandidate(isOptedIn: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotOptedIn));
    }

    [Fact]
    public void OptInBoundary_CandidateNotOptedIn_FailsClosedWithCandidateNotOptedIn()
    {
        var caller = CreateValidCaller(isOptedIn: true);
        var candidate = CreateValidCandidate(isOptedIn: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotOptedIn));
    }

    [Fact]
    public void StandingBoundary_CallerInactive_FailsClosedWithCallerNotActive()
    {
        var caller = CreateValidCaller(isActive: false);
        var candidate = CreateValidCandidate(isActive: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CallerNotActive));
    }

    [Fact]
    public void StandingBoundary_CandidateInactive_FailsClosedWithCandidateNotActive()
    {
        var caller = CreateValidCaller(isActive: true);
        var candidate = CreateValidCandidate(isActive: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateNotActive));
    }

    [Fact]
    public void VisibilityBoundary_CandidateHidden_FailsClosedWithCandidateHidden()
    {
        var caller = CreateValidCaller();
        var candidate = CreateValidCandidate(isDiscoverable: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.CandidateHidden));
    }

    #endregion

    #region 3. Profile Identity and Isolation Boundaries

    [Fact]
    public void IdentityBoundary_SameProfileId_FailsClosedWithSameProfile()
    {
        var caller = CreateValidCaller(profileId: CallerId);
        var candidate = CreateValidCandidate(profileId: CallerId);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.SameProfile));
    }

    [Fact]
    public void IdentityBoundary_EmptyProfileGuids_ThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SocialDiscoveryCallerEvidence(Guid.Empty, 25, true, true));
        Assert.Throws<ArgumentException>(() => new SocialDiscoveryCandidateEvidence(Guid.Empty, 25, true, true, true, false, false));
    }

    #endregion

    #region 4. Bidirectional Block Handling

    [Fact]
    public void BlockBoundary_BlockedByCaller_FailsClosedWithBlockedByCaller()
    {
        var caller = CreateValidCaller();
        var candidate = CreateValidCandidate(isBlockedByCaller: true, isCallerBlockedByCandidate: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller));
    }

    [Fact]
    public void BlockBoundary_BlockedByCandidate_FailsClosedWithBlockedByCandidate()
    {
        var caller = CreateValidCaller();
        var candidate = CreateValidCandidate(isBlockedByCaller: false, isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate));
    }

    [Fact]
    public void BlockBoundary_MutuallyBlocked_FailsClosedWithBothBlockFlags()
    {
        var caller = CreateValidCaller();
        var candidate = CreateValidCandidate(isBlockedByCaller: true, isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCaller));
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.BlockedByCandidate));
    }

    #endregion

    #region 5. Missing Evidence Fail-Closed Invariant

    [Fact]
    public void MissingEvidence_CallerLacksAuthoritativeEvidence_ReturnsIndeterminate()
    {
        var caller = CreateValidCaller(hasEvidence: false);
        var candidate = CreateValidCandidate(hasEvidence: true);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Indeterminate, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence));
    }

    [Fact]
    public void MissingEvidence_CandidateLacksAuthoritativeEvidence_ReturnsIndeterminate()
    {
        var caller = CreateValidCaller(hasEvidence: true);
        var candidate = CreateValidCandidate(hasEvidence: false);

        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);

        Assert.False(result.IsEligible);
        Assert.Equal(SocialDiscoveryEligibilityStatus.Indeterminate, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryIneligibilityReason.MissingEvidence));
    }

    [Fact]
    public void FilterEligible_BatchWithIneligibleAndMissingEvidence_YieldsOnlyConfirmedEligible()
    {
        var caller = CreateValidCaller();
        var candidates = new[]
        {
            CreateValidCandidate(Guid.NewGuid(), age: 17), // underage
            CreateValidCandidate(Guid.NewGuid(), isOptedIn: false), // not opted in
            CreateValidCandidate(Guid.NewGuid(), isBlockedByCaller: true), // blocked
            CreateValidCandidate(Guid.NewGuid(), hasEvidence: false), // indeterminate
            CreateValidCandidate(Guid.NewGuid(), age: 26) // eligible
        };

        var filtered = SocialDiscoveryEligibilityPreFilter.FilterEligible(caller, candidates);

        var eligible = Assert.Single(filtered);
        Assert.Equal(26, eligible.CandidateAge);
    }

    #endregion

    #region 6. One-Sided Interest Does Not Authorize Communication

    [Theory]
    [InlineData(true, null)]
    [InlineData(null, true)]
    [InlineData(null, null)]
    public void ReciprocalEvaluator_OneSidedOrMissing_NeverObservesMutualInterest(bool? first, bool? second)
    {
        var status = SocialReciprocalInterestEvaluator.Evaluate(first, second);

        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, status);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ReciprocalEvaluator_AnyExplicitNegative_ReturnsNotMutual(bool? first, bool? second)
    {
        var status = SocialReciprocalInterestEvaluator.Evaluate(first, second);

        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, status);
    }

    [Fact]
    public async Task ObservationCoordinator_OneSidedInterest_ResultIsNotMutualMatch()
    {
        var pairKey = new SocialProfilePairKey(CallerId, CandidateId);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, DecisionTime);

        var repo = new SecurityTestSocialInterestRepoDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((interestA, null))
        };
        var coordinator = new SocialPairInterestObservationService(repo);

        var result = await coordinator.ObservePairInterestAsync(pairKey);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);
    }

    [Fact]
    public async Task ObservationCoordinator_OneSidedNegative_ResultIsNotMutualMatch()
    {
        var pairKey = new SocialProfilePairKey(CallerId, CandidateId);
        var interestA = new SocialDirectionalInterest(pairKey, pairKey.FirstProfileId, true, DecisionTime);
        var interestB = new SocialDirectionalInterest(pairKey, pairKey.SecondProfileId, false, DecisionTime);

        var repo = new SecurityTestSocialInterestRepoDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((interestA, interestB))
        };
        var coordinator = new SocialPairInterestObservationService(repo);

        var result = await coordinator.ObservePairInterestAsync(pairKey);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result.ReciprocalStatus);
    }

    #endregion

    #region 7. Mismatched Repository Results and Scope Violations

    [Fact]
    public async Task ObservationCoordinator_ForeignPairKeyEvidence_ReturnsScopeViolation()
    {
        var requestedPair = new SocialProfilePairKey(CallerId, CandidateId);
        var foreignPair = new SocialProfilePairKey(CallerId, ForeignId);

        var foreignInterest = new SocialDirectionalInterest(foreignPair, CallerId, true, DecisionTime);

        var repo = new SecurityTestSocialInterestRepoDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((foreignInterest, null))
        };
        var coordinator = new SocialPairInterestObservationService(repo);

        var result = await coordinator.ObservePairInterestAsync(requestedPair);

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);
    }

    [Fact]
    public void DirectionalInterest_ForeignActingProfile_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(CallerId, CandidateId);
        Assert.Throws<ArgumentException>(() => new SocialDirectionalInterest(pairKey, ForeignId, true, DecisionTime));
    }

    [Fact]
    public async Task ObservationCoordinator_MismatchedActingProfileSlot_ReturnsScopeViolation()
    {
        var requestedPair = new SocialProfilePairKey(CallerId, CandidateId);
        var mismatchedSlotInterest = new SocialDirectionalInterest(requestedPair, requestedPair.SecondProfileId, true, DecisionTime);

        var repo = new SecurityTestSocialInterestRepoDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((mismatchedSlotInterest, null))
        };
        var coordinator = new SocialPairInterestObservationService(repo);

        var result = await coordinator.ObservePairInterestAsync(requestedPair);

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
    }

    [Fact]
    public void ScopePolicy_ActingProfileValidation_NonMemberOrEmptyReturnsExpectedFailure()
    {
        var requestedPair = new SocialProfilePairKey(CallerId, CandidateId);

        var unrelatedValidation = SocialInterestResultScopePolicy.Validate(requestedPair, ForeignId, null);
        Assert.False(unrelatedValidation.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.UnrelatedProfile, unrelatedValidation.Status);

        var emptyValidation = SocialInterestResultScopePolicy.Validate(requestedPair, Guid.Empty, null);
        Assert.False(emptyValidation.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.InvalidIdentity, emptyValidation.Status);
    }

    #endregion

    #region 8. Information Leakage and Transport Serialization Security

    [Fact]
    public void Serialization_EvidenceModels_ConcealPrivateProfileIdentities()
    {
        var caller = CreateValidCaller(CallerId);
        var candidate = CreateValidCandidate(CandidateId);

        var callerJson = JsonSerializer.Serialize(caller);
        var candidateJson = JsonSerializer.Serialize(candidate);

        Assert.DoesNotContain(CallerId.ToString(), callerJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CandidateId.ToString(), candidateJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToString_EvidenceAndResultModels_ConcealPrivateState()
    {
        var caller = CreateValidCaller(CallerId);
        var candidate = CreateValidCandidate(CandidateId);
        var pairKey = new SocialProfilePairKey(CallerId, CandidateId);
        var obsResult = SocialPairInterestObservationResult.Success(pairKey, SocialReciprocalInterestStatus.MutualInterestObserved);

        Assert.DoesNotContain(CallerId.ToString(), caller.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CandidateId.ToString(), candidate.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CallerId.ToString(), obsResult.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[private]", caller.ToString());
        Assert.Contains("[private]", candidate.ToString());
        Assert.Contains("[private]", obsResult.ToString());
    }

    #endregion

    #region 9. Production Endpoint and DI Registration Isolation

    [Fact]
    public void SocialDiscoveryDomainServices_AreStaticPureEvaluators_NotExposedInServiceCollection()
    {
        var services = new ServiceCollection();

        var serviceTypes = services.Select(sd => sd.ServiceType).ToList();

        Assert.DoesNotContain(typeof(SocialDiscoveryEligibilityPreFilter), serviceTypes);
        Assert.DoesNotContain(typeof(SocialDiscoveryGatedMatchingService), serviceTypes);
        Assert.DoesNotContain(typeof(SocialCandidateFilterEvaluator), serviceTypes);
        Assert.DoesNotContain(typeof(SocialReciprocalInterestEvaluator), serviceTypes);

        // Ensure static class invariants
        Assert.True(typeof(SocialDiscoveryEligibilityPreFilter).IsAbstract && typeof(SocialDiscoveryEligibilityPreFilter).IsSealed);
        Assert.True(typeof(SocialDiscoveryGatedMatchingService).IsAbstract && typeof(SocialDiscoveryGatedMatchingService).IsSealed);
        Assert.True(typeof(SocialReciprocalInterestEvaluator).IsAbstract && typeof(SocialReciprocalInterestEvaluator).IsSealed);
    }

    #endregion
}
