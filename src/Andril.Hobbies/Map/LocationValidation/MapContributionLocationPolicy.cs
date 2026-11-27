namespace Andril.Hobbies.Map.LocationValidation;

public sealed record MapContributionLocationEvidence(
    double? UserLatitude,
    double? UserLongitude,
    double? AttractionLatitude,
    double? AttractionLongitude,
    double? HorizontalAccuracyMetres,
    DateTimeOffset? CaptureStartedAt,
    DateTimeOffset? CaptureCompletedAt);

public enum MapContributionLocationFailure
{
    MissingEvidence = 1,
    InvalidUserCoordinates,
    InvalidAttractionCoordinates,
    InvalidGpsAccuracy,
    OutsideAttractionDistance,
    InvalidCaptureTiming,
    CaptureDurationExceeded
}

public sealed record MapContributionLocationResult(
    MapContributionLocationFailure? Failure,
    double? DistanceMetres)
{
    public bool PassesChecks => Failure is null;
}

public static class MapContributionLocationPolicy
{
    private const double MaximumDistanceMetres = 100;
    private const double MaximumAccuracyMetres = 30;
    private const double MaximumLatitudeDegrees = 90;
    private const double MaximumLongitudeDegrees = 180;
    private const double DegreesPerFullTurn = 360;
    private const double DegreesToRadians = Math.PI / MaximumLongitudeDegrees;

    // Approximate mean spherical Earth radius used for the great-circle distance.
    private const double EarthRadiusMetres = 6_371_000;

    internal static readonly TimeSpan MaximumCaptureDuration = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Evaluates coordinate, reported accuracy and elapsed time checks.
    /// For contribution authorization, load canonical attraction coordinates and
    /// capture timing from trusted backend sources. These checks establish
    /// neither GPS authenticity nor camera freshness.
    /// </summary>
    public static MapContributionLocationResult Evaluate(MapContributionLocationEvidence? evidence)
    {
        if (evidence is null)
            return new(MapContributionLocationFailure.MissingEvidence, null);

        if (evidence.UserLatitude is not double userLatitude ||
            !IsCoordinateInRange(userLatitude, MaximumLatitudeDegrees) ||
            evidence.UserLongitude is not double userLongitude ||
            !IsCoordinateInRange(userLongitude, MaximumLongitudeDegrees))
        {
            return new(MapContributionLocationFailure.InvalidUserCoordinates, null);
        }

        if (evidence.AttractionLatitude is not double attractionLatitude ||
            !IsCoordinateInRange(attractionLatitude, MaximumLatitudeDegrees) ||
            evidence.AttractionLongitude is not double attractionLongitude ||
            !IsCoordinateInRange(attractionLongitude, MaximumLongitudeDegrees))
        {
            return new(MapContributionLocationFailure.InvalidAttractionCoordinates, null);
        }

        var distanceMetres = CalculateDistanceMetres(
            userLatitude, userLongitude, attractionLatitude, attractionLongitude);

        if (evidence.HorizontalAccuracyMetres is not double accuracy ||
            !double.IsFinite(accuracy) || accuracy < 0 || accuracy > MaximumAccuracyMetres)
        {
            return new(MapContributionLocationFailure.InvalidGpsAccuracy, distanceMetres);
        }

        if (distanceMetres > MaximumDistanceMetres)
            return new(MapContributionLocationFailure.OutsideAttractionDistance, distanceMetres);

        if (evidence.CaptureStartedAt is not DateTimeOffset startedAt ||
            evidence.CaptureCompletedAt is not DateTimeOffset completedAt ||
            completedAt < startedAt)
        {
            return new(MapContributionLocationFailure.InvalidCaptureTiming, distanceMetres);
        }

        if (completedAt - startedAt > MaximumCaptureDuration)
            return new(MapContributionLocationFailure.CaptureDurationExceeded, distanceMetres);

        return new(null, distanceMetres);
    }

    private static bool IsCoordinateInRange(double degrees, double maximumDegrees) =>
        double.IsFinite(degrees) && Math.Abs(degrees) <= maximumDegrees;

    private static double CalculateDistanceMetres(
        double userLatitude,
        double userLongitude,
        double attractionLatitude,
        double attractionLongitude)
    {
        var latitudeDifference = (attractionLatitude - userLatitude) * DegreesToRadians;
        var longitudeDifference = Math.IEEERemainder(
            attractionLongitude - userLongitude, DegreesPerFullTurn) * DegreesToRadians;

        var latitudeSine = Math.Sin(latitudeDifference / 2);
        var longitudeSine = Math.Sin(longitudeDifference / 2);
        var haversine = latitudeSine * latitudeSine
            + Math.Cos(userLatitude * DegreesToRadians) * Math.Cos(attractionLatitude * DegreesToRadians)
            * longitudeSine * longitudeSine;

        // Rounding near antipodal points can put the intermediate value outside [0, 1].
        haversine = Math.Clamp(haversine, 0, 1);
        var centralAngle = 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
        return EarthRadiusMetres * centralAngle;
    }
}
