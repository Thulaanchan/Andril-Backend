using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Comprehensive unit, immutability, structural equality, and privacy regression tests for <see cref="SocialExploreCandidateCard"/>.
/// </summary>
public sealed class SocialExploreCandidateCardTests
{
    private static readonly Guid SyntheticProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SyntheticDisplayName = "Alex";
    private const int SyntheticAge = 25;
    private const int SyntheticDistanceKm = 10;

    private static SocialCandidateEvaluationResult CreateEligibleResult(
        IEnumerable<string>? languages = null,
        IEnumerable<string>? goals = null) =>
        new(
            SocialCandidateMatchStatus.Eligible,
            exclusionReasons: null,
            indeterminateReasons: null,
            sharedLanguages: languages ?? new[] { "English", "Tamil" },
            sharedConnectionGoals: goals ?? new[] { "Dating" });

    private static SocialCandidateEvaluationResult CreateExcludedResult(
        SocialCandidateExclusionReason reason = SocialCandidateExclusionReason.CallerUnderage) =>
        new(
            SocialCandidateMatchStatus.Excluded,
            exclusionReasons: new[] { reason },
            indeterminateReasons: null);

    private static SocialCandidateEvaluationResult CreateIndeterminateResult(
        SocialCandidateIndeterminateReason reason = SocialCandidateIndeterminateReason.DistanceUnavailable) =>
        new(
            SocialCandidateMatchStatus.Indeterminate,
            exclusionReasons: null,
            indeterminateReasons: new[] { reason });

    private static SocialExploreCandidateCard CreateValidCard(
        Guid? profileId = null,
        string? displayName = null,
        int? age = null,
        int? distanceKm = 10,
        SocialCandidateEvaluationResult? evaluationResult = null) =>
        SocialExploreCandidateCard.Create(
            profileId ?? SyntheticProfileId,
            displayName ?? SyntheticDisplayName,
            age ?? SyntheticAge,
            distanceKm,
            evaluationResult ?? CreateEligibleResult());

    #region SECTION A — Valid Snapshot Construction

    [Fact]
    public void Create_ValidCandidateProfileId_IsPreserved()
    {
        var expectedId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var card = CreateValidCard(profileId: expectedId);

        Assert.Equal(expectedId, card.CandidateProfileId);
    }

    [Fact]
    public void Create_ValidDisplayName_IsPreserved()
    {
        var card = CreateValidCard(displayName: "Maya");

        Assert.Equal("Maya", card.DisplayName);
    }

    [Theory]
    [InlineData("  Maya  ", "Maya")]
    [InlineData("\tJordan\n", "Jordan")]
    [InlineData(" Sam ", "Sam")]
    public void Create_LeadingAndTrailingDisplayNameWhitespace_IsTrimmed(string input, string expected)
    {
        var card = CreateValidCard(displayName: input);

        Assert.Equal(expected, card.DisplayName);
    }

    [Fact]
    public void Create_Age18_IsAccepted()
    {
        var card = CreateValidCard(age: 18);

        Assert.Equal(18, card.Age);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(105)]
    [InlineData(120)]
    public void Create_AgeAbove99_IsAcceptedBecauseExploreLimitIsNotHumanMaxAge(int ageAbove99)
    {
        var card = CreateValidCard(age: ageAbove99);

        Assert.Equal(ageAbove99, card.Age);
    }

    [Fact]
    public void Create_PositiveApproximateDistance_IsPreserved()
    {
        var card = CreateValidCard(distanceKm: 42);

        Assert.Equal(42, card.ApproximateDistanceKm);
    }

    [Fact]
    public void Create_ZeroApproximateDistance_IsAccepted()
    {
        var card = CreateValidCard(distanceKm: 0);

        Assert.Equal(0, card.ApproximateDistanceKm);
    }

    [Fact]
    public void Create_NullApproximateDistance_IsAcceptedWhenEligibleResultSupplied()
    {
        var card = CreateValidCard(distanceKm: null);

        Assert.Null(card.ApproximateDistanceKm);
    }

