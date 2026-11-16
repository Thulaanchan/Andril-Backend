using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionLocationPolicyTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly MapContributionLocationEvidence ValidEvidence =
        new(0, 0, 0, 0, 15, StartedAt, StartedAt.AddSeconds(60));

    [Fact]
    public void MissingEvidence_FailsWithoutDistance()
    {
        var result = MapContributionLocationPolicy.Evaluate(null);

        Assert.False(result.PassesChecks);
        Assert.Equal(MapContributionLocationFailure.MissingEvidence, result.Failure);
        Assert.Null(result.DistanceMetres);
    }

    [Theory]
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    [InlineData(0, 0)]
    [InlineData(-35, -70)]
    public void IdenticalValidCoordinates_PassWithZeroDistance(double latitude, double longitude)
    {
        var evidence = ValidEvidence with
        {
            UserLatitude = latitude,
            UserLongitude = longitude,
            AttractionLatitude = latitude,
            AttractionLongitude = longitude
        };

        AssertOutcome(evidence, null, 0, 0.000001);
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(0d, null)]
    [InlineData(-90.0001, 0d)]
    [InlineData(90.0001, 0d)]
    [InlineData(0d, -180.0001)]
    [InlineData(0d, 180.0001)]
    [InlineData(double.NaN, 0d)]
    [InlineData(0d, double.NaN)]
    [InlineData(double.PositiveInfinity, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0d)]
    [InlineData(0d, double.NegativeInfinity)]
    public void MalformedCoordinates_FailForEitherLocation(double? latitude, double? longitude)
    {
        var badUser = ValidEvidence with { UserLatitude = latitude, UserLongitude = longitude };
        var badAttraction = ValidEvidence with { AttractionLatitude = latitude, AttractionLongitude = longitude };

        AssertOutcome(badUser, MapContributionLocationFailure.InvalidUserCoordinates);
        AssertOutcome(badAttraction, MapContributionLocationFailure.InvalidAttractionCoordinates);
    }

    [Theory]
    // At the equator these angular separations represent 99.99 m, 100 m and 100.01 m.
    [InlineData(0.0008992316737581387, 99.99, null)]
    [InlineData(0.0008993216059187306, 100, null)]
    [InlineData(0.0008994115380793224, 100.01, MapContributionLocationFailure.OutsideAttractionDistance)]
    public void AttractionDistance_UsesInclusiveHundredMetreLimit(
        double attractionLongitude, double expectedDistance, MapContributionLocationFailure? failure)
    {
        var evidence = ValidEvidence with { AttractionLongitude = attractionLongitude };

        AssertOutcome(evidence, failure, expectedDistance - 0.001, expectedDistance + 0.001);
    }

    [Theory]
    [InlineData(0, 0, 0, 0.0001, 11.11, 11.13)]
    [InlineData(0, 179.9998, 0, -179.9998, 44.47, 44.49)]
    [InlineData(0, -180, 0, 180, 0, 0.000001)]
    [InlineData(89.9999, 0, 89.9999, 180, 22.23, 22.25)]
    [InlineData(-45, -70, -45, -70.0005, 39.30, 39.33)]
    public void ValidCoordinatePairs_ProduceExpectedGreatCircleDistance(
        double userLatitude, double userLongitude, double attractionLatitude, double attractionLongitude,
        double minimumDistance, double maximumDistance)
    {
        var evidence = ValidEvidence with
        {
            UserLatitude = userLatitude,
            UserLongitude = userLongitude,
            AttractionLatitude = attractionLatitude,
            AttractionLongitude = attractionLongitude
        };

        AssertOutcome(evidence, null, minimumDistance, maximumDistance);
    }

    [Fact]
    public void AntipodalPoints_ProduceFiniteDistanceAndFailProximity()
    {
        var evidence = ValidEvidence with { AttractionLongitude = 180 };

        AssertOutcome(evidence, MapContributionLocationFailure.OutsideAttractionDistance, 20_015_086, 20_015_088);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(29.9)]
    [InlineData(30)]
    public void ReportedAccuracyWithinLimit_Passes(double accuracy)
    {
        AssertOutcome(ValidEvidence with { HorizontalAccuracyMetres = accuracy }, null, 0, 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-0.1)]
    [InlineData(30.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidReportedAccuracy_FailsAndRetainsDistance(double? accuracy)
    {
        AssertOutcome(ValidEvidence with { HorizontalAccuracyMetres = accuracy },
            MapContributionLocationFailure.InvalidGpsAccuracy, 0, 0);
    }

    [Theory]
    [InlineData(0d, null)]
    [InlineData(60, null)]
    [InlineData(119, null)]
    [InlineData(120, null)]
    [InlineData(121, MapContributionLocationFailure.CaptureDurationExceeded)]
    [InlineData(-1, MapContributionLocationFailure.InvalidCaptureTiming)]
    public void CaptureDuration_EnforcesInclusiveLimitAndTimestampOrder(
        int seconds, MapContributionLocationFailure? failure)
    {
        var evidence = ValidEvidence with { CaptureCompletedAt = StartedAt.AddSeconds(seconds) };

        AssertOutcome(evidence, failure, 0, 0);
    }

    [Theory]
    [InlineData(0d, null)]
    [InlineData(120, null)]
    [InlineData(121, MapContributionLocationFailure.CaptureDurationExceeded)]
    [InlineData(-1, MapContributionLocationFailure.InvalidCaptureTiming)]
    public void DifferentTimestampOffsets_AreComparedByAbsoluteInstant(
        int seconds, MapContributionLocationFailure? failure)
    {
        var evidence = ValidEvidence with
        {
            CaptureStartedAt = StartedAt.ToOffset(TimeSpan.FromHours(5.5)),
            CaptureCompletedAt = StartedAt.AddSeconds(seconds).ToOffset(TimeSpan.FromHours(-4))
        };

        AssertOutcome(evidence, failure, 0, 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MissingCaptureTimestamps_Fail(bool hasStart, bool hasCompletion)
    {
        var evidence = ValidEvidence with
        {
            CaptureStartedAt = hasStart ? StartedAt : null,
            CaptureCompletedAt = hasCompletion ? StartedAt.AddSeconds(60) : null
        };

        AssertOutcome(evidence, MapContributionLocationFailure.InvalidCaptureTiming, 0, 0);
    }

    [Fact]
    public void MultipleFailures_ReturnFirstFailureInPolicyEvaluationOrder()
    {
        var evidence = ValidEvidence with
        {
            UserLatitude = null,
            AttractionLatitude = 91,
            AttractionLongitude = 1,
            HorizontalAccuracyMetres = 31,
            CaptureCompletedAt = null
        };

        AssertOutcome(evidence, MapContributionLocationFailure.InvalidUserCoordinates);

        evidence = evidence with { UserLatitude = 0 };
        AssertOutcome(evidence, MapContributionLocationFailure.InvalidAttractionCoordinates);

        evidence = evidence with { AttractionLatitude = 0 };
        AssertOutcome(evidence, MapContributionLocationFailure.InvalidGpsAccuracy, 111_194, 111_196);

        evidence = evidence with { HorizontalAccuracyMetres = 30 };
        AssertOutcome(evidence, MapContributionLocationFailure.OutsideAttractionDistance, 111_194, 111_196);

        evidence = evidence with { AttractionLongitude = 0 };
        AssertOutcome(evidence, MapContributionLocationFailure.InvalidCaptureTiming, 0, 0);
    }

    private static void AssertOutcome(
        MapContributionLocationEvidence evidence,
        MapContributionLocationFailure? failure,
        double? minimumDistance = null,
        double maximumDistance = 0)
    {
        var result = MapContributionLocationPolicy.Evaluate(evidence);

        Assert.Equal(failure is null, result.PassesChecks);
        Assert.Equal(failure, result.Failure);
        if (minimumDistance is double minimum)
            Assert.InRange(Assert.IsType<double>(result.DistanceMetres), minimum, maximumDistance);
        else
            Assert.Null(result.DistanceMetres);
    }
}
