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
}