    [Fact]
    public void Create_EmptySharedLanguages_IsAccepted()
    {
        var evalResult = CreateEligibleResult(languages: Array.Empty<string>());
        var card = CreateValidCard(evaluationResult: evalResult);

        Assert.Empty(card.SharedLanguages);
    }

    [Fact]
    public void Create_EmptySharedConnectionGoals_IsAccepted()
    {
        var evalResult = CreateEligibleResult(goals: Array.Empty<string>());
        var card = CreateValidCard(evaluationResult: evalResult);

        Assert.Empty(card.SharedConnectionGoals);
    }

    [Fact]
    public void Create_SharedLanguagesAndGoals_AreCopiedFromEvaluationResult()
    {
        var expectedLanguages = new[] { "French", "Spanish" };
        var expectedGoals = new[] { "Friendship", "Meaningful Connection" };
        var evalResult = CreateEligibleResult(languages: expectedLanguages, goals: expectedGoals);

        var card = CreateValidCard(evaluationResult: evalResult);

        Assert.Equal(2, card.SharedLanguages.Count);
        Assert.Equal("French", card.SharedLanguages[0]);
        Assert.Equal("Spanish", card.SharedLanguages[1]);

        Assert.Equal(2, card.SharedConnectionGoals.Count);
        Assert.Equal("Friendship", card.SharedConnectionGoals[0]);
        Assert.Equal("Meaningful Connection", card.SharedConnectionGoals[1]);
    }

    [Fact]
    public void Create_Snapshot_DoesNotIntroduceSyntheticCompatibilityInformation()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var forbiddenNames = new[] { "Score", "Percentage", "Compatibility", "Rank", "Weight" };
        foreach (var property in properties)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.DoesNotContain(forbidden, property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region SECTION B — Invalid Input Rejection

    [Fact]
    public void Create_EmptyCandidateProfileId_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CreateValidCard(profileId: Guid.Empty));

