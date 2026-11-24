using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialCandidateEvaluationResultInvariantTests
{
    #region A. Eligible Result Invariants

    [Fact]
    public void Constructor_Eligible_WithZeroReasons_IsAccepted()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            exclusionReasons: Array.Empty<SocialCandidateExclusionReason>(),
            indeterminateReasons: Array.Empty<SocialCandidateIndeterminateReason>());

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.True(result.IsEligible);
        Assert.False(result.IsExcluded);
        Assert.False(result.IsIndeterminate);
        Assert.Empty(result.ExclusionReasons);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Fact]
    public void Constructor_Eligible_WithNullReasons_IsAccepted()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            exclusionReasons: null,
            indeterminateReasons: null);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.True(result.IsEligible);
        Assert.Empty(result.ExclusionReasons);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Fact]
    public void Constructor_Eligible_WithNonEmptyExclusionReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Eligible,
                exclusionReasons: new[] { SocialCandidateExclusionReason.CallerUnderage },
                indeterminateReasons: null));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("cannot contain exclusion reasons", ex.Message);
    }

    [Fact]
    public void Constructor_Eligible_WithNonEmptyIndeterminateReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Eligible,
                exclusionReasons: null,
                indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable }));

        Assert.Equal("indeterminateReasons", ex.ParamName);
        Assert.Contains("cannot contain indeterminate reasons", ex.Message);
    }

    [Fact]
    public void Constructor_Eligible_WithBothReasonCollectionsNonEmpty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Eligible,
                exclusionReasons: new[] { SocialCandidateExclusionReason.CandidateAgePreferenceMismatch },
                indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable }));

        Assert.Equal("exclusionReasons", ex.ParamName);
    }

    [Fact]
    public void Constructor_Eligible_WithValidSharedLanguagesAndGoals_IsAccepted()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            exclusionReasons: null,
            indeterminateReasons: null,
            sharedLanguages: new[] { "English", "Tamil" },
            sharedConnectionGoals: new[] { "Dating" });

        Assert.True(result.IsEligible);
        Assert.Equal(2, result.SharedLanguages.Count);
        Assert.Equal("English", result.SharedLanguages[0]);
        Assert.Equal("Tamil", result.SharedLanguages[1]);
        Assert.Single(result.SharedConnectionGoals);
        Assert.Equal("Dating", result.SharedConnectionGoals[0]);
    }

    [Fact]
    public void IsEligible_IsTrueOnlyForEligibleStatus()
    {
        var eligible = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible);

        var excluded = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage });

        var indeterminate = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Indeterminate,
            null,
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable });

        Assert.True(eligible.IsEligible);
        Assert.False(excluded.IsEligible);
        Assert.False(indeterminate.IsEligible);
    }

    #endregion

    #region B. Excluded Result Invariants

    [Fact]
    public void Constructor_Excluded_WithSingleExclusionReason_IsAccepted()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            exclusionReasons: new[] { SocialCandidateExclusionReason.CallerAgePreferenceMismatch });

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.False(result.IsEligible);
        Assert.True(result.IsExcluded);
        Assert.False(result.IsIndeterminate);
        Assert.Single(result.ExclusionReasons);
        Assert.Equal(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons[0]);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Fact]
    public void Constructor_Excluded_WithMultipleExclusionReasons_IsAccepted()
    {
        var reasons = new[]
        {
            SocialCandidateExclusionReason.CallerAgePreferenceMismatch,
            SocialCandidateExclusionReason.CandidateGenderPreferenceMismatch,
            SocialCandidateExclusionReason.CallerDistancePreferenceMismatch
        };

        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            exclusionReasons: reasons);

        Assert.True(result.IsExcluded);
        Assert.Equal(3, result.ExclusionReasons.Count);
        Assert.Equal(reasons, result.ExclusionReasons);
    }

    [Fact]
    public void Constructor_Excluded_WithBothExclusionAndIndeterminateReasons_IsAccepted()
    {
        var exclusions = new[] { SocialCandidateExclusionReason.CallerAgePreferenceMismatch };
        var indeterminates = new[] { SocialCandidateIndeterminateReason.DistanceUnavailable };

        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            exclusionReasons: exclusions,
            indeterminateReasons: indeterminates);

        Assert.True(result.IsExcluded);
        Assert.False(result.IsEligible);
        Assert.False(result.IsIndeterminate);
        Assert.Single(result.ExclusionReasons);
        Assert.Single(result.IndeterminateReasons);
    }

    [Fact]
    public void Constructor_Excluded_WithZeroExclusionReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Excluded,
                exclusionReasons: Array.Empty<SocialCandidateExclusionReason>()));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("must contain at least one exclusion reason", ex.Message);
    }

    [Fact]
    public void Constructor_Excluded_WithNullExclusionReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Excluded,
                exclusionReasons: null));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("must contain at least one exclusion reason", ex.Message);
    }

    [Fact]
    public void Constructor_Excluded_WithOnlyIndeterminateReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Excluded,
                exclusionReasons: Array.Empty<SocialCandidateExclusionReason>(),
                indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable }));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("must contain at least one exclusion reason", ex.Message);
    }

    [Fact]
    public void IsExcluded_IsTrueForValidExcludedResults_AndIsEligibleRemainsFalse()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage },
            new[] { SocialCandidateIndeterminateReason.CallerGenderUnresolved });

        Assert.True(result.IsExcluded);
        Assert.False(result.IsEligible);
        Assert.False(result.IsIndeterminate);
    }

    #endregion

    #region C. Indeterminate Result Invariants

    [Fact]
    public void Constructor_Indeterminate_WithSingleIndeterminateReason_IsAccepted()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Indeterminate,
            exclusionReasons: null,
            indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable });

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.False(result.IsEligible);
        Assert.False(result.IsExcluded);
        Assert.True(result.IsIndeterminate);
        Assert.Empty(result.ExclusionReasons);
        Assert.Single(result.IndeterminateReasons);
        Assert.Equal(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons[0]);
    }

    [Fact]
    public void Constructor_Indeterminate_WithMultipleIndeterminateReasons_IsAccepted()
    {
        var reasons = new[]
        {
            SocialCandidateIndeterminateReason.CallerGenderUnresolved,
            SocialCandidateIndeterminateReason.CandidateGenderUnresolved,
            SocialCandidateIndeterminateReason.DistanceUnavailable
        };

        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Indeterminate,
            exclusionReasons: Array.Empty<SocialCandidateExclusionReason>(),
            indeterminateReasons: reasons);

        Assert.True(result.IsIndeterminate);
        Assert.Empty(result.ExclusionReasons);
        Assert.Equal(3, result.IndeterminateReasons.Count);
        Assert.Equal(reasons, result.IndeterminateReasons);
    }

    [Fact]
    public void Constructor_Indeterminate_WithZeroIndeterminateReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Indeterminate,
                exclusionReasons: null,
                indeterminateReasons: Array.Empty<SocialCandidateIndeterminateReason>()));

        Assert.Equal("indeterminateReasons", ex.ParamName);
        Assert.Contains("must contain at least one indeterminate reason", ex.Message);
    }

    [Fact]
    public void Constructor_Indeterminate_WithNullIndeterminateReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Indeterminate,
                exclusionReasons: null,
                indeterminateReasons: null));

        Assert.Equal("indeterminateReasons", ex.ParamName);
        Assert.Contains("must contain at least one indeterminate reason", ex.Message);
    }

    [Fact]
    public void Constructor_Indeterminate_WithExclusionReasons_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Indeterminate,
                exclusionReasons: new[] { SocialCandidateExclusionReason.CallerAgePreferenceMismatch },
                indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable }));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("cannot contain exclusion reasons", ex.Message);
    }

    [Fact]
    public void IsIndeterminate_IsTrueOnlyForIndeterminateStatus()
    {
        var eligible = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible);

        var excluded = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage });

        var indeterminate = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Indeterminate,
            null,
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable });

        Assert.True(indeterminate.IsIndeterminate);
        Assert.False(indeterminate.IsEligible);
        Assert.False(indeterminate.IsExcluded);

        Assert.False(eligible.IsIndeterminate);
        Assert.False(excluded.IsIndeterminate);
    }

    #endregion

    #region D. Undefined Enum Values

    [Fact]
    public void Constructor_UndefinedStatus_ThrowsArgumentOutOfRangeException()
    {
        var invalidStatus = (SocialCandidateMatchStatus)999;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialCandidateEvaluationResult(invalidStatus));

        Assert.Equal("status", ex.ParamName);
        Assert.Equal(invalidStatus, ex.ActualValue);
    }

    [Fact]
    public void Constructor_UndefinedExclusionReason_ThrowsArgumentException()
    {
        var invalidReason = (SocialCandidateExclusionReason)888;

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Excluded,
                exclusionReasons: new[] { invalidReason }));

        Assert.Equal("exclusionReasons", ex.ParamName);
        Assert.Contains("Invalid exclusion reason value", ex.Message);
    }

    [Fact]
    public void Constructor_UndefinedIndeterminateReason_ThrowsArgumentException()
    {
        var invalidReason = (SocialCandidateIndeterminateReason)777;

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Indeterminate,
                exclusionReasons: null,
                indeterminateReasons: new[] { invalidReason }));

        Assert.Equal("indeterminateReasons", ex.ParamName);
        Assert.Contains("Invalid indeterminate reason value", ex.Message);
    }

    [Fact]
    public void Constructor_UndefinedExclusionReasonAlongsideValidReason_ThrowsArgumentException()
    {
        var invalidReason = (SocialCandidateExclusionReason)999;

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Excluded,
                exclusionReasons: new[] { SocialCandidateExclusionReason.CallerUnderage, invalidReason }));

        Assert.Equal("exclusionReasons", ex.ParamName);
    }

    [Fact]
    public void Constructor_UndefinedIndeterminateReasonAlongsideValidReason_ThrowsArgumentException()
    {
        var invalidReason = (SocialCandidateIndeterminateReason)999;

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateEvaluationResult(
                SocialCandidateMatchStatus.Indeterminate,
                exclusionReasons: null,
                indeterminateReasons: new[] { SocialCandidateIndeterminateReason.DistanceUnavailable, invalidReason }));

        Assert.Equal("indeterminateReasons", ex.ParamName);
    }

    #endregion

    #region E. Defensive Immutability

    [Fact]
    public void Constructor_DefensivelyCopiesSuppliedCollections()
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
        Assert.Equal(SocialCandidateExclusionReason.CallerUnderage, result.ExclusionReasons[0]);

        Assert.Single(result.IndeterminateReasons);
        Assert.Equal(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons[0]);

        Assert.Single(result.SharedLanguages);
        Assert.Equal("English", result.SharedLanguages[0]);

        Assert.Single(result.SharedConnectionGoals);
        Assert.Equal("Dating", result.SharedConnectionGoals[0]);
    }

    [Fact]
    public void ResultCollections_CannotBeModifiedThroughIList()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage },
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable },
            new[] { "English" },
            new[] { "Dating" });

        var exclusions = (IList<SocialCandidateExclusionReason>)result.ExclusionReasons;
        Assert.True(exclusions.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exclusions.Add(SocialCandidateExclusionReason.CandidateUnderage));
        Assert.Throws<NotSupportedException>(() => exclusions.Clear());

        var indeterminates = (IList<SocialCandidateIndeterminateReason>)result.IndeterminateReasons;
        Assert.True(indeterminates.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => indeterminates.Add(SocialCandidateIndeterminateReason.CallerGenderUnresolved));
        Assert.Throws<NotSupportedException>(() => indeterminates.Clear());

        var languages = (IList<string>)result.SharedLanguages;
        Assert.True(languages.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => languages.Add("Tamil"));
        Assert.Throws<NotSupportedException>(() => languages.Clear());

        var goals = (IList<string>)result.SharedConnectionGoals;
        Assert.True(goals.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => goals.Add("Friendship"));
        Assert.Throws<NotSupportedException>(() => goals.Clear());
    }

    [Fact]
    public void ResultCollections_CannotBeCastToMutableArray()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            null,
            null,
            new[] { "English" },
            new[] { "Dating" });

        Assert.Throws<InvalidCastException>(() => { _ = (SocialCandidateExclusionReason[])result.ExclusionReasons; });
        Assert.Throws<InvalidCastException>(() => { _ = (SocialCandidateIndeterminateReason[])result.IndeterminateReasons; });
        Assert.Throws<InvalidCastException>(() => { _ = (string[])result.SharedLanguages; });
        Assert.Throws<InvalidCastException>(() => { _ = (string[])result.SharedConnectionGoals; });
    }

    [Fact]
    public void PublicProperties_ExposeNoWritableSetters()
    {
        var type = typeof(SocialCandidateEvaluationResult);
        var propertyNames = new[]
        {
            nameof(SocialCandidateEvaluationResult.Status),
            nameof(SocialCandidateEvaluationResult.IsEligible),
            nameof(SocialCandidateEvaluationResult.IsExcluded),
            nameof(SocialCandidateEvaluationResult.IsIndeterminate),
            nameof(SocialCandidateEvaluationResult.ExclusionReasons),
            nameof(SocialCandidateEvaluationResult.IndeterminateReasons),
            nameof(SocialCandidateEvaluationResult.SharedLanguages),
            nameof(SocialCandidateEvaluationResult.SharedConnectionGoals)
        };

        foreach (var propertyName in propertyNames)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop);
            Assert.False(prop.CanWrite, $"Property {propertyName} must not have a public setter.");
        }
    }

    #endregion

    #region F. Equality and Value Consistency

    [Fact]
    public void Result_IdenticalValidInstances_CompareEqualAndProduceSameHashCode()
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
    public void Result_DifferentStatusesOrReasons_ProduceUnequalResults()
    {
        var baseResult = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            null,
            null,
            new[] { "English" },
            new[] { "Dating" });

        var differentStatus = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerUnderage },
            null,
            new[] { "English" },
            new[] { "Dating" });

        var differentLanguages = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            null,
            null,
            new[] { "Tamil" },
            new[] { "Dating" });

        var differentGoals = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            null,
            null,
            new[] { "English" },
            new[] { "Friendship" });

        Assert.False(baseResult.Equals(differentStatus));
        Assert.False(baseResult.Equals(differentLanguages));
        Assert.False(baseResult.Equals(differentGoals));
        Assert.True(baseResult != differentStatus);
        Assert.False(baseResult == differentStatus);
        Assert.False(baseResult.Equals(null));
        Assert.False(baseResult.Equals(new object()));
    }

    [Fact]
    public void Result_ToString_DoesNotExposePrivateDemographicData()
    {
        var result = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CallerGenderPreferenceMismatch },
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable },
            new[] { "English", "Tamil" },
            new[] { "Dating" });

        var output = result.ToString();

        Assert.Contains("Status: Excluded", output);
        Assert.Contains("CallerGenderPreferenceMismatch", output);
        Assert.Contains("DistanceUnavailable", output);
        Assert.Contains("Languages: [English, Tamil]", output);
        Assert.Contains("Goals: [Dating]", output);

        // Verify private data is absent
        Assert.DoesNotContain("male", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("female", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("birth", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coordinate", output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region G. Existing Evaluator Compatibility

    [Fact]
    public void Evaluator_FullyCompatibleCandidate_ProducesValidEligibleResult()
    {
        var callerPrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.Women, new[] { "English", "Tamil" }, new[] { "Dating" });
        var candidatePrefs = CreatePreferences(20, 30, 0, 50, SocialGenderPreference.Men, new[] { "Tamil" }, new[] { "Dating" });

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 25,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 26,
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 20);

        Assert.Equal(SocialCandidateMatchStatus.Eligible, result.Status);
        Assert.True(result.IsEligible);
        Assert.Empty(result.ExclusionReasons);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluator_AgeMismatch_ProducesValidExcludedResult()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(20, 30);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 22,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 30, // outside caller max (25)
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: 10);

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.True(result.IsExcluded);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
    }

    [Fact]
    public void Evaluator_MissingDistance_ProducesValidIndeterminateResult()
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            25, "male", SocialExplorePreferences.Default,
            26, "female", SocialExplorePreferences.Default,
            approximateDistanceKm: null);

        Assert.Equal(SocialCandidateMatchStatus.Indeterminate, result.Status);
        Assert.True(result.IsIndeterminate);
        Assert.Empty(result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Fact]
    public void Evaluator_AgeMismatchCombinedWithMissingDistance_ProducesValidExcludedResultWithBothReasons()
    {
        var callerPrefs = CreatePreferences(20, 25);
        var candidatePrefs = CreatePreferences(20, 30);

        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge: 22,
            callerGender: "male",
            callerPreferences: callerPrefs,
            candidateAge: 35, // hard exclusion
            candidateGender: "female",
            candidatePreferences: candidatePrefs,
            approximateDistanceKm: null); // indeterminate factor

        Assert.Equal(SocialCandidateMatchStatus.Excluded, result.Status);
        Assert.True(result.IsExcluded);
        Assert.Contains(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReasons);
        Assert.Contains(SocialCandidateIndeterminateReason.DistanceUnavailable, result.IndeterminateReasons);
    }

    [Theory]
    [InlineData(25, "male", 25, "female", 10, SocialCandidateMatchStatus.Eligible)]
    [InlineData(17, "male", 25, "female", 10, SocialCandidateMatchStatus.Excluded)]
    [InlineData(25, "male", 17, "female", 10, SocialCandidateMatchStatus.Excluded)]
    [InlineData(25, "male", 25, "female", null, SocialCandidateMatchStatus.Indeterminate)]
    [InlineData(25, "other", 25, "female", 10, SocialCandidateMatchStatus.Indeterminate)]
    [InlineData(25, "male", 25, "female", 150, SocialCandidateMatchStatus.Excluded)] // Default max is 100 km
    [InlineData(17, "other", 25, "female", null, SocialCandidateMatchStatus.Excluded)] // Underage takes precedence
    public void Evaluator_AllProducedResults_SatisfyConstructorInvariants(
        int callerAge,
        string? callerGender,
        int candidateAge,
        string? candidateGender,
        int? distanceKm,
        SocialCandidateMatchStatus expectedStatus)
    {
        var result = SocialCandidateFilterEvaluator.Evaluate(
            callerAge,
            callerGender,
            SocialExplorePreferences.Default,
            candidateAge,
            candidateGender,
            SocialExplorePreferences.Default,
            distanceKm);

        Assert.Equal(expectedStatus, result.Status);

        switch (result.Status)
        {
            case SocialCandidateMatchStatus.Eligible:
                Assert.Empty(result.ExclusionReasons);
                Assert.Empty(result.IndeterminateReasons);
                break;
            case SocialCandidateMatchStatus.Excluded:
                Assert.NotEmpty(result.ExclusionReasons);
                break;
            case SocialCandidateMatchStatus.Indeterminate:
                Assert.Empty(result.ExclusionReasons);
                Assert.NotEmpty(result.IndeterminateReasons);
                break;
        }
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
