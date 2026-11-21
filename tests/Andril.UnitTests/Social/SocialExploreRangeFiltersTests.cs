using System;
using System.Reflection;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialExploreRangeFiltersTests
{
    #region A. Defaults

    [Fact]
    public void DefaultProperty_ExposesExpectedDefaultValues()
    {
        var filters = SocialExploreRangeFilters.Default;

        Assert.Equal(18, filters.MinAge);
        Assert.Equal(35, filters.MaxAge);
        Assert.Equal(0, filters.MinDistanceKm);
        Assert.Equal(100, filters.MaxDistanceKm);
    }

    [Fact]
    public void ParameterlessConstructor_MatchesDefaultInstance()
    {
        var constructed = new SocialExploreRangeFilters();
        var defaultInstance = SocialExploreRangeFilters.Default;

        Assert.Equal(defaultInstance.MinAge, constructed.MinAge);
        Assert.Equal(defaultInstance.MaxAge, constructed.MaxAge);
        Assert.Equal(defaultInstance.MinDistanceKm, constructed.MinDistanceKm);
        Assert.Equal(defaultInstance.MaxDistanceKm, constructed.MaxDistanceKm);
        Assert.Equal(defaultInstance, constructed);
    }

    [Fact]
    public void ExposedConstants_MatchApprovedLimitsAndDefaults()
    {
        Assert.Equal(18, SocialExploreRangeFilters.AbsoluteMinAge);
        Assert.Equal(99, SocialExploreRangeFilters.AbsoluteMaxAge);
        Assert.Equal(18, SocialExploreRangeFilters.DefaultMinAge);
        Assert.Equal(35, SocialExploreRangeFilters.DefaultMaxAge);

        Assert.Equal(0, SocialExploreRangeFilters.AbsoluteMinDistanceKm);
        Assert.Equal(999, SocialExploreRangeFilters.AbsoluteMaxDistanceKm);
        Assert.Equal(0, SocialExploreRangeFilters.DefaultMinDistanceKm);
        Assert.Equal(100, SocialExploreRangeFilters.DefaultMaxDistanceKm);
    }

    #endregion

    #region B. Valid Age Ranges

    [Theory]
    [InlineData(18, 99)]
    [InlineData(18, 18)]
    [InlineData(99, 99)]
    [InlineData(25, 40)]
    [InlineData(18, 35)]
    [InlineData(50, 60)]
    public void ValidAgeRanges_AreAcceptedAndPreserved(int minAge, int maxAge)
    {
        var filters = new SocialExploreRangeFilters(minAge: minAge, maxAge: maxAge);

        Assert.Equal(minAge, filters.MinAge);
        Assert.Equal(maxAge, filters.MaxAge);
        Assert.Equal(SocialExploreRangeFilters.DefaultMinDistanceKm, filters.MinDistanceKm);
        Assert.Equal(SocialExploreRangeFilters.DefaultMaxDistanceKm, filters.MaxDistanceKm);
    }

    #endregion

    #region C. Invalid Age Ranges

    [Theory]
    [InlineData(17)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-18)]
    [InlineData(int.MinValue)]
    public void MinAge_BelowAbsoluteMinimum_ThrowsArgumentOutOfRangeException(int minAge)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(minAge: minAge, maxAge: 35));

        Assert.Equal("minAge", ex.ParamName);
        Assert.Equal(minAge, ex.ActualValue);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(999)]
    [InlineData(int.MaxValue)]
    public void MaxAge_AboveAbsoluteMaximum_ThrowsArgumentOutOfRangeException(int maxAge)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(minAge: 18, maxAge: maxAge));

        Assert.Equal("maxAge", ex.ParamName);
        Assert.Equal(maxAge, ex.ActualValue);
    }

    [Theory]
    [InlineData(36, 35)]
    [InlineData(99, 18)]
    [InlineData(50, 49)]
    public void MinAge_GreaterThanMaxAge_ThrowsArgumentException(int minAge, int maxAge)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExploreRangeFilters(minAge: minAge, maxAge: maxAge));

        Assert.Equal("minAge", ex.ParamName);
    }

    #endregion

    #region D. Valid Distance Ranges

    [Theory]
    [InlineData(0, 999)]
    [InlineData(0, 0)]
    [InlineData(999, 999)]
    [InlineData(20, 80)]
    [InlineData(0, 100)]
    [InlineData(50, 50)]
    public void ValidDistanceRanges_AreAcceptedAndPreserved(int minDistanceKm, int maxDistanceKm)
    {
        var filters = new SocialExploreRangeFilters(
            minDistanceKm: minDistanceKm,
            maxDistanceKm: maxDistanceKm);

        Assert.Equal(SocialExploreRangeFilters.DefaultMinAge, filters.MinAge);
        Assert.Equal(SocialExploreRangeFilters.DefaultMaxAge, filters.MaxAge);
        Assert.Equal(minDistanceKm, filters.MinDistanceKm);
        Assert.Equal(maxDistanceKm, filters.MaxDistanceKm);
    }

    #endregion

    #region E. Invalid Distance Ranges

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void MinDistance_BelowZero_ThrowsArgumentOutOfRangeException(int minDistanceKm)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(minDistanceKm: minDistanceKm, maxDistanceKm: 100));

        Assert.Equal("minDistanceKm", ex.ParamName);
        Assert.Equal(minDistanceKm, ex.ActualValue);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1500)]
    [InlineData(int.MaxValue)]
    public void MaxDistance_AboveAbsoluteMaximum_ThrowsArgumentOutOfRangeException(int maxDistanceKm)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(minDistanceKm: 0, maxDistanceKm: maxDistanceKm));

        Assert.Equal("maxDistanceKm", ex.ParamName);
        Assert.Equal(maxDistanceKm, ex.ActualValue);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(500, 200)]
    [InlineData(999, 0)]
    public void MinDistance_GreaterThanMaxDistance_ThrowsArgumentException(
        int minDistanceKm,
        int maxDistanceKm)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialExploreRangeFilters(
                minDistanceKm: minDistanceKm,
                maxDistanceKm: maxDistanceKm));

        Assert.Equal("minDistanceKm", ex.ParamName);
    }

    #endregion

    #region F. Combined Range Validation

    [Fact]
    public void ValidCombinedRanges_WorkTogetherCorrectly()
    {
        var filters = new SocialExploreRangeFilters(
            minAge: 21,
            maxAge: 30,
            minDistanceKm: 10,
            maxDistanceKm: 50);

        Assert.Equal(21, filters.MinAge);
        Assert.Equal(30, filters.MaxAge);
        Assert.Equal(10, filters.MinDistanceKm);
        Assert.Equal(50, filters.MaxDistanceKm);
    }

    [Fact]
    public void InvalidAge_CannotBeAccepted_EvenIfDistanceIsValid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(
                minAge: 17,
                maxAge: 30,
                minDistanceKm: 10,
                maxDistanceKm: 50));
    }

    [Fact]
    public void InvalidDistance_CannotBeAccepted_EvenIfAgeIsValid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialExploreRangeFilters(
                minAge: 21,
                maxAge: 30,
                minDistanceKm: -5,
                maxDistanceKm: 50));
    }

    #endregion

    #region G. Immutability

    [Fact]
    public void Properties_DoNotExposePublicSetters()
    {
        var properties = typeof(SocialExploreRangeFilters).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            Assert.False(
                property.CanWrite,
                $"Property '{property.Name}' should be read-only but has a setter.");
        }
    }

    [Fact]
    public void DefaultInstance_RemainsUnchanged_AfterCustomInstancesCreated()
    {
        var defaultBefore = SocialExploreRangeFilters.Default;

        _ = new SocialExploreRangeFilters(25, 45, 10, 250);
        _ = SocialExploreRangeFilters.Create(18, 99, 0, 999);

        var defaultAfter = SocialExploreRangeFilters.Default;

        Assert.Equal(18, defaultAfter.MinAge);
        Assert.Equal(35, defaultAfter.MaxAge);
        Assert.Equal(0, defaultAfter.MinDistanceKm);
        Assert.Equal(100, defaultAfter.MaxDistanceKm);
        Assert.Same(defaultBefore, defaultAfter);
    }

    [Fact]
    public void RepeatedConstruction_YieldsPredictableValues()
    {
        var first = new SocialExploreRangeFilters(20, 28, 5, 45);
        var second = new SocialExploreRangeFilters(20, 28, 5, 45);

        Assert.Equal(first.MinAge, second.MinAge);
        Assert.Equal(first.MaxAge, second.MaxAge);
        Assert.Equal(first.MinDistanceKm, second.MinDistanceKm);
        Assert.Equal(first.MaxDistanceKm, second.MaxDistanceKm);
        Assert.Equal(first, second);
    }

    #endregion

    #region H. Value Equality

    [Fact]
    public void TwoInstances_WithIdenticalValues_AreEqual()
    {
        var a = new SocialExploreRangeFilters(20, 30, 10, 50);
        var b = new SocialExploreRangeFilters(20, 30, 10, 50);

        Assert.True(a.Equals(b));
        Assert.True(b.Equals(a));
        Assert.True(a.Equals((object)b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Instances_WithDifferentValues_AreNotEqual()
    {
        var baseInstance = new SocialExploreRangeFilters(20, 30, 10, 50);

        var diffMinAge = new SocialExploreRangeFilters(21, 30, 10, 50);
        var diffMaxAge = new SocialExploreRangeFilters(20, 31, 10, 50);
        var diffMinDist = new SocialExploreRangeFilters(20, 30, 15, 50);
        var diffMaxDist = new SocialExploreRangeFilters(20, 30, 10, 60);

        Assert.False(baseInstance.Equals(diffMinAge));
        Assert.False(baseInstance == diffMinAge);
        Assert.True(baseInstance != diffMinAge);

        Assert.False(baseInstance.Equals(diffMaxAge));
        Assert.False(baseInstance == diffMaxAge);
        Assert.True(baseInstance != diffMaxAge);

        Assert.False(baseInstance.Equals(diffMinDist));
        Assert.False(baseInstance == diffMinDist);
        Assert.True(baseInstance != diffMinDist);

        Assert.False(baseInstance.Equals(diffMaxDist));
        Assert.False(baseInstance == diffMaxDist);
        Assert.True(baseInstance != diffMaxDist);
    }

    [Fact]
    public void Equality_WithNullOrOtherTypes_ReturnsFalse()
    {
        var filters = new SocialExploreRangeFilters();

        Assert.False(filters.Equals((SocialExploreRangeFilters?)null));
        Assert.False(filters.Equals((object?)null));
        Assert.False(filters.Equals("not-a-range-filter"));
        Assert.False(filters == null);
        Assert.False(null == filters);
        Assert.True(filters != null);
        Assert.True(null != filters);
    }

    [Fact]
    public void ReferenceEquals_ReturnsTrue()
    {
        var filters = new SocialExploreRangeFilters();
        var sameReference = filters;

        Assert.True(filters.Equals(sameReference));
        Assert.True(filters == sameReference);
        Assert.False(filters != sameReference);
    }

    #endregion

    #region I. Factory and Formatting

    [Fact]
    public void CreateFactory_ReturnsInstance_WithSpecifiedValues()
    {
        var filters = SocialExploreRangeFilters.Create(22, 38, 15, 75);

        Assert.NotNull(filters);
        Assert.Equal(22, filters.MinAge);
        Assert.Equal(38, filters.MaxAge);
        Assert.Equal(15, filters.MinDistanceKm);
        Assert.Equal(75, filters.MaxDistanceKm);
    }

    [Fact]
    public void CreateFactory_RejectsInvalidValues_WithSameRules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialExploreRangeFilters.Create(17, 30, 0, 100));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialExploreRangeFilters.Create(18, 100, 0, 100));

        Assert.Throws<ArgumentException>(() =>
            SocialExploreRangeFilters.Create(35, 20, 0, 100));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialExploreRangeFilters.Create(18, 30, -1, 100));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialExploreRangeFilters.Create(18, 30, 0, 1000));

        Assert.Throws<ArgumentException>(() =>
            SocialExploreRangeFilters.Create(18, 30, 100, 50));
    }

    [Fact]
    public void ToString_AccuratelyRepresentsRanges()
    {
        var defaultFilters = SocialExploreRangeFilters.Default;
        Assert.Equal("Age: 18–35, Distance: 0–100 km", defaultFilters.ToString());

        var customFilters = new SocialExploreRangeFilters(21, 30, 5, 50);
        Assert.Equal("Age: 21–30, Distance: 5–50 km", customFilters.ToString());
    }

    #endregion
}