        Assert.Equal("candidateProfileId", ex.ParamName);
    }

    [Fact]
    public void Create_NullDisplayName_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            SocialExploreCandidateCard.Create(
                SyntheticProfileId,
                null!,
                SyntheticAge,
                SyntheticDistanceKm,
                CreateEligibleResult()));

        Assert.Equal("displayName", ex.ParamName);
    }

    [Fact]
    public void Create_EmptyDisplayName_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CreateValidCard(displayName: string.Empty));

        Assert.Equal("displayName", ex.ParamName);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Create_WhitespaceOnlyDisplayName_ThrowsArgumentException(string whitespace)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CreateValidCard(displayName: whitespace));

        Assert.Equal("displayName", ex.ParamName);
    }

    [Fact]
    public void Create_Age17_ThrowsArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateValidCard(age: 17));

        Assert.Equal("age", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-18)]
    [InlineData(-100)]
    public void Create_NegativeAge_ThrowsArgumentOutOfRangeException(int negativeAge)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateValidCard(age: negativeAge));

        Assert.Equal("age", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    public void Create_NegativeApproximateDistance_ThrowsArgumentOutOfRangeException(int negativeDistance)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateValidCard(distanceKm: negativeDistance));

        Assert.Equal("approximateDistanceKm", ex.ParamName);
    }

    [Fact]
    public void Create_NullEvaluationResult_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            SocialExploreCandidateCard.Create(
                SyntheticProfileId,
                SyntheticDisplayName,
                SyntheticAge,
                SyntheticDistanceKm,
                null!));

        Assert.Equal("evaluationResult", ex.ParamName);
    }

    [Fact]
    public void Create_ExcludedEvaluationResult_ThrowsArgumentException()
    {
        var excludedResult = CreateExcludedResult();

        var ex = Assert.Throws<ArgumentException>(() =>
            CreateValidCard(evaluationResult: excludedResult));

        Assert.Equal("evaluationResult", ex.ParamName);
        Assert.Contains("eligible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_IndeterminateEvaluationResult_ThrowsArgumentException()
    {
        var indeterminateResult = CreateIndeterminateResult();

        var ex = Assert.Throws<ArgumentException>(() =>
            CreateValidCard(evaluationResult: indeterminateResult));

        Assert.Equal("evaluationResult", ex.ParamName);
        Assert.Contains("eligible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SECTION C — Immutability and Defensive Collections

    [Fact]
    public void Class_HasNoPublicConstructors()
    {
        var publicConstructors = typeof(SocialExploreCandidateCard)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(publicConstructors);
    }

    [Fact]
    public void AllDataProperties_AreGetterOnly()
    {
        var properties = typeof(SocialExploreCandidateCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Equal(6, properties.Length);

        foreach (var property in properties)
        {
            Assert.True(property.CanRead);
            Assert.NotNull(property.GetGetMethod(nonPublic: false));
        }
    }

    [Fact]
    public void NoPublicDataProperty_ExposesSetter()
    {
        var properties = typeof(SocialExploreCandidateCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            Assert.Null(property.GetSetMethod(nonPublic: false));
        }
    }

    [Fact]
    public void SharedLanguages_RejectsMutationWhenCastToIList()
    {
        var card = CreateValidCard();

        var list = card.SharedLanguages as IList<string>;
        Assert.NotNull(list);
        Assert.Throws<NotSupportedException>(() => list.Add("German"));
        Assert.Throws<NotSupportedException>(() => list.Remove("English"));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = "German");
    }

    [Fact]
    public void SharedConnectionGoals_RejectsMutationWhenCastToIList()
    {
        var card = CreateValidCard();

        var list = card.SharedConnectionGoals as IList<string>;
        Assert.NotNull(list);
        Assert.Throws<NotSupportedException>(() => list.Add("Companionship"));
        Assert.Throws<NotSupportedException>(() => list.Remove("Dating"));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Throws<NotSupportedException>(() => list[0] = "Companionship");
    }

    [Fact]
    public void MutatingOriginalInputList_AfterCardCreation_DoesNotMutateCard()
    {
        var mutableLanguages = new List<string> { "English", "Tamil" };
        var mutableGoals = new List<string> { "Dating" };
        var evalResult = CreateEligibleResult(languages: mutableLanguages, goals: mutableGoals);

        var card = CreateValidCard(evaluationResult: evalResult);

        mutableLanguages.Add("German");
        mutableLanguages.Clear();
        mutableGoals.Add("Friendship");
        mutableGoals.Clear();

        Assert.Equal(2, card.SharedLanguages.Count);
        Assert.Equal("English", card.SharedLanguages[0]);
        Assert.Equal("Tamil", card.SharedLanguages[1]);

        Assert.Single(card.SharedConnectionGoals);
        Assert.Equal("Dating", card.SharedConnectionGoals[0]);
    }

    [Fact]
    public void TwoCardsBuiltFromEquivalentIndependentInputs_DoNotShareUnderlyingCollectionReferences()
    {
        var evalResult1 = CreateEligibleResult(new[] { "English" }, new[] { "Dating" });
        var evalResult2 = CreateEligibleResult(new[] { "English" }, new[] { "Dating" });

        var card1 = CreateValidCard(evaluationResult: evalResult1);
        var card2 = CreateValidCard(evaluationResult: evalResult2);

        Assert.NotSame(card1.SharedLanguages, card2.SharedLanguages);
        Assert.NotSame(card1.SharedConnectionGoals, card2.SharedConnectionGoals);
    }

    [Fact]
    public void CardCreation_DoesNotModifySuppliedEvaluationResult()
    {
        var evalResult = CreateEligibleResult();
        var statusBefore = evalResult.Status;
        var langCountBefore = evalResult.SharedLanguages.Count;
        var goalCountBefore = evalResult.SharedConnectionGoals.Count;

        var card = CreateValidCard(evaluationResult: evalResult);

        Assert.Equal(statusBefore, evalResult.Status);
        Assert.Equal(langCountBefore, evalResult.SharedLanguages.Count);
        Assert.Equal(goalCountBefore, evalResult.SharedConnectionGoals.Count);
    }

    [Fact]
    public void FactoryCreation_PreservesInputValuesExactlyWithoutMutation()
    {
        var profileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string name = "Taylor";
        const int age = 29;
        const int distance = 15;
        var evalResult = CreateEligibleResult();

        var card = SocialExploreCandidateCard.Create(profileId, name, age, distance, evalResult);

        Assert.Equal(profileId, card.CandidateProfileId);
        Assert.Equal(name, card.DisplayName);
        Assert.Equal(age, card.Age);
        Assert.Equal(distance, card.ApproximateDistanceKm);
    }

    [Fact]
    public void Snapshot_ContainsNoPubliclyMutableNestedCollections()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var isGenericList = property.PropertyType.IsGenericType &&
                                property.PropertyType.GetGenericTypeDefinition() == typeof(List<>);
            Assert.False(isGenericList, $"Property {property.Name} should not be mutable List<T>.");
        }
    }

    #endregion

    #region SECTION D — Structural Equality

    [Fact]
    public void Equals_SameInstance_ReturnsTrue()
    {
        var card = CreateValidCard();
        var sameCardReference = card;

        Assert.True(card.Equals(sameCardReference));
        Assert.True(card == sameCardReference);
        Assert.False(card != sameCardReference);
    }

    [Fact]
    public void Equals_TwoIndependentlyConstructedCardsWithIdenticalValues_AreEqual()
    {
        var card1 = CreateValidCard();
        var card2 = CreateValidCard();

        Assert.NotSame(card1, card2);
        Assert.True(card1.Equals(card2));
        Assert.True(card2.Equals(card1));
        Assert.True(card1 == card2);
        Assert.False(card1 != card2);
        Assert.Equal(card1, card2);
    }

    [Fact]
    public void GetHashCode_EqualCards_ProduceIdenticalHashCodes()
    {
        var card1 = CreateValidCard();
        var card2 = CreateValidCard();

        Assert.Equal(card1.GetHashCode(), card2.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentCandidateProfileId_ReturnsFalse()
    {
        var card1 = CreateValidCard(profileId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var card2 = CreateValidCard(profileId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

        Assert.False(card1.Equals(card2));
        Assert.False(card1 == card2);
        Assert.True(card1 != card2);
    }

    [Fact]
    public void Equals_DifferentDisplayName_ReturnsFalse()
    {
        var card1 = CreateValidCard(displayName: "Alex");
        var card2 = CreateValidCard(displayName: "Sam");

        Assert.False(card1.Equals(card2));
        Assert.False(card1 == card2);
        Assert.True(card1 != card2);
    }

    [Fact]
    public void Equals_DifferentAge_ReturnsFalse()
    {
        var card1 = CreateValidCard(age: 25);
        var card2 = CreateValidCard(age: 26);

        Assert.False(card1.Equals(card2));
        Assert.False(card1 == card2);
        Assert.True(card1 != card2);
    }

    [Fact]
    public void Equals_DifferentDistance_ReturnsFalse()
    {
        var card1 = CreateValidCard(distanceKm: 10);
        var card2 = CreateValidCard(distanceKm: 20);
        var card3 = CreateValidCard(distanceKm: null);

        Assert.False(card1.Equals(card2));
        Assert.False(card1.Equals(card3));
        Assert.False(card2.Equals(card3));
    }

    [Fact]
    public void Equals_DifferentSharedLanguages_ReturnsFalse()
    {
        var card1 = CreateValidCard(evaluationResult: CreateEligibleResult(languages: new[] { "English" }));
        var card2 = CreateValidCard(evaluationResult: CreateEligibleResult(languages: new[] { "Spanish" }));

        Assert.False(card1.Equals(card2));
        Assert.False(card1 == card2);
    }

    [Fact]
    public void Equals_DifferentSharedConnectionGoals_ReturnsFalse()
    {
        var card1 = CreateValidCard(evaluationResult: CreateEligibleResult(goals: new[] { "Dating" }));
        var card2 = CreateValidCard(evaluationResult: CreateEligibleResult(goals: new[] { "Friendship" }));

        Assert.False(card1.Equals(card2));
        Assert.False(card1 == card2);
    }

    [Fact]
    public void Equals_DifferentSharedLanguageOrder_ReturnsFalse()
    {
        var card1 = CreateValidCard(evaluationResult: CreateEligibleResult(languages: new[] { "English", "French" }));
        var card2 = CreateValidCard(evaluationResult: CreateEligibleResult(languages: new[] { "French", "English" }));

        Assert.False(card1.Equals(card2));
    }

    [Fact]
    public void Equals_WithNull_ReturnsFalse()
    {
        var card = CreateValidCard();

        Assert.False(card.Equals((SocialExploreCandidateCard?)null));
        Assert.False(card.Equals((object?)null));
        Assert.False(card == null);
        Assert.True(card != null);
    }

    [Fact]
    public void Equals_WithUnrelatedObjectType_ReturnsFalse()
    {
        var card = CreateValidCard();

        Assert.False(card.Equals("unrelated string"));
        Assert.False(card.Equals(42));
        Assert.False(card.Equals(Guid.NewGuid()));
    }

    [Fact]
    public void EqualityOperators_BehaveConsistentlyWithEquals()
    {
        var card1 = CreateValidCard();
        var card2 = CreateValidCard();
        var card3 = CreateValidCard(displayName: "Different");

        Assert.True(card1 == card2);
        Assert.False(card1 != card2);

        Assert.False(card1 == card3);
        Assert.True(card1 != card3);

        SocialExploreCandidateCard? nullCard1 = null;
        SocialExploreCandidateCard? nullCard2 = null;

        Assert.True(nullCard1 == nullCard2);
        Assert.False(nullCard1 != nullCard2);
        Assert.False(card1 == nullCard1);
        Assert.True(card1 != nullCard1);
    }

    [Fact]
    public void HashSet_ContainsLookup_WorksForEqualCards()
    {
        var card1 = CreateValidCard();
        var card2 = CreateValidCard();

        var set = new HashSet<SocialExploreCandidateCard> { card1 };

        Assert.Contains(card2, set);
    }

    #endregion

    #region SECTION E — Privacy and Serialization

    [Fact]
    public void EveryPublicDataProperty_CarriesJsonIgnoreAttribute()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(6, properties.Length);

        foreach (var property in properties)
        {
            var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
            Assert.NotNull(jsonIgnore);
        }
    }

    [Fact]
    public void DefaultJsonSerialization_ProducesEmptyJsonObject()
    {
        var card = CreateValidCard();

        var json = JsonSerializer.Serialize(card);

        Assert.Equal("{}", json);
    }

    [Fact]
    public void DefaultJsonSerialization_DoesNotSerializeCandidateProfileGuid()
    {
        var card = CreateValidCard();
        var json = JsonSerializer.Serialize(card);

        Assert.DoesNotContain(SyntheticProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultJsonSerialization_DoesNotSerializeDisplayName()
    {
        var card = CreateValidCard(displayName: "SecretName");
        var json = JsonSerializer.Serialize(card);

        Assert.DoesNotContain("SecretName", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultJsonSerialization_DoesNotSerializeAgeOrDistance()
    {
        var card = CreateValidCard(age: 28, distanceKm: 75);
        var json = JsonSerializer.Serialize(card);

        Assert.DoesNotContain("28", json, StringComparison.Ordinal);
        Assert.DoesNotContain("75", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultJsonSerialization_DoesNotSerializeSharedLanguagesOrGoals()
    {
        var card = CreateValidCard(evaluationResult: CreateEligibleResult(new[] { "Mandarin Chinese" }, new[] { "Life Partner" }));
        var json = JsonSerializer.Serialize(card);

        Assert.DoesNotContain("Mandarin Chinese", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Life Partner", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_ReturnsExactSafeConstantString()
    {
        var card = CreateValidCard();

        var result = card.ToString();

        Assert.Equal("SocialExploreCandidateCard [private]", result);
    }

    [Fact]
    public void ToString_DoesNotContainCandidateDetails()
    {
        var card = CreateValidCard(
            profileId: Guid.Parse("99999999-9999-9999-9999-999999999999"),
            displayName: "ClassifiedName",
            age: 33,
            distanceKm: 55);

        var result = card.ToString();

        Assert.DoesNotContain("99999999-9999-9999-9999-999999999999", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClassifiedName", result, StringComparison.Ordinal);
        Assert.DoesNotContain("33", result, StringComparison.Ordinal);
        Assert.DoesNotContain("55", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_DoesNotExposeQuestionnaireAnswersOrQuestionIds()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, p => p.Name.Contains("Answer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Question", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Class_DoesNotExposeQuestionnaireStatusProperty()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, p => p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Participation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Skip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Class_DoesNotExposeCompatibilityScoresOrWeights()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(properties, p => p.Name.Contains("Score", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Percent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Weight", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Rank", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Class_DoesNotExposeInternalAccountOrContactOrLocationCoordinates()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedFields = new[] { "AccountId", "UserId", "Email", "Phone", "BirthDate", "DateOfBirth", "Latitude", "Longitude", "Coordinate" };
        foreach (var property in properties)
        {
            foreach (var prohibited in prohibitedFields)
            {
                Assert.DoesNotContain(prohibited, property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region SECTION F — Matching and Authorization Boundary

    [Fact]
    public void Create_EligibleResult_PermitsConstruction()
    {
        var eligibleResult = CreateEligibleResult();

        var card = CreateValidCard(evaluationResult: eligibleResult);

        Assert.NotNull(card);
        Assert.Equal(SyntheticProfileId, card.CandidateProfileId);
    }

    [Fact]
    public void Create_ExcludedResult_DoesNotPermitConstruction()
    {
        var excludedResult = CreateExcludedResult();

        Assert.Throws<ArgumentException>(() =>
            CreateValidCard(evaluationResult: excludedResult));
    }

    [Fact]
    public void Create_IndeterminateResult_DoesNotPermitConstruction()
    {
        var indeterminateResult = CreateIndeterminateResult();

        Assert.Throws<ArgumentException>(() =>
            CreateValidCard(evaluationResult: indeterminateResult));
    }

    [Fact]
    public void Class_ExposesNoAuthenticationOrAuthorizationMethods()
    {
        var methods = typeof(SocialExploreCandidateCard).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var prohibitedMethodWords = new[] { "Authenticate", "Authorize", "Login", "Verify", "Token", "Permission" };
        foreach (var method in methods)
        {
            foreach (var word in prohibitedMethodWords)
            {
                Assert.DoesNotContain(word, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Class_ExposesNoPublicPublishingOrApiEndpoints()
    {
        var methods = typeof(SocialExploreCandidateCard).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var prohibitedWords = new[] { "Publish", "Send", "Deliver", "Post", "Dispatch", "Broadcast" };
        foreach (var method in methods)
        {
            foreach (var word in prohibitedWords)
            {
                Assert.DoesNotContain(word, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Class_HasNoDependenciesOnDbContextOrDatabasePersistence()
    {
        var fields = typeof(SocialExploreCandidateCard).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        foreach (var field in fields)
        {
            Assert.DoesNotContain("DbContext", field.FieldType.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Repository", field.FieldType.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Database", field.FieldType.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Class_HasNoPermissionsOrRelationshipsState()
    {
        var properties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedConcepts = new[] { "Blocked", "Matched", "Connected", "Permitted", "Allowed", "Granted" };
        foreach (var property in properties)
        {
            foreach (var concept in prohibitedConcepts)
            {
                Assert.DoesNotContain(concept, property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion
}
