using System;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Unit and truth-table verification tests for <see cref="SocialReciprocalInterestEvaluator"/>
/// and <see cref="SocialReciprocalInterestStatus"/>.
/// </summary>
public sealed class SocialReciprocalInterestEvaluatorTests
{
    #region SECTION A — Complete 9-State Truth Table

    [Theory]
    [InlineData(true, true, SocialReciprocalInterestStatus.MutualInterestObserved)]
    [InlineData(true, false, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(true, null, SocialReciprocalInterestStatus.Indeterminate)]
    [InlineData(false, true, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(false, false, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(false, null, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(null, true, SocialReciprocalInterestStatus.Indeterminate)]
    [InlineData(null, false, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(null, null, SocialReciprocalInterestStatus.Indeterminate)]
    public void Evaluate_AllNineInputCombinations_MatchesTruthTable(
        bool? firstInterestedInSecond,
        bool? secondInterestedInFirst,
        SocialReciprocalInterestStatus expectedStatus)
    {
        var actual = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond, secondInterestedInFirst);

        Assert.Equal(expectedStatus, actual);
    }

    #endregion

    #region SECTION B — Symmetry Invariants

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(false, null)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData(null, null)]
    public void Evaluate_IsSymmetric_WhenReversingInputs(
        bool? firstInterestedInSecond,
        bool? secondInterestedInFirst)
    {
        var forward = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond, secondInterestedInFirst);
        var reverse = SocialReciprocalInterestEvaluator.Evaluate(secondInterestedInFirst, firstInterestedInSecond);

        Assert.Equal(forward, reverse);
    }

    #endregion

    #region SECTION C — One-Sided Positive Interest Safeguards

    [Fact]
    public void Evaluate_OneSidedPositiveWithUnknownOther_NeverEstablishesMutualInterest()
    {
        // First likes Second, but Second's response is unrecorded
        var result1 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: true, secondInterestedInFirst: null);
        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, result1);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result1);

