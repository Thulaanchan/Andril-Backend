using System;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionEvidenceValidatorTests
{
    private static readonly Guid OwnerId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid AttractionId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid SessionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid OtherId = new("44444444-4444-4444-8444-444444444444");
    private static readonly DateTimeOffset StartedAtUtc = new(2026, 11, 19, 5, 0, 0, TimeSpan.Zero);
    private static readonly MapContributionLocationEvidence Observation = new(0, 0, null, null, 10, null, null);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Validate_RequiresBothTrustedRecords(bool hasAttraction, bool hasSession)
    {
        var result = MapContributionEvidenceValidator.Validate(
            hasAttraction ? CreateAttraction() : null, hasSession ? CreateSession() : null, OwnerId, Observation);

        AssertFailure(MapContributionEvidenceFailure.MissingRecords, result);
    }

    [Fact]
    public void Validate_RejectsEmptyExpectedOwner()
    {
        var result = MapContributionEvidenceValidator.Validate(CreateAttraction(), CreateSession(), Guid.Empty, Observation);

        AssertFailure(MapContributionEvidenceFailure.InconsistentRecords, result);
    }

    [Fact]
    public void Validate_RejectsWrongOwnerBeforeInspectingAttractionOrCompletion()
    {
        var unrelatedSession = MapContributionCaptureSession.Create(SessionId, OtherId, OtherId, StartedAtUtc);
        var result = Validate(CreateAttraction(), unrelatedSession, Observation);

        AssertFailure(MapContributionEvidenceFailure.OwnershipMismatch, result);
    }

    [Fact]
    public void Validate_RejectsWrongAttraction()
    {
        var result = Validate(MapAttraction.Create(OtherId, "Other attraction", 0, 0), CreateSession(), Observation);

        AssertFailure(MapContributionEvidenceFailure.AttractionMismatch, result);
    }

    [Fact]
    public void Validate_RejectsIncompleteSessionDespiteReportedCompletion()
    {
        var session = MapContributionCaptureSession.Create(SessionId, OwnerId, AttractionId, StartedAtUtc);
        var report = Observation with { CaptureStartedAt = StartedAtUtc, CaptureCompletedAt = StartedAtUtc.AddSeconds(1) };

        AssertFailure(MapContributionEvidenceFailure.IncompleteCaptureSession, Validate(CreateAttraction(), session, report));
        Assert.Null(session.CompletedAtUtc);
    }

    [Fact]
    public void Validate_RejectsInconsistentStoredTimestamps()
    {
        var session = CreateSession();
        // Simulate corrupted materialized state that the public domain API cannot construct.
        typeof(MapContributionCaptureSession).GetProperty(nameof(MapContributionCaptureSession.CompletedAtUtc))!
            .SetValue(session, StartedAtUtc.AddSeconds(-1));

        AssertFailure(MapContributionEvidenceFailure.InconsistentRecords, Validate(CreateAttraction(), session, Observation));
    }

    [Fact]
    public void Validate_PreservesOnlyDeviceObservationsAndBuildsCanonicalSnapshotWithoutMutation()
    {
        var attraction = MapAttraction.Create(AttractionId, "Canonical attraction", 7.1, 80.2);
        var session = CreateSession();
        var report = new MapContributionLocationEvidence(7.1001, 80.2001, -35, 95, 12.5,
            StartedAtUtc.AddDays(-1), StartedAtUtc.AddDays(1));
        var expected = new MapContributionLocationEvidence(7.1001, 80.2001, 7.1, 80.2, 12.5,
            StartedAtUtc, StartedAtUtc.AddSeconds(60));

        var result = Validate(attraction, session, report);
        var repeated = Validate(attraction, session, report);

        Assert.True(result.PassesChecks);
        Assert.Null(result.Failure);
        Assert.Null(result.LocationFailure);
        Assert.Equal(expected, result.Evidence);
        Assert.Equal(result.Evidence, repeated.Evidence);
        Assert.NotSame(report, result.Evidence);
        Assert.Equal(7.1, attraction.Latitude);
        Assert.Equal(80.2, attraction.Longitude);
        Assert.Equal(StartedAtUtc, session.StartedAtUtc);
        Assert.Equal(StartedAtUtc.AddSeconds(60), session.CompletedAtUtc);
        Assert.Equal(-35d, report.AttractionLatitude);
        Assert.Equal(StartedAtUtc.AddDays(1), report.CaptureCompletedAt);
    }

    [Fact]
    public void Validate_ReportedAttractionCannotHideExcessDistance()
    {
        var report = Observation with { UserLongitude = 0.001, AttractionLatitude = 0, AttractionLongitude = 0.001 };

        AssertFailure(MapContributionEvidenceFailure.LocationPolicyFailed,
            Validate(CreateAttraction(), CreateSession(), report), MapContributionLocationFailure.OutsideAttractionDistance);
    }

    [Fact]
    public void Validate_ReportedTimesCannotShortenStoredCaptureDuration()
    {
        var report = Observation with { CaptureStartedAt = StartedAtUtc, CaptureCompletedAt = StartedAtUtc.AddSeconds(1) };

        AssertFailure(MapContributionEvidenceFailure.LocationPolicyFailed,
            Validate(CreateAttraction(), CreateSession(121), report), MapContributionLocationFailure.CaptureDurationExceeded);
    }

    [Fact]
    public void Validate_InclusiveDistanceAccuracyAndDurationLimitsPass()
    {
        // The existing location policy fixture uses this longitude offset for 100 m at the equator.
        var report = Observation with { UserLongitude = 0.0008993216059187306, HorizontalAccuracyMetres = 30 };

        Assert.True(Validate(CreateAttraction(), CreateSession(120), report).PassesChecks);
    }

    [Theory]
    [InlineData(91, 10, MapContributionLocationFailure.InvalidUserCoordinates)]
    [InlineData(double.NaN, 10, MapContributionLocationFailure.InvalidUserCoordinates)]
    [InlineData(0, 30.01, MapContributionLocationFailure.InvalidGpsAccuracy)]
    [InlineData(0, double.PositiveInfinity, MapContributionLocationFailure.InvalidGpsAccuracy)]
    public void Validate_PropagatesPolicyReasonWithoutPrivateEvidence(
        double latitude, double accuracy, MapContributionLocationFailure reason)
    {
        var report = Observation with { UserLatitude = latitude, HorizontalAccuracyMetres = accuracy };

        AssertFailure(MapContributionEvidenceFailure.LocationPolicyFailed,
            Validate(CreateAttraction(), CreateSession(), report), reason);
    }

    [Fact]
    public void Validate_RejectsMissingDeviceObservations()
    {
        AssertFailure(MapContributionEvidenceFailure.LocationPolicyFailed,
            Validate(CreateAttraction(), CreateSession(), null), MapContributionLocationFailure.MissingEvidence);
    }

    private static MapAttraction CreateAttraction() => MapAttraction.Create(AttractionId, "Canonical attraction", 0, 0);

    private static MapContributionCaptureSession CreateSession(int durationSeconds = 60)
    {
        var session = MapContributionCaptureSession.Create(SessionId, OwnerId, AttractionId, StartedAtUtc);
        session.Complete(StartedAtUtc.AddSeconds(durationSeconds));
        return session;
    }

    private static MapContributionEvidenceResult Validate(
        MapAttraction attraction, MapContributionCaptureSession session, MapContributionLocationEvidence? report) =>
        MapContributionEvidenceValidator.Validate(attraction, session, OwnerId, report);

    private static void AssertFailure(MapContributionEvidenceFailure failure, MapContributionEvidenceResult result,
        MapContributionLocationFailure? locationFailure = null)
    {
        Assert.False(result.PassesChecks);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(locationFailure, result.LocationFailure);
        Assert.Null(result.Evidence);
    }
}
