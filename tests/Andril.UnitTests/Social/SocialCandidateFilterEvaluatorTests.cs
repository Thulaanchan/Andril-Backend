using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialCandidateFilterEvaluatorTests
{
    #region A. Eligibility and Result Status

    [Fact]
    public void Evaluate_FullyCompatibleInputs_ReturnsEligible()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women, new[] { "English", "Tamil" }, new[] { "Dating" });
        var candidatePrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.Men, new[] { "Tamil", "French" }, new[] { "Dating" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 24,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 15);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.True(result.IsEligible);
        Assert.False(result.IsExcluded);
        Assert.False(result.IsIndeterminate);
        Assert.Empty(result.ExclusionReasons);
        Assert.Empty(result.IndeterminateReasons);
        Assert.Single(result.SharedLanguages);
        Assert.Equal("Tamil", result.SharedLanguages[0]);
        Assert.Single(result.SharedConnectionGoals);
        Assert.Equal("Dating", result.SharedConnectionGoals[0]);
    }

    [Fact]
    public void Evaluate_ExplicitHardFilterMismatch_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.All);

        // Candidate age 35 is outside caller's max age 30
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 35,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.False(result.IsEligible);
        Assert.True(result.IsExcluded);
        Assert.False(result.IsIndeterminate);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_MissingRequiredEvidence_ReturnsIndeterminate()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);

        // Distance is null (missing evidence)
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.False(result.IsEligible);
        Assert.False(result.IsExcluded);
        Assert.True(result.IsIndeterminate);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_Indeterminate_IsNeverEligible()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: 25,
            candidateGender: "female",
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: null);

        Assert.True(result.IsIndeterminate);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void Evaluate_Excluded_IsNeverEligible()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 17, // Underage
            callerGender: "male",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: 25,
            candidateGender: "female",
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: 10);

        Assert.True(result.IsExcluded);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void Evaluate_ConfirmedExclusionTakesPrecedenceOverIndeterminate()
    {
        var callerPrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.All);

        // Candidate age 40 (hard exclusion) AND distance is null (indeterminate factor)
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 40,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.True(result.IsExcluded);
        Assert.False(result.IsEligible);
        Assert.False(result.IsIndeterminate);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_EvaluatorGeneratedResults_ContainNoContradictoryStatusReasonCombinations()
    {
        // 1. Eligible has zero exclusions and zero indeterminates
        var eligible = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            10);
        Assert.Equal(SocialCandidateMatchStatus.Eligible, eligible.Status);
        Assert.Empty(eligible.ExclusionReasons);
        Assert.Empty(eligible.IndeterminateReasons);

        // 2. Excluded has at least one exclusion reason
        var excluded = SocialCandidateFilterEvaluator.Evaluate(
            17, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            10);
        Assert.Equal(SocialCandidateMatchStatus.Excluded, excluded.Status);
        Assert.NotEmpty(excluded.ExclusionReasons);

        // 3. Indeterminate has at least one indeterminate reason and zero exclusion reasons
        var indeterminate = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            null);
        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, indeterminate.Status);
        Assert.NotEmpty(indeterminate.IndeterminateReasons);
        Assert.Empty(indeterminate.ExclusionReasons);
    }

    #endregion

    #region B. Reciprocal Age Matching

    [Fact]
    public void Evaluate_BothUsersAtLeast18_MatchesAgeRequirement()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 18,
            callerGender: "male",
            callerPreferences: CreatePreferences(18, 30),
            candidateAge: 18,
            candidateGender: "female",
            candidatePreferences: CreatePreferences(18, 30),
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerUnderage, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CandidateUnderage, result.ExclusionReasons);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Evaluate_CallerUnder18_ReturnsExcluded_WithCallerUnderage(int callerAge)
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: callerAge,
            callerGender: "male",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: 25,
            candidateGender: "female",
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerUnderage, result.ExclusionReasons);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Evaluate_CandidateUnder18_ReturnsExcluded_WithCandidateUnderage(int candidateAge)
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: candidateAge,
            candidateGender: "female",
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CandidateUnderage, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_BothUnderage_ReturnsExcluded_WithBothUnderageReasons()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 17,
            callerGender: "male",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: 16,
            candidateGender: "female",
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerUnderage, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateUnderage, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CandidateOutsideCallerAgeRange_ReturnsExcluded_WithCallerAgePreferenceMismatch()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(18, 40);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 22,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 28, // Candidate is older than caller's max age (25)
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CandidateAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CallerOutsideCandidateAgeRange_ReturnsExcluded_WithCandidateAgePreferenceMismatch()
    {
        var callerPrefs = CreatePreferences(18, 40);
        var candidatePrefs = CreatePreferences(20, 25);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 30, // Caller is older than candidate's max age (25)
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 22,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CandidateAgePreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_BothAgeConstraintsCheckedIndependently_ReportsBothMismatches()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(35, 45);

        // Caller (30) is below candidate min (35). Candidate (30) is above caller max (25).
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 30,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 30,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Theory]
    [InlineData(20, 20)] // Candidate at caller min, caller at candidate min
    [InlineData(30, 30)] // Candidate at caller max, caller at candidate max
    [InlineData(20, 30)] // Inclusive min and max combinations
    [InlineData(30, 20)]
    public void Evaluate_AgeRangeInclusiveBoundaries_AreAccepted(int candidateAge, int callerAge)
    {
        var callerPrefs = CreatePreferences(20, 30);
        var candidatePrefs = CreatePreferences(20, 30);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: callerAge,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: candidateAge,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    #endregion

    #region C. Reciprocal Gender Matching

    [Fact]
    public void Evaluate_MenMatchesCanonicalMale()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Men);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Men);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "male",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_WomenMatchesCanonicalFemale()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "female",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Theory]
    [InlineData("male", "female")]
    [InlineData("female", "male")]
    [InlineData("male", "male")]
    [InlineData("female", "female")]
    public void Evaluate_AllAcceptsBothMaleAndFemale(string callerGender, string candidateGender)
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: callerGender,
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: candidateGender,
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CallerShowMeEnforced_MismatchReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Men);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);

        // Caller wants Men, but candidate is female
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "female",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CandidateShowMeEnforced_MismatchReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women);

        // Candidate wants Women, but caller is male
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_BothGenderPreferencesEnforcedIndependently_ReportsBothMismatches()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Men);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women);

        // Caller wants Men (candidate is female). Candidate wants Women (caller is male).
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch, result.ExclusionReasons);
    }

    [Theory]
    [InlineData("MALE", "FEMALE")]
    [InlineData("Male", "Female")]
    [InlineData(" male ", " female ")]
    [InlineData("mAlE", "fEmAlE")]
    public void Evaluate_GenderCasingAndWhitespaceNormalization_MatchesSuccessfully(string callerGender, string candidateGender)
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Women);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.Men);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: callerGender,
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: candidateGender,
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("prefer_not_to_say")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non_binary")]
    [InlineData("unspecified")]
    public void Evaluate_UnresolvedDemographicGender_ReturnsIndeterminate(string? unresolvedGender)
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);

        // Candidate gender is unresolved
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: unresolvedGender,
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.True(result.IsIndeterminate);
        Assert.False(result.IsEligible);
        Assert.Contains(SocialCandidateIndeterminateReason.CandidateGenderUnresolved, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_GenderUncertainty_IsNeverSilentlyTreatedAsMatch_EvenWithShowMeAll()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100, SocialGenderPreference.All);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "other", // Unresolved policy
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "prefer_not_to_say", // Unresolved policy
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.False(result.IsEligible);
        Assert.Contains(SocialCandidateIndeterminateReason.CallerGenderUnresolved, result.IndeterminateReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.CandidateGenderUnresolved, result.IndeterminateReasons);
    }

    #endregion

    #region D. Reciprocal Distance Matching

    [Fact]
    public void Evaluate_DistanceEvaluatedAgainstBothUsersPreferences()
    {
        var callerPrefs = CreatePreferences(18, 35, 10, 50);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 25);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Theory]
    [InlineData(10)] // Minimum inclusive boundary
    [InlineData(50)] // Maximum inclusive boundary
    [InlineData(25)] // Mid-range
    public void Evaluate_DistanceInclusiveBoundaries_AreAccepted(int distanceKm)
    {
        var callerPrefs = CreatePreferences(18, 35, 10, 50);
        var candidatePrefs = CreatePreferences(18, 35, 10, 50);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: distanceKm);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_DistanceZero_IsValidWhenBothRangesPermitIt()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 50);
        var candidatePrefs = CreatePreferences(18, 35, 0, 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 0);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CandidateOutsideCallerDistanceRange_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 40); // Max 40 km
        var candidatePrefs = CreatePreferences(18, 35, 0, 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 45);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CandidateDistancePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_CallerOutsideCandidateDistanceRange_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(18, 35, 0, 100);
        var candidatePrefs = CreatePreferences(18, 35, 0, 30); // Max 30 km

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 35);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CandidateDistancePreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_DistanceBelowMinimum_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(18, 35, 20, 100); // Min 20 km
        var candidatePrefs = CreatePreferences(18, 35, 0, 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 15); // Below minimum

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_NegativeDistance_ThrowsArgumentOutOfRangeException()
    {
        var callerPrefs = SocialExplorePreferences.Default;
        var candidatePrefs = SocialExplorePreferences.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialCandidateFilterEvaluator.Evaluate(
                25, "male", callerPrefs,
                26, "female", candidatePrefs,
                approximateDistanceKm: -1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialCandidateEvaluationInput(
                25, "male", callerPrefs,
                26, "female", candidatePrefs,
                approximateDistanceKm: -5));
    }

    [Fact]
    public void Evaluate_NullDistance_ProducesIndeterminate_WithDistanceUnavailable()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_MissingDistance_DoesNotBecomeZeroKm()
    {
        var callerPrefs = CreatePreferences(18, 35, 10, 100); // Min 10 km (0 km would be excluded!)
        var candidatePrefs = CreatePreferences(18, 35, 10, 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            approximateDistanceKm: null);

        // If null became 0 km, it would be Excluded. Because it is Indeterminate, it is NOT treated as 0 km.
        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_NonDefaultDistanceRanges_HandledCorrectly()
    {
        var callerPrefs = CreatePreferences(18, 35, 50, 500);
        var candidatePrefs = CreatePreferences(18, 35, 10, 250);

        // Distance 150 satisfies both ranges ([50, 500] and [10, 250])
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            approximateDistanceKm: 150);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
    }

    #endregion

    #region E. Shared Languages

    [Fact]
    public void Evaluate_SharedApprovedLanguages_AreIdentified()
    {
        var callerPrefs = CreatePreferences(languages: new[] { "English", "Tamil", "German" });
        var candidatePrefs = CreatePreferences(languages: new[] { "Tamil", "French", "German" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(2, result.SharedLanguages.Count);
        Assert.Contains("Tamil", result.SharedLanguages);
        Assert.Contains("German", result.SharedLanguages);
        Assert.DoesNotContain("English", result.SharedLanguages);
        Assert.DoesNotContain("French", result.SharedLanguages);
    }

    [Fact]
    public void Evaluate_CanonicalLanguageNames_ArePreserved()
    {
        var callerPrefs = CreatePreferences(languages: new[] { "Mandarin Chinese", "Spanish" });
        var candidatePrefs = CreatePreferences(languages: new[] { "Mandarin Chinese" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Single(result.SharedLanguages);
        Assert.Equal("Mandarin Chinese", result.SharedLanguages[0]);
    }

    [Fact]
    public void Evaluate_SharedLanguages_PreservesCallerOrder()
    {
        var callerPrefs = CreatePreferences(languages: new[] { "German", "Tamil", "English" });
        var candidatePrefs = CreatePreferences(languages: new[] { "English", "Tamil", "German" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(3, result.SharedLanguages.Count);
        Assert.Equal("German", result.SharedLanguages[0]);
        Assert.Equal("Tamil", result.SharedLanguages[1]);
        Assert.Equal("English", result.SharedLanguages[2]);
    }

    [Fact]
    public void Evaluate_NoLanguageOverlap_DoesNotExcludeOtherwiseEligibleCandidate()
    {
        var callerPrefs = CreatePreferences(languages: new[] { "English" });
        var candidatePrefs = CreatePreferences(languages: new[] { "Tamil" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.SharedLanguages);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_AllApprovedLanguages_HandledCorrectly()
    {
        var callerPrefs = CreatePreferences(languages: SocialLanguages.All);
        var candidatePrefs = CreatePreferences(languages: SocialLanguages.All);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(8, result.SharedLanguages.Count);
        Assert.Equal(SocialLanguages.All, result.SharedLanguages);
    }

    #endregion

    #region F. Shared Connection Goals

    [Fact]
    public void Evaluate_SharedApprovedGoals_AreIdentified()
    {
        var callerPrefs = CreatePreferences(connectionGoals: new[] { "Friendship", "Dating" });
        var candidatePrefs = CreatePreferences(connectionGoals: new[] { "Dating", "Life Partner" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Single(result.SharedConnectionGoals);
        Assert.Equal("Dating", result.SharedConnectionGoals[0]);
    }

    [Fact]
    public void Evaluate_CanonicalGoalNames_ArePreserved()
    {
        var callerPrefs = CreatePreferences(connectionGoals: new[] { "Meaningful Connection", "Open to Explore" });
        var candidatePrefs = CreatePreferences(connectionGoals: new[] { "Meaningful Connection" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Single(result.SharedConnectionGoals);
        Assert.Equal("Meaningful Connection", result.SharedConnectionGoals[0]);
    }

    [Fact]
    public void Evaluate_NoGoalOverlap_DoesNotExcludeOtherwiseEligibleCandidate()
    {
        var callerPrefs = CreatePreferences(connectionGoals: new[] { "Serious Relationship" });
        var candidatePrefs = CreatePreferences(connectionGoals: new[] { "Friendship" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.SharedConnectionGoals);
        Assert.Empty(result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_EmptyGoalLists_AreAccepted()
    {
        var callerPrefs = CreatePreferences(connectionGoals: Array.Empty<string>());
        var candidatePrefs = CreatePreferences(connectionGoals: Array.Empty<string>());

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.Empty(result.SharedConnectionGoals);
    }

    [Fact]
    public void Evaluate_MultipleSharedGoals_AreHandled()
    {
        var callerPrefs = CreatePreferences(connectionGoals: new[] { "Dating", "Friendship", "Companionship" });
        var candidatePrefs = CreatePreferences(connectionGoals: new[] { "Companionship", "Dating" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(2, result.SharedConnectionGoals.Count);
        Assert.Contains("Dating", result.SharedConnectionGoals);
        Assert.Contains("Companionship", result.SharedConnectionGoals);
    }

    #endregion

    #region G. Mixed Conditions and Precedence

    [Fact]
    public void Evaluate_ValidAgeAndGender_WithMissingDistance_ReturnsIndeterminate()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.Empty(result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_AgeMismatch_WithMissingDistance_ReturnsExcluded_WithExclusionPrecedence()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(20, 30);

        // Candidate age 30 is outside caller range [20, 25], and distance is null
        var result = SocialCandidateFilterEvaluator.Evaluate(
            22, "male", callerPrefs,
            30, "female", candidatePrefs,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_GenderMismatch_WithMissingDistance_ReturnsExcluded_WithExclusionPrecedence()
    {
        var callerPrefs = CreatePreferences(showMe: SocialGenderPreference.Men);
        var candidatePrefs = CreatePreferences(showMe: SocialGenderPreference.All);

        // Caller wants Men, candidate is female, and distance is null
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "female", callerPrefs,
            26, "female", candidatePrefs,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_AgeMismatch_WithUnresolvedGender_ReturnsExcluded_WithExclusionPrecedence()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(20, 30);

        // Candidate age 35 is mismatch, and candidate gender is "other" (unresolved)
        var result = SocialCandidateFilterEvaluator.Evaluate(
            22, "male", callerPrefs,
            35, "other", candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.CandidateGenderUnresolved, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_MultipleSimultaneousExclusions_AllReasonsPopulated()
    {
        var callerPrefs = CreatePreferences(20, 25, 0, 20, SocialGenderPreference.Men);
        var candidatePrefs = CreatePreferences(35, 45, 0, 20, SocialGenderPreference.Women);

        // Age mismatch both ways, gender mismatch both ways, distance mismatch (50 > 20)
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 30, // outside candidate [35, 45]
            callerGender: "male", // candidate wants Women
            callerPreferences: callerPrefs,
            candidateAge: 30, // outside caller [20, 25]
            candidateGender: "female", // caller wants Men
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 50);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateExclusionReason.CandidateDistancePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_MissingGenderAndMissingDistanceTogether_ReturnsIndeterminate_WithAllIndeterminateReasons()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "prefer_not_to_say",
            callerPreferences: SocialExplorePreferences.Default,
            candidateAge: 26,
            candidateGender: null,
            candidatePreferences: SocialExplorePreferences.Default,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.Empty(result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.CallerGenderUnresolved, result.IndeterminateReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.CandidateGenderUnresolved, result.IndeterminateReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluate_ReciprocalAgeMatches_ButGenderMismatches_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(showMe: SocialGenderPreference.Women);
        var candidatePrefs = CreatePreferences(showMe: SocialGenderPreference.Women);

        // Caller is male (candidate wants Women)
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluate_ReciprocalGenderMatches_ButDistanceMismatches_ReturnsExcluded()
    {
        var callerPrefs = CreatePreferences(minDistanceKm: 0, maxDistanceKm: 20);
        var candidatePrefs = CreatePreferences(minDistanceKm: 0, maxDistanceKm: 100);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", callerPrefs,
            26, "female", candidatePrefs,
            approximateDistanceKm: 30);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.Contains(SocialCandidateExclusionReason.CallerDistancePreferenceMismatch, result.ExclusionReasons);
        Assert.DoesNotContain(SocialCandidateExclusionReason.CallerGenderPreferenceMismatch, result.ExclusionReasons);
    }

    #endregion

    #region H. Input Validation

    [Fact]
    public void Evaluate_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateFilterEvaluator.Evaluate(null!));
    }

    [Fact]
    public void Evaluate_NullCallerPreferences_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateFilterEvaluator.Evaluate(
                25, "male", null!,
                26, "female", SocialExplorePreferences.Default,
                10));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialCandidateEvaluationInput(
                25, "male", null!,
                26, "female", SocialExplorePreferences.Default,
                10));
    }

    [Fact]
    public void Evaluate_NullCandidatePreferences_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateFilterEvaluator.Evaluate(
                25, "male", SocialExplorePreferences.Default,
                26, "female", null!,
                10));

        Assert.Throws<ArgumentNullException>(() =>
            new SocialCandidateEvaluationInput(
                25, "male", SocialExplorePreferences.Default,
                26, "female", null!,
                10));
    }

    [Fact]
    public void Evaluate_StructuredInputAndSeparateParameters_ReturnEquivalentResults()
    {
        var callerPrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.Women, new[] { "English", "Tamil" }, new[] { "Dating" });
        var candidatePrefs = CreatePreferences(22, 32, 0, 60, SocialGenderPreference.Men, new[] { "Tamil" }, new[] { "Dating" });

        var input = new SocialCandidateEvaluationInput(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 15);

        var resultFromInput = SocialCandidateFilterEvaluator.Evaluate(input);
        var resultFromParams = SocialCandidateFilterEvaluator.Evaluate(
            input.CallerAge,
            input.CallerGender,
            input.CallerPreferences,
            input.CandidateAge,
            input.CandidateGender,
            input.CandidatePreferences,
            input.ApproximateDistanceKm);

        Assert.Equal(resultFromInput, resultFromParams);
        Assert.Equal(resultFromInput.GetHashCode(), resultFromParams.GetHashCode());
    }

    #endregion

    #region I. Result Immutability, Equality and Formatting

    [Fact]
    public void ResultCollections_AreReadOnly_ModificationThrowsNotSupportedException()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            17, "other", SocialExplorePreferences.Default,
            16, "prefer_not_to_say", SocialExplorePreferences.Default,
            null);

        var exclusionList = (IList<SocialCandidateExclusionReason>)result.ExclusionReasons;
        Assert.Throws<NotSupportedException>(() => exclusionList.Add(SocialCandidateExclusionReason.CallerAgePreferenceMismatch));
        Assert.Throws<NotSupportedException>(() => exclusionList.Clear());

        var indeterminateList = (IList<SocialCandidateIndeterminateReason>)result.IndeterminateReasons;
        Assert.Throws<NotSupportedException>(() => indeterminateList.Add(SocialCandidateIndeterminateReason.DistanceUnavailable));
        Assert.Throws<NotSupportedException>(() => indeterminateList.Clear());

        var langList = (IList<string>)result.SharedLanguages;
        Assert.Throws<NotSupportedException>(() => langList.Add("German"));
        Assert.Throws<NotSupportedException>(() => langList.Clear());

        var goalList = (IList<string>)result.SharedConnectionGoals;
        Assert.Throws<NotSupportedException>(() => goalList.Add("Friendship"));
        Assert.Throws<NotSupportedException>(() => goalList.Clear());
    }

    [Fact]
    public void ResultCollections_CannotBeDowncastToMutableArray()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            10);

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (SocialCandidateExclusionReason[])result.ExclusionReasons;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (SocialCandidateIndeterminateReason[])result.IndeterminateReasons;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])result.SharedLanguages;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])result.SharedConnectionGoals;
        });
    }

    [Fact]
    public void ResultConstructor_DefensivelyCopiesInputCollections()
    {
        var mutableExclusions = new List<SocialCandidateExclusionReason> { SocialCandidateExclusionReason.CallerUnderage };
        var mutableIndeterminates = new List<SocialCandidateIndeterminateReason> { SocialCandidateIndeterminateReason.DistanceUnavailable };
        var mutableLanguages = new List<string> { "English" };
        var mutableGoals = new List<string> { "Dating" };

        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            mutableExclusions,
            mutableIndeterminates,
            mutableLanguages,
            mutableGoals);

        mutableExclusions.Add(SocialCandidateExclusionReason.CandidateUnderage);
        mutableIndeterminates.Add(SocialCandidateIndeterminateReason.CallerGenderUnresolved);
        mutableLanguages.Add("Tamil");
        mutableGoals.Add("Friendship");

        Assert.Single(result.ExclusionReasons);
        Assert.Single(result.IndeterminateReasons);
        Assert.Single(result.SharedLanguages);
        Assert.Single(result.SharedConnectionGoals);
    }

    [Fact]
    public void Result_EquivalentInstances_CompareEqualAndHaveSameHashCode()
    {
        var result1 = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerAgePreferenceMismatch },
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable },
            new[] { "English" },
            new[] { "Dating" });

        var result2 = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerAgePreferenceMismatch },
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable },
            new[] { "English" },
            new[] { "Dating" });

        Assert.True(result1.Equals(result2));
        Assert.True(result2.Equals(result1));
        Assert.True(result1 == result2);
        Assert.False(result1 != result2);
        Assert.Equal(result1.GetHashCode(), result2.GetHashCode());
    }

    [Fact]
    public void Result_DifferentStatusesOrReasons_CompareNotEqual()
    {
        var baseResult = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            Array.Empty<SocialCandidateExclusionReason>(),
            Array.Empty<SocialCandidateIndeterminateReason>(),
            new[] { "English" },
            new[] { "Dating" });

        var differentStatus = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage },
            Array.Empty<SocialCandidateIndeterminateReason>(),
            new[] { "English" },
            new[] { "Dating" });

        var differentLanguages = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            Array.Empty<SocialCandidateExclusionReason>(),
            Array.Empty<SocialCandidateIndeterminateReason>(),
            new[] { "Tamil" },
            new[] { "Dating" });

        Assert.False(baseResult.Equals(differentStatus));
        Assert.False(baseResult.Equals(differentLanguages));
        Assert.True(baseResult != differentStatus);
        Assert.False(baseResult == differentStatus);
        Assert.False(baseResult.Equals(null));
        Assert.False(baseResult.Equals("not a result"));
    }

    [Fact]
    public void Result_ToString_DoesNotExposeSensitiveData()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: CreatePreferences(languages: new[] { "English" }, connectionGoals: new[] { "Dating" }),
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: CreatePreferences(languages: new[] { "English" }, connectionGoals: new[] { "Dating" }),
            approximateDistanceKm: 15);

        var output = result.ToString();

        Assert.Contains("Status: Eligible", output);
        Assert.Contains("Languages: [English]", output);
        Assert.Contains("Goals: [Dating]", output);

        // Sensitive demographic data, coordinates, dates of birth must NOT be exposed
        Assert.DoesNotContain("male", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("female", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("15", output); // raw distance omitted from result ToString
        Assert.DoesNotContain("25", output); // raw caller age omitted from result ToString
        Assert.DoesNotContain("26", output); // raw candidate age omitted from result ToString
    }

    #endregion

    #region Reusable Test Helpers

    private static SocialExplorePreferences CreatePreferences(
        int minAge = 18,
        int maxAge = 35,
        int minDistanceKm = 0,
        int maxDistanceKm = 100,
        SocialGenderPreference showMe = SocialGenderPreference.All,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null)
    {
        var rangeFilters = new SocialExploreRangeFilters(minAge, maxAge, minDistanceKm, maxDistanceKm);
        return new SocialExplorePreferences(rangeFilters, showMe, languages, connectionGoals);
    }

    #endregion
}