        // Second likes First, but First's response is unrecorded
        var result2 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: null, secondInterestedInFirst: true);
        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, result2);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result2);
    }

    [Fact]
    public void Evaluate_OneSidedPositiveWithExplicitPass_NeverEstablishesMutualInterest()
    {
        // First likes Second, but Second explicitly passed
        var result1 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: true, secondInterestedInFirst: false);
        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, result1);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result1);

        // First passed, but Second liked First
        var result2 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: false, secondInterestedInFirst: true);
        Assert.NotEqual(SocialReciprocalInterestStatus.MutualInterestObserved, result2);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result2);
    }

    #endregion

    #region SECTION D — Explicit Negative Evidence Precedence

    [Fact]
    public void Evaluate_ExplicitNegativeEvidence_TakesPrecedenceOverUnknownEvidence()
    {
        // First explicitly passed; Second unrecorded
        var result1 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: false, secondInterestedInFirst: null);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result1);

        // First unrecorded; Second explicitly passed
        var result2 = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: null, secondInterestedInFirst: false);
        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result2);
    }

    [Fact]
    public void Evaluate_BothExplicitNegative_ReturnsNotMutual()
    {
        var result = SocialReciprocalInterestEvaluator.Evaluate(firstInterestedInSecond: false, secondInterestedInFirst: false);

        Assert.Equal(SocialReciprocalInterestStatus.NotMutual, result);
    }

    #endregion

    #region SECTION E — Determinism and Pure Execution

    [Fact]
    public void Evaluate_RepeatedInvocations_AreDeterministic()
    {
        var inputs = new (bool? first, bool? second)[]
        {
            (true, true),
            (true, false),
            (true, null),
            (false, true),
            (false, false),
            (false, null),
            (null, true),
            (null, false),
            (null, null)
        };

        foreach (var (first, second) in inputs)
        {
            var run1 = SocialReciprocalInterestEvaluator.Evaluate(first, second);
            var run2 = SocialReciprocalInterestEvaluator.Evaluate(first, second);
            var run3 = SocialReciprocalInterestEvaluator.Evaluate(first, second);

            Assert.Equal(run1, run2);
            Assert.Equal(run2, run3);
        }
    }

    [Fact]
    public void EnumValues_AllThreeStatuses_AreDistinct()
    {
        var values = (SocialReciprocalInterestStatus[])Enum.GetValues(typeof(SocialReciprocalInterestStatus));

        Assert.Equal(3, values.Length);
        Assert.Contains(SocialReciprocalInterestStatus.MutualInterestObserved, values);
        Assert.Contains(SocialReciprocalInterestStatus.NotMutual, values);
        Assert.Contains(SocialReciprocalInterestStatus.Indeterminate, values);

        Assert.NotEqual((int)SocialReciprocalInterestStatus.MutualInterestObserved, (int)SocialReciprocalInterestStatus.NotMutual);
        Assert.NotEqual((int)SocialReciprocalInterestStatus.MutualInterestObserved, (int)SocialReciprocalInterestStatus.Indeterminate);
        Assert.NotEqual((int)SocialReciprocalInterestStatus.NotMutual, (int)SocialReciprocalInterestStatus.Indeterminate);
    }

    #endregion

    #region SECTION F — Typed Directional Interest Evaluation and Observation

    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileC = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime ValidUtc = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_BothPositiveDirectionalInterests_ReturnsMutualInterestObserved()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interestA = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);
        var interestB = new SocialDirectionalInterest(pairKey, ProfileB, true, ValidUtc);

        var result = SocialReciprocalInterestEvaluator.Evaluate(interestA, interestB);

        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, result);
    }

    [Theory]
    [InlineData(true, false, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(false, true, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(false, false, SocialReciprocalInterestStatus.NotMutual)]
    public void Evaluate_WithNegativeInterest_ReturnsNotMutual(bool aIsPositive, bool bIsPositive, SocialReciprocalInterestStatus expected)
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interestA = new SocialDirectionalInterest(pairKey, ProfileA, aIsPositive, ValidUtc);
        var interestB = new SocialDirectionalInterest(pairKey, ProfileB, bIsPositive, ValidUtc);

        var result = SocialReciprocalInterestEvaluator.Evaluate(interestA, interestB);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Evaluate_OneSidedDirectionalInterest_ReturnsIndeterminate()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interestA = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);

        var result1 = SocialReciprocalInterestEvaluator.Evaluate(interestA, null);
        var result2 = SocialReciprocalInterestEvaluator.Evaluate(null, interestA);

        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result1);
        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result2);
    }

    [Fact]
    public void Evaluate_BothNullInterests_ReturnsIndeterminate()
    {
        var result = SocialReciprocalInterestEvaluator.Evaluate((SocialDirectionalInterest?)null, null);

        Assert.Equal(SocialReciprocalInterestStatus.Indeterminate, result);
    }

    [Fact]
    public void Evaluate_MismatchedPairKeys_ThrowsArgumentException()
    {
        var pairKey1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKey2 = new SocialProfilePairKey(ProfileA, ProfileC);

        var interestA = new SocialDirectionalInterest(pairKey1, ProfileA, true, ValidUtc);
        var interestB = new SocialDirectionalInterest(pairKey2, ProfileC, true, ValidUtc);

        var ex = Assert.Throws<ArgumentException>(
            () => SocialReciprocalInterestEvaluator.Evaluate(interestA, interestB));
        Assert.Equal("interestFromSecond", ex.ParamName);
    }

    [Fact]
    public void Evaluate_SameActingProfile_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest1 = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);
        var interest2 = new SocialDirectionalInterest(pairKey, ProfileA, false, ValidUtc);

        var ex = Assert.Throws<ArgumentException>(
            () => SocialReciprocalInterestEvaluator.Evaluate(interest1, interest2));
        Assert.Equal("interestFromSecond", ex.ParamName);
    }

    [Fact]
    public void Observe_ValidInputs_MatchesEvaluatorResult()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interestA = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);
        var interestB = new SocialDirectionalInterest(pairKey, ProfileB, true, ValidUtc);

        var result = SocialReciprocalInterestEvaluator.Observe(pairKey, interestA, interestB);

        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, result);
    }

    [Fact]
    public void Observe_NullPairKey_ThrowsArgumentNullException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtc);

        var ex = Assert.Throws<ArgumentNullException>(
            () => SocialReciprocalInterestEvaluator.Observe(null!, interest, null));
        Assert.Equal("pairKey", ex.ParamName);
    }

    [Fact]
    public void Observe_MismatchedInterestPairKey_ThrowsArgumentException()
    {
        var pairKey1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKey2 = new SocialProfilePairKey(ProfileA, ProfileC);
        var interest = new SocialDirectionalInterest(pairKey2, ProfileA, true, ValidUtc);

        var ex = Assert.Throws<ArgumentException>(
            () => SocialReciprocalInterestEvaluator.Observe(pairKey1, interest, null));
        Assert.Equal("interestFromFirst", ex.ParamName);
    }

    #endregion
}
