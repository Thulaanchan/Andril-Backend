using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialExplorePreferencesTests
{
    #region A. Defaults

    [Fact]
    public void DefaultProperty_ExposesApprovedDefaults()
    {
        var prefs = SocialExplorePreferences.Default;

        Assert.NotNull(prefs.RangeFilters);
        Assert.Equal(18, prefs.MinAge);
        Assert.Equal(35, prefs.MaxAge);
        Assert.Equal(0, prefs.MinDistanceKm);
        Assert.Equal(100, prefs.MaxDistanceKm);
        Assert.Equal("All", prefs.ShowMe);
        Assert.Equal(SocialGenderPreference.All, prefs.GenderPreference);
        Assert.Single(prefs.Languages);
        Assert.Equal("English", prefs.Languages[0]);
        Assert.Empty(prefs.ConnectionGoals);
    }

    [Fact]
    public void ParameterlessConstructor_MatchesDefaultInstance()
    {
        var constructed = new SocialExplorePreferences();
        var defaultInstance = SocialExplorePreferences.Default;

        Assert.Equal(defaultInstance.RangeFilters, constructed.RangeFilters);
        Assert.Equal(defaultInstance.MinAge, constructed.MinAge);
        Assert.Equal(defaultInstance.MaxAge, constructed.MaxAge);
        Assert.Equal(defaultInstance.MinDistanceKm, constructed.MinDistanceKm);
        Assert.Equal(defaultInstance.MaxDistanceKm, constructed.MaxDistanceKm);
        Assert.Equal(defaultInstance.ShowMe, constructed.ShowMe);
        Assert.Equal(defaultInstance.GenderPreference, constructed.GenderPreference);
        Assert.Equal(defaultInstance.Languages, constructed.Languages);
        Assert.Equal(defaultInstance.ConnectionGoals, constructed.ConnectionGoals);
        Assert.Equal(defaultInstance, constructed);
    }

    [Fact]
    public void PassThroughProperties_MatchUnderlyingRangeFilters()
    {
        var customRange = new SocialExploreRangeFilters(22, 45, 15, 150);
        var prefs = new SocialExplorePreferences(rangeFilters: customRange);

        Assert.Same(customRange, prefs.RangeFilters);
        Assert.Equal(customRange.MinAge, prefs.MinAge);
        Assert.Equal(customRange.MaxAge, prefs.MaxAge);
        Assert.Equal(customRange.MinDistanceKm, prefs.MinDistanceKm);
        Assert.Equal(customRange.MaxDistanceKm, prefs.MaxDistanceKm);
    }

    #endregion

    #region B. Gender Preferences

    [Theory]
    [InlineData("Men", "Men", SocialGenderPreference.Men)]
    [InlineData("men", "Men", SocialGenderPreference.Men)]
    [InlineData("MEN", "Men", SocialGenderPreference.Men)]
    [InlineData("Women", "Women", SocialGenderPreference.Women)]
    [InlineData("women", "Women", SocialGenderPreference.Women)]
    [InlineData("WOMEN", "Women", SocialGenderPreference.Women)]
    [InlineData("All", "All", SocialGenderPreference.All)]
    [InlineData("all", "All", SocialGenderPreference.All)]
    [InlineData("ALL", "All", SocialGenderPreference.All)]
    public void StringConstructor_NormalizesAndSetsGenderPreference(
        string inputShowMe,
        string expectedShowMe,
        SocialGenderPreference expectedEnum)
    {
        var prefs = new SocialExplorePreferences(showMe: inputShowMe);

        Assert.Equal(expectedShowMe, prefs.ShowMe);
        Assert.Equal(expectedEnum, prefs.GenderPreference);
    }

    [Theory]
    [InlineData(SocialGenderPreference.Men, "Men")]
    [InlineData(SocialGenderPreference.Women, "Women")]
    [InlineData(SocialGenderPreference.All, "All")]
    public void EnumConstructor_SetsShowMeAndGenderPreference(
        SocialGenderPreference inputPreference,
        string expectedShowMe)
    {
        var prefs = new SocialExplorePreferences(rangeFilters: null, showMe: inputPreference);

        Assert.Equal(expectedShowMe, prefs.ShowMe);
        Assert.Equal(inputPreference, prefs.GenderPreference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NonBinary")]
    [InlineData("Everyone")]
    [InlineData("Male")]
    [InlineData("Female")]
    [InlineData("Other")]
    public void StringConstructor_InvalidShowMe_ThrowsArgumentException(string invalidShowMe)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(showMe: invalidShowMe));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void EnumConstructor_InvalidEnumValue_ThrowsArgumentOutOfRangeException()
    {
        var invalidEnum = (SocialGenderPreference)999;
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExplorePreferences(rangeFilters: null, showMe: invalidEnum));

        Assert.Equal("showMe", ex.ParamName);
        Assert.Equal(invalidEnum, ex.ActualValue);
    }

    [Fact]
    public void SocialGenderPreferences_StaticMembers_ExposeApprovedVocabulary()
    {
        Assert.Equal("Men", SocialGenderPreferences.Men);
        Assert.Equal("Women", SocialGenderPreferences.Women);
        Assert.Equal("All", SocialGenderPreferences.All);
        Assert.Equal(SocialGenderPreferences.All, SocialGenderPreferences.Default);

        Assert.Equal(3, SocialGenderPreferences.AllOptions.Count);
        Assert.Contains("Men", SocialGenderPreferences.AllOptions);
        Assert.Contains("Women", SocialGenderPreferences.AllOptions);
        Assert.Contains("All", SocialGenderPreferences.AllOptions);

        Assert.True(SocialGenderPreferences.IsValid("men"));
        Assert.True(SocialGenderPreferences.IsValid("WOMEN"));
        Assert.True(SocialGenderPreferences.IsValid("All"));
        Assert.False(SocialGenderPreferences.IsValid("Invalid"));
        Assert.False(SocialGenderPreferences.IsValid(null));
        Assert.False(SocialGenderPreferences.IsValid(""));
    }

    #endregion

    #region C. Languages

    [Fact]
    public void NullLanguages_DefaultsToEnglishOnly()
    {
        var prefs = new SocialExplorePreferences(languages: null);

        Assert.Single(prefs.Languages);
        Assert.Equal("English", prefs.Languages[0]);
    }

    [Theory]
    [InlineData("English", "English")]
    [InlineData("Tamil", "Tamil")]
    [InlineData("Hindi", "Hindi")]
    [InlineData("Arabic", "Arabic")]
    [InlineData("French", "French")]
    [InlineData("Spanish", "Spanish")]
    [InlineData("German", "German")]
    [InlineData("Mandarin Chinese", "Mandarin Chinese")]
    public void SingleApprovedLanguage_AcceptedAndNormalized(string inputLanguage, string expectedLanguage)
    {
        var prefs = new SocialExplorePreferences(languages: new[] { inputLanguage });

        Assert.Single(prefs.Languages);
        Assert.Equal(expectedLanguage, prefs.Languages[0]);
    }

    [Fact]
    public void SingleNonEnglishLanguage_ValidAndEnglishIsNotCompulsory()
    {
        var prefs = new SocialExplorePreferences(languages: new[] { "Tamil" });

        Assert.Single(prefs.Languages);
        Assert.Equal("Tamil", prefs.Languages[0]);
        Assert.DoesNotContain("English", prefs.Languages);
    }

    [Fact]
    public void MultipleApprovedLanguages_AcceptedAndOrderPreserved()
    {
        var input = new[] { "Tamil", "English", "German" };
        var prefs = new SocialExplorePreferences(languages: input);

        Assert.Equal(3, prefs.Languages.Count);
        Assert.Equal("Tamil", prefs.Languages[0]);
        Assert.Equal("English", prefs.Languages[1]);
        Assert.Equal("German", prefs.Languages[2]);
    }

    [Fact]
    public void AllApprovedLanguages_CanBeSelectedSimultaneously()
    {
        var prefs = new SocialExplorePreferences(languages: SocialLanguages.All);

        Assert.Equal(8, prefs.Languages.Count);
        Assert.Equal(SocialLanguages.All, prefs.Languages);
    }

    [Theory]
    [InlineData("tamil", "Tamil")]
    [InlineData("  HINDI  ", "Hindi")]
    [InlineData("mandarin chinese", "Mandarin Chinese")]
    [InlineData("FRENCH", "French")]
    public void Languages_CasingAndWhitespace_AreNormalized(string inputLanguage, string expectedLanguage)
    {
        var prefs = new SocialExplorePreferences(languages: new[] { inputLanguage });

        Assert.Single(prefs.Languages);
        Assert.Equal(expectedLanguage, prefs.Languages[0]);
    }

    [Fact]
    public void EmptyLanguagesCollection_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(languages: Array.Empty<string>()));

        Assert.Equal("languages", ex.ParamName);
        Assert.Contains("At least one language must always remain selected", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LanguageWithNullOrBlankEntry_ThrowsArgumentException(string? blankEntry)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(languages: new[] { "English", blankEntry! }));

        Assert.Equal("languages", ex.ParamName);
        Assert.Contains("Language entry cannot be null or blank", ex.Message);
    }

    [Theory]
    [InlineData("Latin")]
    [InlineData("Japanese")]
    [InlineData("Russian")]
    [InlineData("Italian")]
    [InlineData("Sinhala")]
    public void UnsupportedLanguage_ThrowsArgumentException(string unsupportedLanguage)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(languages: new[] { unsupportedLanguage }));

        Assert.Equal("value", ex.ParamName);
        Assert.Contains("Unsupported language", ex.Message);
    }

    [Fact]
    public void DuplicateLanguageEntries_ExactMatch_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(languages: new[] { "English", "Tamil", "English" }));

        Assert.Equal("languages", ex.ParamName);
        Assert.Contains("Duplicate language entry 'English'", ex.Message);
    }

    [Fact]
    public void DuplicateLanguageEntries_CaseInsensitive_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(languages: new[] { "Tamil", "tamil" }));

        Assert.Equal("languages", ex.ParamName);
        Assert.Contains("Duplicate language entry 'tamil'", ex.Message);
    }

    [Fact]
    public void SocialLanguages_StaticMembers_ExposeApprovedVocabulary()
    {
        Assert.Equal(8, SocialLanguages.All.Count);
        Assert.Contains("English", SocialLanguages.All);
        Assert.Contains("Tamil", SocialLanguages.All);
        Assert.Contains("Hindi", SocialLanguages.All);
        Assert.Contains("Arabic", SocialLanguages.All);
        Assert.Contains("French", SocialLanguages.All);
        Assert.Contains("Spanish", SocialLanguages.All);
        Assert.Contains("German", SocialLanguages.All);
        Assert.Contains("Mandarin Chinese", SocialLanguages.All);

        Assert.True(SocialLanguages.IsValid("English"));
        Assert.True(SocialLanguages.IsValid("tamil"));
        Assert.True(SocialLanguages.IsValid("  Mandarin Chinese  "));
        Assert.False(SocialLanguages.IsValid("Latin"));
        Assert.False(SocialLanguages.IsValid(null));
        Assert.False(SocialLanguages.IsValid(""));
    }

    #endregion

    #region D. Connection Goals

    [Fact]
    public void NullConnectionGoals_DefaultsToEmptyList()
    {
        var prefs = new SocialExplorePreferences(connectionGoals: null);

        Assert.Empty(prefs.ConnectionGoals);
    }

    [Fact]
    public void EmptyConnectionGoals_AcceptedAsZeroGoals()
    {
        var prefs = new SocialExplorePreferences(connectionGoals: Array.Empty<string>());

        Assert.Empty(prefs.ConnectionGoals);
    }

    [Theory]
    [InlineData("Serious Relationship", "Serious Relationship")]
    [InlineData("Long-term Partner", "Long-term Partner")]
    [InlineData("Life Partner", "Life Partner")]
    [InlineData("Dating", "Dating")]
    [InlineData("Friendship", "Friendship")]
    [InlineData("Meaningful Connection", "Meaningful Connection")]
    [InlineData("Companionship", "Companionship")]
    [InlineData("Open to Explore", "Open to Explore")]
    public void SingleApprovedConnectionGoal_AcceptedAndNormalized(string inputGoal, string expectedGoal)
    {
        var prefs = new SocialExplorePreferences(connectionGoals: new[] { inputGoal });

        Assert.Single(prefs.ConnectionGoals);
        Assert.Equal(expectedGoal, prefs.ConnectionGoals[0]);
    }

    [Fact]
    public void MultipleApprovedConnectionGoals_AcceptedAndOrderPreserved()
    {
        var input = new[] { "Friendship", "Dating", "Open to Explore" };
        var prefs = new SocialExplorePreferences(connectionGoals: input);

        Assert.Equal(3, prefs.ConnectionGoals.Count);
        Assert.Equal("Friendship", prefs.ConnectionGoals[0]);
        Assert.Equal("Dating", prefs.ConnectionGoals[1]);
        Assert.Equal("Open to Explore", prefs.ConnectionGoals[2]);
    }

    [Fact]
    public void AllApprovedConnectionGoals_CanBeSelectedSimultaneously()
    {
        var prefs = new SocialExplorePreferences(connectionGoals: SocialConnectionGoals.All);

        Assert.Equal(8, prefs.ConnectionGoals.Count);
        Assert.Equal(SocialConnectionGoals.All, prefs.ConnectionGoals);
    }

    [Theory]
    [InlineData("dating", "Dating")]
    [InlineData("  FRIENDSHIP  ", "Friendship")]
    [InlineData("life partner", "Life Partner")]
    [InlineData("OPEN TO EXPLORE", "Open to Explore")]
    public void ConnectionGoals_CasingAndWhitespace_AreNormalized(string inputGoal, string expectedGoal)
    {
        var prefs = new SocialExplorePreferences(connectionGoals: new[] { inputGoal });

        Assert.Single(prefs.ConnectionGoals);
        Assert.Equal(expectedGoal, prefs.ConnectionGoals[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConnectionGoalWithNullOrBlankEntry_ThrowsArgumentException(string? blankEntry)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(connectionGoals: new[] { "Friendship", blankEntry! }));

        Assert.Equal("goals", ex.ParamName);
        Assert.Contains("Connection goal cannot be null or blank", ex.Message);
    }

    [Theory]
    [InlineData("Casual Hookup")]
    [InlineData("Marriage")]
    [InlineData("Business Networking")]
    [InlineData("Travel Buddy")]
    public void UnsupportedConnectionGoal_ThrowsArgumentException(string unsupportedGoal)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(connectionGoals: new[] { unsupportedGoal }));

        Assert.Equal("value", ex.ParamName);
        Assert.Contains("Unsupported connection goal", ex.Message);
    }

    [Fact]
    public void DuplicateConnectionGoals_ExactMatch_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(connectionGoals: new[] { "Dating", "Friendship", "Dating" }));

        Assert.Equal("goals", ex.ParamName);
        Assert.Contains("Duplicate connection goal 'Dating'", ex.Message);
    }

    [Fact]
    public void DuplicateConnectionGoals_CaseInsensitive_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExplorePreferences(connectionGoals: new[] { "Friendship", "friendship" }));

        Assert.Equal("goals", ex.ParamName);
        Assert.Contains("Duplicate connection goal 'friendship'", ex.Message);
    }

    [Fact]
    public void SocialConnectionGoals_StaticMembers_ExposeApprovedVocabulary()
    {
        Assert.Equal(8, SocialConnectionGoals.All.Count);
        Assert.Contains("Serious Relationship", SocialConnectionGoals.All);
        Assert.Contains("Long-term Partner", SocialConnectionGoals.All);
        Assert.Contains("Life Partner", SocialConnectionGoals.All);
        Assert.Contains("Dating", SocialConnectionGoals.All);
        Assert.Contains("Friendship", SocialConnectionGoals.All);
        Assert.Contains("Meaningful Connection", SocialConnectionGoals.All);
        Assert.Contains("Companionship", SocialConnectionGoals.All);
        Assert.Contains("Open to Explore", SocialConnectionGoals.All);

        Assert.True(SocialConnectionGoals.IsValid("Dating"));
        Assert.True(SocialConnectionGoals.IsValid("friendship"));
        Assert.True(SocialConnectionGoals.IsValid("  Open to Explore  "));
        Assert.False(SocialConnectionGoals.IsValid("Casual"));
        Assert.False(SocialConnectionGoals.IsValid(null));
        Assert.False(SocialConnectionGoals.IsValid(""));
    }

    #endregion

    #region E. Custom Preferences & Preservation

    [Fact]
    public void CustomPreferences_PreserveExactValuesWithoutFallback()
    {
        var customFilters = new SocialExploreRangeFilters(23, 40, 10, 250);
        var customLanguages = new[] { "Tamil", "English" };
        var customGoals = new[] { "Friendship", "Meaningful Connection" };

        var prefs = new SocialExplorePreferences(
            rangeFilters: customFilters,
            showMe: "Women",
            languages: customLanguages,
            connectionGoals: customGoals);

        Assert.Equal(23, prefs.MinAge);
        Assert.Equal(40, prefs.MaxAge);
        Assert.Equal(10, prefs.MinDistanceKm);
        Assert.Equal(250, prefs.MaxDistanceKm);
        Assert.Equal("Women", prefs.ShowMe);
        Assert.Equal(SocialGenderPreference.Women, prefs.GenderPreference);

        Assert.Equal(2, prefs.Languages.Count);
        Assert.Equal("Tamil", prefs.Languages[0]);
        Assert.Equal("English", prefs.Languages[1]);

        Assert.Equal(2, prefs.ConnectionGoals.Count);
        Assert.Equal("Friendship", prefs.ConnectionGoals[0]);
        Assert.Equal("Meaningful Connection", prefs.ConnectionGoals[1]);
    }

    [Fact]
    public void CustomPreferences_WithTamilOnly_PreservesTamilWithoutAddingEnglish()
    {
        var prefs = new SocialExplorePreferences(
            rangeFilters: new SocialExploreRangeFilters(20, 30, 5, 50),
            showMe: "Men",
            languages: new[] { "Tamil" },
            connectionGoals: new[] { "Dating" });

        Assert.Single(prefs.Languages);
        Assert.Equal("Tamil", prefs.Languages[0]);
        Assert.DoesNotContain("English", prefs.Languages);
    }

    [Fact]
    public void DefaultInstance_RemainsUnchangedAfterCreatingCustomInstances()
    {
        var defaultInstanceBefore = SocialExplorePreferences.Default;

        _ = new SocialExplorePreferences(
            rangeFilters: new SocialExploreRangeFilters(30, 50, 20, 200),
            showMe: "Women",
            languages: new[] { "German" },
            connectionGoals: new[] { "Companionship" });

        var defaultInstanceAfter = SocialExplorePreferences.Default;

        Assert.Equal(18, defaultInstanceAfter.MinAge);
        Assert.Equal(35, defaultInstanceAfter.MaxAge);
        Assert.Equal(0, defaultInstanceAfter.MinDistanceKm);
        Assert.Equal(100, defaultInstanceAfter.MaxDistanceKm);
        Assert.Equal("All", defaultInstanceAfter.ShowMe);
        Assert.Equal(SocialGenderPreference.All, defaultInstanceAfter.GenderPreference);
        Assert.Single(defaultInstanceAfter.Languages);
        Assert.Equal("English", defaultInstanceAfter.Languages[0]);
        Assert.Empty(defaultInstanceAfter.ConnectionGoals);
        Assert.Equal(defaultInstanceBefore, defaultInstanceAfter);
    }

    #endregion

    #region F. Immutability & Defensive Copying

    [Fact]
    public void PublicProperties_AreReadOnlyWithoutSetters()
    {
        var type = typeof(SocialExplorePreferences);
        var properties = new[]
        {
            nameof(SocialExplorePreferences.RangeFilters),
            nameof(SocialExplorePreferences.MinAge),
            nameof(SocialExplorePreferences.MaxAge),
            nameof(SocialExplorePreferences.MinDistanceKm),
            nameof(SocialExplorePreferences.MaxDistanceKm),
            nameof(SocialExplorePreferences.ShowMe),
            nameof(SocialExplorePreferences.GenderPreference),
            nameof(SocialExplorePreferences.Languages),
            nameof(SocialExplorePreferences.ConnectionGoals)
        };

        foreach (var propertyName in properties)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop);
            Assert.False(prop.CanWrite, $"Property {propertyName} should not have a public setter.");
        }
    }

    [Fact]
    public void LanguagesAndGoals_AreReadOnlyCollections_MutationThrows()
    {
        var prefs = new SocialExplorePreferences(
            languages: new[] { "English", "Tamil" },
            connectionGoals: new[] { "Friendship" });

        Assert.IsAssignableFrom<ReadOnlyCollection<string>>(prefs.Languages);
        Assert.IsAssignableFrom<ReadOnlyCollection<string>>(prefs.ConnectionGoals);

        var langList = (IList<string>)prefs.Languages;
        Assert.True(langList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => langList.Add("German"));
        Assert.Throws<NotSupportedException>(() => langList.Remove("English"));
        Assert.Throws<NotSupportedException>(() => langList[0] = "German");
        Assert.Throws<NotSupportedException>(() => langList.Clear());

        var goalList = (IList<string>)prefs.ConnectionGoals;
        Assert.True(goalList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => goalList.Add("Dating"));
        Assert.Throws<NotSupportedException>(() => goalList.Remove("Friendship"));
        Assert.Throws<NotSupportedException>(() => goalList[0] = "Dating");
        Assert.Throws<NotSupportedException>(() => goalList.Clear());
    }

    [Fact]
    public void LanguagesAndGoals_CannotBeDowncastToMutableArray()
    {
        var prefs = new SocialExplorePreferences(
            languages: new[] { "English" },
            connectionGoals: new[] { "Friendship" });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])prefs.Languages;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])prefs.ConnectionGoals;
        });
    }

    [Fact]
    public void ExternalCollectionMutation_DoesNotAffectConstructedPreferences()
    {
        var inputLanguages = new List<string> { "English", "Tamil" };
        var inputGoals = new List<string> { "Friendship", "Dating" };

        var prefs = new SocialExplorePreferences(
            languages: inputLanguages,
            connectionGoals: inputGoals);

        inputLanguages.Add("German");
        inputLanguages[0] = "Spanish";
        inputGoals.Add("Open to Explore");
        inputGoals.Clear();

        Assert.Equal(2, prefs.Languages.Count);
        Assert.Equal("English", prefs.Languages[0]);
        Assert.Equal("Tamil", prefs.Languages[1]);

        Assert.Equal(2, prefs.ConnectionGoals.Count);
        Assert.Equal("Friendship", prefs.ConnectionGoals[0]);
        Assert.Equal("Dating", prefs.ConnectionGoals[1]);
    }

    [Fact]
    public void StaticVocabularyCollections_AreProtectedAgainstMutation()
    {
        Assert.IsAssignableFrom<ReadOnlyCollection<string>>(SocialGenderPreferences.AllOptions);
        Assert.IsAssignableFrom<ReadOnlyCollection<string>>(SocialLanguages.All);
        Assert.IsAssignableFrom<ReadOnlyCollection<string>>(SocialConnectionGoals.All);

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])SocialGenderPreferences.AllOptions;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])SocialLanguages.All;
        });

        Assert.Throws<InvalidCastException>(() =>
        {
            _ = (string[])SocialConnectionGoals.All;
        });

        var genderList = (IList<string>)SocialGenderPreferences.AllOptions;
        Assert.Throws<NotSupportedException>(() => genderList.Add("Other"));
        Assert.Throws<NotSupportedException>(() => genderList[0] = "Other");

        var langList = (IList<string>)SocialLanguages.All;
        Assert.Throws<NotSupportedException>(() => langList.Add("Latin"));
        Assert.Throws<NotSupportedException>(() => langList[0] = "Latin");

        var goalList = (IList<string>)SocialConnectionGoals.All;
        Assert.Throws<NotSupportedException>(() => goalList.Add("Casual"));
        Assert.Throws<NotSupportedException>(() => goalList[0] = "Casual");
    }

    #endregion

    #region G. Value Equality & HashCode

    [Fact]
    public void IdenticalInstances_AreEqualAndHaveSameHashCode()
    {
        var instance1 = new SocialExplorePreferences(
            rangeFilters: new SocialExploreRangeFilters(20, 30, 5, 50),
            showMe: "Men",
            languages: new[] { "English", "Tamil" },
            connectionGoals: new[] { "Friendship", "Dating" });

        var instance2 = new SocialExplorePreferences(
            rangeFilters: new SocialExploreRangeFilters(20, 30, 5, 50),
            showMe: "Men",
            languages: new[] { "English", "Tamil" },
            connectionGoals: new[] { "Friendship", "Dating" });

        Assert.True(instance1.Equals(instance2));
        Assert.True(instance2.Equals(instance1));
        Assert.True(instance1.Equals((object)instance2));
        Assert.True(instance1 == instance2);
        Assert.False(instance1 != instance2);
        Assert.Equal(instance1.GetHashCode(), instance2.GetHashCode());
    }

    [Fact]
    public void ReferenceEquality_ReturnsTrue()
    {
        var instance = new SocialExplorePreferences();
        var sameReference = instance;

        Assert.True(instance.Equals(instance));
        Assert.True(instance.Equals((object)instance));
        Assert.True(instance == sameReference);
        Assert.False(instance != sameReference);
    }

    [Fact]
    public void NullComparison_ReturnsFalse()
    {
        var instance = new SocialExplorePreferences();

        Assert.False(instance.Equals(null));
        Assert.False(instance.Equals((object?)null));
        Assert.False(instance == null);
        Assert.False(null == instance);
        Assert.True(instance != null);
        Assert.True(null != instance);
    }

    [Fact]
    public void DifferentTypes_ReturnsFalse()
    {
        var instance = new SocialExplorePreferences();

        Assert.False(instance.Equals("some string"));
        Assert.False(instance.Equals(123));
        Assert.False(instance.Equals(new object()));
    }

    [Fact]
    public void DifferentRangeFilters_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(rangeFilters: new SocialExploreRangeFilters(18, 30));
        var instance2 = new SocialExplorePreferences(rangeFilters: new SocialExploreRangeFilters(18, 35));

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    [Fact]
    public void DifferentShowMe_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(showMe: "Men");
        var instance2 = new SocialExplorePreferences(showMe: "Women");

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    [Fact]
    public void DifferentLanguages_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(languages: new[] { "English" });
        var instance2 = new SocialExplorePreferences(languages: new[] { "Tamil" });

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    [Fact]
    public void DifferentLanguageOrder_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(languages: new[] { "English", "Tamil" });
        var instance2 = new SocialExplorePreferences(languages: new[] { "Tamil", "English" });

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    [Fact]
    public void DifferentConnectionGoals_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(connectionGoals: new[] { "Friendship" });
        var instance2 = new SocialExplorePreferences(connectionGoals: new[] { "Dating" });

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    [Fact]
    public void DifferentConnectionGoalOrder_AreNotEqual()
    {
        var instance1 = new SocialExplorePreferences(connectionGoals: new[] { "Friendship", "Dating" });
        var instance2 = new SocialExplorePreferences(connectionGoals: new[] { "Dating", "Friendship" });

        Assert.False(instance1.Equals(instance2));
        Assert.False(instance1 == instance2);
        Assert.True(instance1 != instance2);
    }

    #endregion

    #region H. Factory Methods & Formatting

    [Fact]
    public void CreateFactory_StringOverload_MatchesConstructor()
    {
        var customFilters = new SocialExploreRangeFilters(21, 39, 10, 80);
        var created = SocialExplorePreferences.Create(
            rangeFilters: customFilters,
            showMe: "Women",
            languages: new[] { "Tamil", "English" },
            connectionGoals: new[] { "Friendship" });

        var constructed = new SocialExplorePreferences(
            rangeFilters: customFilters,
            showMe: "Women",
            languages: new[] { "Tamil", "English" },
            connectionGoals: new[] { "Friendship" });

        Assert.Equal(constructed, created);
        Assert.Equal(constructed.GetHashCode(), created.GetHashCode());
    }

    [Fact]
    public void CreateFactory_EnumOverload_MatchesConstructor()
    {
        var customFilters = new SocialExploreRangeFilters(25, 45, 0, 150);
        var created = SocialExplorePreferences.Create(
            rangeFilters: customFilters,
            showMe: SocialGenderPreference.Men,
            languages: new[] { "Hindi" },
            connectionGoals: new[] { "Life Partner" });

        var constructed = new SocialExplorePreferences(
            rangeFilters: customFilters,
            showMe: SocialGenderPreference.Men,
            languages: new[] { "Hindi" },
            connectionGoals: new[] { "Life Partner" });

        Assert.Equal(constructed, created);
        Assert.Equal(constructed.GetHashCode(), created.GetHashCode());
    }

    [Fact]
    public void ToString_DefaultInstance_MatchesExpectedFormat()
    {
        var prefs = SocialExplorePreferences.Default;
        var expected = "Age: 18–35, Distance: 0–100 km, ShowMe: All, Languages: [English], Goals: []";

        Assert.Equal(expected, prefs.ToString());
    }

    [Fact]
    public void ToString_CustomInstance_MatchesExpectedFormat()
    {
        var prefs = new SocialExplorePreferences(
            rangeFilters: new SocialExploreRangeFilters(20, 30, 5, 50),
            showMe: "Men",
            languages: new[] { "Tamil", "English" },
            connectionGoals: new[] { "Dating", "Friendship" });

        var expected = "Age: 20–30, Distance: 5–50 km, ShowMe: Men, Languages: [Tamil, English], Goals: [Dating, Friendship]";

        Assert.Equal(expected, prefs.ToString());
    }

    #endregion
}
