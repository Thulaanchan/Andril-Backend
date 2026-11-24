using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialPairInterestObservationServiceTests
{
    private static readonly Guid ProfileIdA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileIdB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ForeignProfileId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime DecisionTime = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private static SocialProfilePairKey CanonicalPair =>
        new(ProfileIdA, ProfileIdB);

    private static SocialProfilePairKey ForeignPair =>
        new(ProfileIdA, ForeignProfileId);

    private sealed class TestSocialInterestRepositoryDouble : ISocialInterestRepository
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

    #region 1. Constructor and Argument Guards

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SocialPairInterestObservationService(null!));
    }

    [Fact]
    public async Task ObservePairInterestAsync_NullPairKey_ThrowsArgumentNullException()
    {
        var repo = new TestSocialInterestRepositoryDouble();
        var service = new SocialPairInterestObservationService(repo);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ObservePairInterestAsync(null!));
    }

    #endregion

    #region 2. Reciprocal State Observations

    [Fact]
    public async Task ObservePairInterestAsync_BothDirectionsPositive_ReturnsMutualInterestObserved()
    {
        var interestA = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.FirstProfileId, true, DecisionTime);
        var interestB = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.SecondProfileId, true, DecisionTime);

        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((interestA, interestB))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.True(result.IsValid);
        Assert.True(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, result.ReciprocalStatus);
    }

    [Fact]
    public async Task ObservePairInterestAsync_OneDirectionPositiveOneMissing_ReturnsIndeterminate()
    {
        var interestA = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.FirstProfileId, true, DecisionTime);

        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((interestA, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);
    }

    [Fact]
    public async Task ObservePairInterestAsync_BothDecisionsMissing_ReturnsIndeterminate()
    {
        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((null, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ObservePairInterestAsync_ExplicitNegativeInterest_ReturnsNotMutual(bool firstPositive, bool secondPositive)
    {
        var interestA = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.FirstProfileId, firstPositive, DecisionTime);
        var interestB = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.SecondProfileId, secondPositive, DecisionTime);

        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((interestA, interestB))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.True(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.Success, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result.ReciprocalStatus);
    }

    #endregion

    #region 3. Scope Boundary and Malformed Record Handling

    [Fact]
    public async Task ObservePairInterestAsync_ForeignPairRecord_FailsClosedWithScopeViolation()
    {
        var foreignInterest = new SocialDirectionalInterest(ForeignPair, CanonicalPair.FirstProfileId, true, DecisionTime);

        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((foreignInterest, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result.ReciprocalStatus);
    }

    [Fact]
    public async Task ObservePairInterestAsync_WrongActorForSlot_FailsClosedWithScopeViolation()
    {
        // Second profile ID returned in the first profile's slot
        var wrongActorInterest = new SocialDirectionalInterest(CanonicalPair, CanonicalPair.SecondProfileId, true, DecisionTime);

        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => Task.FromResult<(SocialDirectionalInterest?, SocialDirectionalInterest?)>((wrongActorInterest, null))
        };
        var service = new SocialPairInterestObservationService(repo);

        var result = await service.ObservePairInterestAsync(CanonicalPair);

        Assert.False(result.IsValid);
        Assert.False(result.IsMutualMatch);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
    }

    #endregion

    #region 4. Cancellation and Dependency Failures

    [Fact]
    public async Task ObservePairInterestAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var repo = new TestSocialInterestRepositoryDouble();
        var service = new SocialPairInterestObservationService(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ObservePairInterestAsync(CanonicalPair, cts.Token));
    }

    [Fact]
    public async Task ObservePairInterestAsync_RepositoryFailure_PropagatesException()
    {
        var repo = new TestSocialInterestRepositoryDouble
        {
            PairInterestsHandler = (_, _) => throw new InvalidOperationException("Database read failure")
        };
        var service = new SocialPairInterestObservationService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ObservePairInterestAsync(CanonicalPair));
    }

    #endregion

    #region 5. Privacy Safeguards and Equality

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveData()
    {
        var result = SocialPairInterestObservationResult.Success(CanonicalPair, SocialReciprocalInterestStatus.MutualInterestObserved);
        Assert.Equal("SocialPairInterestObservationResult [private]", result.ToString());
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var res1 = SocialPairInterestObservationResult.Success(CanonicalPair, SocialReciprocalInterestStatus.MutualInterestObserved);
        var res2 = SocialPairInterestObservationResult.Success(CanonicalPair, SocialReciprocalInterestStatus.MutualInterestObserved);
        var violation = SocialPairInterestObservationResult.ScopeViolation(CanonicalPair);

        Assert.Equal(res1, res2);
        Assert.Equal(res1.GetHashCode(), res2.GetHashCode());
        Assert.NotEqual(res1, violation);
    }

    #endregion
}
