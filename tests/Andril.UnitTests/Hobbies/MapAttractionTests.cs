using System;
using System.Reflection;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapAttractionTests
{
    private static readonly Guid AttractionId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void Create_ValidInputs_PreservesOriginalValues()
    {
        const string name = "  Synthetic attraction  ";
        const double latitude = 7.123456789;
        const double longitude = -80.987654321;

        var attraction = MapAttraction.Create(AttractionId, name, latitude, longitude);

        Assert.Equal(AttractionId, attraction.AttractionId);
        Assert.Equal(name, attraction.Name);
        Assert.Equal(latitude, attraction.Latitude);
        Assert.Equal(longitude, attraction.Longitude);
    }

    [Fact]
    public void Create_EmptyAttractionId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            MapAttraction.Create(Guid.Empty, "Synthetic attraction", 0, 0));

        Assert.Equal("attractionId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u2003")]
    public void Create_BlankName_ThrowsArgumentException(string? name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            MapAttraction.Create(AttractionId, name!, 0, 0));

        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData("Synthetic attraction")]
    [InlineData("  Synthetic attraction  ")]
    [InlineData("කෘත්‍රිම ස්ථානය")]
    [InlineData("செயற்கை இடம்")]
    [InlineData("山 🗺️")]
    public void Create_ValidName_PreservesName(string name)
    {
        var attraction = MapAttraction.Create(AttractionId, name, 0, 0);

        Assert.Equal(name, attraction.Name);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 80)]
    [InlineData(7, 0)]
    [InlineData(7.5, 80.5)]
    [InlineData(-7.5, -80.5)]
    [InlineData(-7.5, 80.5)]
    [InlineData(7.5, -80.5)]
    [InlineData(-90, 0)]
    [InlineData(90, 0)]
    [InlineData(0, -180)]
    [InlineData(0, 180)]
    [InlineData(-90, -180)]
    [InlineData(-90, 180)]
    [InlineData(90, -180)]
    [InlineData(90, 180)]
    public void Create_ValidCoordinates_PreservesCoordinates(double latitude, double longitude)
    {
        var attraction = MapAttraction.Create(AttractionId, "Synthetic attraction", latitude, longitude);

        Assert.Equal(latitude, attraction.Latitude);
        Assert.Equal(longitude, attraction.Longitude);
    }

    [Theory]
    [InlineData(-90.000001)]
    [InlineData(90.000001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_InvalidLatitude_ThrowsArgumentOutOfRangeException(double latitude)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapAttraction.Create(AttractionId, "Synthetic attraction", latitude, 0));

        Assert.Equal("latitude", exception.ParamName);
    }

    [Theory]
    [InlineData(-180.000001)]
    [InlineData(180.000001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Create_InvalidLongitude_ThrowsArgumentOutOfRangeException(double longitude)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MapAttraction.Create(AttractionId, "Synthetic attraction", 0, longitude));

        Assert.Equal("longitude", exception.ParamName);
    }

    [Fact]
    public void DomainState_HasNoPublicMutationOrConstruction()
    {
        var type = typeof(MapAttraction);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            Assert.False(property.SetMethod?.IsPublic == true, property.Name));
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
    }
}
