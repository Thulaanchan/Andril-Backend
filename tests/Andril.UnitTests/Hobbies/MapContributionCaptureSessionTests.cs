
using System;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionCaptureSessionTests
{
    private static readonly Guid ValidSessionId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid ValidProfileId =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static readonly Guid ValidAttractionId =
        Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static readonly DateTimeOffset StartUtc =
        new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static MapContributionCaptureSession CreateSession(
        DateTimeOffset? startedAt = null)
    {
        return MapContributionCaptureSession.Create(
            ValidSessionId,
            ValidProfileId,
            ValidAttractionId,
            startedAt ?? StartUtc);
    }

    private static void AssertRejected(Action action)
    {
        // The report confirms rejection, but does not
        // identify every exception subtype.
        // Only expected domain/argument failures count.
        var exception = Record.Exception(action);

        Assert.NotNull(exception);

        Assert.True(
            exception is ArgumentException
                or InvalidOperationException,
            $"Unexpected exception type: " +
            $"{exception?.GetType().FullName}");
    }

    [Fact]
    public void Create_ValidInputs_PreservesIdentity()
    {
        var session = CreateSession();

        Assert.Equal(ValidSessionId, session.SessionId);
        Assert.Equal(
            ValidProfileId,
            session.OwnerHobbiesModuleProfileId);
        Assert.Equal(
            ValidAttractionId,
            session.AttractionId);
        Assert.Equal(StartUtc, session.StartedAtUtc);
        Assert.Null(session.CompletedAtUtc);
    }

    [Fact]
    public void Create_EmptySessionId_IsRejected()
    {
        AssertRejected(() =>
            MapContributionCaptureSession.Create(
                Guid.Empty,
                ValidProfileId,
                ValidAttractionId,
                StartUtc));
    }

    [Fact]
    public void Create_EmptyProfileId_IsRejected()
    {
        AssertRejected(() =>
            MapContributionCaptureSession.Create(
                ValidSessionId,
                Guid.Empty,
                ValidAttractionId,
                StartUtc));
    }

    [Fact]
    public void Create_EmptyAttractionId_IsRejected()
    {
        AssertRejected(() =>
            MapContributionCaptureSession.Create(
                ValidSessionId,
                ValidProfileId,
                Guid.Empty,
                StartUtc));
    }

    [Fact]
    public void Create_NonUtcStart_NormalizesToUtc()
    {
        var localTime = new DateTimeOffset(
            2026, 11, 16, 15, 30, 0,
            TimeSpan.FromMinutes(330));

        var session = CreateSession(localTime);

        Assert.Equal(StartUtc, session.StartedAtUtc);
        Assert.Equal(
            TimeSpan.Zero,
            session.StartedAtUtc.Offset);
    }

    [Fact]
    public void Create_UtcStart_RemainsUnchanged()
    {
        var session = CreateSession(StartUtc);

        Assert.Equal(StartUtc, session.StartedAtUtc);
        Assert.Equal(
            TimeSpan.Zero,
            session.StartedAtUtc.Offset);
    }

    [Fact]
    public void Complete_ValidLaterTime_Succeeds()
    {
        var session = CreateSession();
        var completedAt = StartUtc.AddSeconds(60);

        session.Complete(completedAt);

        Assert.Equal(
            completedAt,
            session.CompletedAtUtc);
    }

    [Fact]
    public void Complete_AtStartInstant_Succeeds()
    {
        var session = CreateSession();

        session.Complete(StartUtc);

        Assert.Equal(
            StartUtc,
            session.CompletedAtUtc);
    }

    [Fact]
    public void Complete_BeforeStart_IsRejected()
    {
        var session = CreateSession();

        AssertRejected(() =>
            session.Complete(
                StartUtc.AddTicks(-1)));

        Assert.Null(session.CompletedAtUtc);
    }

    [Fact]
    public void Complete_EarlierAbsoluteInstant_IsRejected()
    {
        var start = new DateTimeOffset(
            2026, 11, 16, 10, 0, 0,
            TimeSpan.FromHours(-4));

        // Start = 14:00 UTC.
        var session = CreateSession(start);

        var completion = new DateTimeOffset(
            2026, 11, 16, 15, 0, 0,
            TimeSpan.FromHours(2));

        // Completion = 13:00 UTC.
        // The displayed clock is later,
        // but the absolute instant is earlier.
        AssertRejected(() =>
            session.Complete(completion));

        Assert.Null(session.CompletedAtUtc);
    }

    [Fact]
    public void Complete_EquivalentInstantOffsets_Succeeds()
    {
        var session = CreateSession();

        var sameInstant = new DateTimeOffset(
            2026, 11, 16, 15, 30, 0,
            TimeSpan.FromMinutes(330));

        // Both timestamps represent 10:00 UTC.
        session.Complete(sameInstant);

        Assert.Equal(
            StartUtc,
            session.CompletedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            session.CompletedAtUtc!.Value.Offset);
    }

    [Fact]
    public void Complete_NonUtcTimestamp_NormalizesToUtc()
    {
        var session = CreateSession();

        var localCompletion = new DateTimeOffset(
            2026, 11, 16, 15, 31, 0,
            TimeSpan.FromMinutes(330));

        session.Complete(localCompletion);

        Assert.Equal(
            StartUtc.AddMinutes(1),
            session.CompletedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            session.CompletedAtUtc!.Value.Offset);
    }

    [Fact]
    public void Complete_RepeatedCall_IsRejected()
    {
        var session = CreateSession();
        var firstCompletion = StartUtc.AddSeconds(30);

        session.Complete(firstCompletion);

        AssertRejected(() =>
            session.Complete(
                StartUtc.AddSeconds(45)));

        // Failed second completion must not overwrite
        // the first recorded timestamp.
        Assert.Equal(
            firstCompletion,
            session.CompletedAtUtc);
    }

    [Fact]
    public void Complete_DoesNotChangeIdentityBinding()
    {
        var session = CreateSession();

        session.Complete(StartUtc.AddSeconds(90));

        Assert.Equal(
            ValidSessionId,
            session.SessionId);
        Assert.Equal(
            ValidProfileId,
            session.OwnerHobbiesModuleProfileId);
        Assert.Equal(
            ValidAttractionId,
            session.AttractionId);
        Assert.Equal(
            StartUtc,
            session.StartedAtUtc);
    }

    [Fact]
    public void Complete_After120Seconds_DoesNotApplyGpsPolicy()
    {
        // Capture-session domain records timestamps.
        // Location-policy eligibility is a separate check.
        var session = CreateSession();
        var completedAt = StartUtc.AddSeconds(121);

        session.Complete(completedAt);

        Assert.Equal(
            completedAt,
            session.CompletedAtUtc);
    }
}
