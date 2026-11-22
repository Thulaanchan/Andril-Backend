using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionSubmissionPreflightTests
{
    [Fact]
    public async Task PreparedSubmissionUsesCanonicalEvidenceAndCannotBeForged()
    {
        var ports = new TestPorts();
        var before = DateTimeOffset.UtcNow;
        var prepared = await new MapContributionSubmissionPreflight(ports, ports, ports, ports)
            .PrepareAsync(AttractionId, SessionId, ports.Observation, PhotoKey, "Description");
        Assert.NotNull(prepared);
        var contribution = prepared.Contribution;
        Assert.NotEqual(Guid.Empty, contribution.ContributionId);
        Assert.Equal(OwnerId, contribution.OwnerHobbiesModuleProfileId);
        Assert.Equal(AttractionId, contribution.AttractionId);
        Assert.Equal(SessionId, contribution.CaptureSessionId);
        Assert.Equal(Start, contribution.LocationEvidence.CaptureStartedAt);
        Assert.Equal(Start.AddSeconds(10), contribution.LocationEvidence.CaptureCompletedAt);
        Assert.Equal(ports.Attraction!.Latitude, contribution.LocationEvidence.AttractionLatitude);
        Assert.InRange(contribution.SubmittedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() =>
            new MapContributionSubmissionPreflight.PreparedSubmission(contribution, new object()));
    }

    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid AttractionId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ForeignId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private const string PhotoKey = "private-camera-object";

    [Fact]
    public async Task AuthorizedCompleteCapture_WithTrustedTestPhoto_ReturnsOnlyDecisionWithoutWrites()
    {
        var ports = new TestPorts();
        var session = ports.Session!;

        Assert.True(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture", "attraction", "photo" }, ports.Calls);
        Assert.Equal((SessionId, OwnerId, AttractionId), ports.CaptureScope);
        Assert.Equal((OwnerId, AttractionId, SessionId, PhotoKey), ports.PhotoScope);
        Assert.Equal(Start.AddSeconds(10), session.CompletedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizationUnavailable_StopsBeforeReadingRecords(bool emptyOwner)
    {
        var ports = new TestPorts { AuthorizedOwner = emptyOwner ? Guid.Empty : null };

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access" }, ports.Calls);
    }

    [Theory]
    [InlineData("foreign owner")]
    [InlineData("wrong attraction")]
    [InlineData("wrong session")]
    [InlineData("missing")]
    [InlineData("incomplete")]
    public async Task InvalidCapture_ReturnsSameDenialAndStopsBeforeEvidenceReads(string problem)
    {
        var ports = new TestPorts
        {
            Session = problem switch
            {
                "foreign owner" => CreateSession(ownerId: ForeignId),
                "wrong attraction" => CreateSession(attractionId: ForeignId),
                "wrong session" => CreateSession(sessionId: ForeignId),
                "incomplete" => CreateSession(durationSeconds: null),
                _ => null
            }
        };

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture" }, ports.Calls);
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("accuracy")]
    [InlineData("duration")]
    [InlineData("missing attraction")]
    [InlineData("wrong canonical attraction")]
    public async Task InvalidLocationEvidence_StopsBeforePhotoVerification(string problem)
    {
        var ports = new TestPorts();
        switch (problem)
        {
            case "distance":
                ports.Observation = ports.Observation! with { UserLongitude = 0.001 };
                break;
            case "accuracy":
                ports.Observation = ports.Observation! with { HorizontalAccuracyMetres = 31 };
                break;
            case "duration":
                ports.Session = CreateSession(durationSeconds: 121);
                break;
            case "missing attraction":
                ports.Attraction = null;
                break;
            case "wrong canonical attraction":
                ports.Attraction = MapAttraction.Create(ForeignId, "Other attraction", 0, 0);
                break;
        }

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture", "attraction" }, ports.Calls);
    }

    [Theory]
    [InlineData(MapContributionPhotoVerificationResult.Denied)]
    [InlineData((MapContributionPhotoVerificationResult)99)]
    public async Task PhotoVerification_RequiresExplicitVerifiedResult(MapContributionPhotoVerificationResult result)
    {
        var ports = new TestPorts { PhotoResult = result };

        Assert.False(await ports.CheckAsync());
        Assert.Equal(new[] { "access", "capture", "attraction", "photo" }, ports.Calls);
    }

    [Fact]
    public async Task MissingRequiredEvidence_StopsBeforeCallingPorts()
    {
        var ports = new TestPorts { Observation = null };

        Assert.False(await ports.CheckAsync());
        Assert.Empty(ports.Calls);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("access", 1)]
    [InlineData("capture", 2)]
    [InlineData("attraction", 3)]
    [InlineData("photo", 4)]
    public async Task Cancellation_PropagatesAndStopsFurtherWork(string? cancelAt, int expectedCalls)
    {
        using var cancellation = new CancellationTokenSource();
        var ports = new TestPorts { CancelAt = cancelAt, Cancellation = cancellation };
        if (cancelAt is null)
            cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ports.CheckAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(expectedCalls, ports.Calls.Count);
        Assert.All(ports.Tokens, token => Assert.Equal(cancellation.Token, token));
    }

    [Theory]
    [InlineData("access", 1)]
    [InlineData("capture", 2)]
    [InlineData("attraction", 3)]
    [InlineData("photo", 4)]
    public async Task DependencyFailure_PropagatesAndStopsFurtherWork(string failAt, int expectedCalls)
    {
        var ports = new TestPorts { FailAt = failAt };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ports.CheckAsync());
        Assert.Same(ports.Failure, error);
        Assert.Equal(expectedCalls, ports.Calls.Count);
    }

    private static MapContributionCaptureSession CreateSession(
        Guid? ownerId = null, Guid? attractionId = null, Guid? sessionId = null, int? durationSeconds = 10)
    {
        var session = MapContributionCaptureSession.Create(
            sessionId ?? SessionId, ownerId ?? OwnerId, attractionId ?? AttractionId, Start);
        if (durationSeconds is int seconds)
            session.Complete(Start.AddSeconds(seconds));
        return session;
    }

    private sealed class TestPorts : IMapCaptureSessionAccessReader, IMapContributionCaptureSessionRepository,
        IMapAttractionRepository, IMapContributionPhotoEvidenceReader
    {
        public Guid? AuthorizedOwner { get; init; } = OwnerId;
        public MapContributionCaptureSession? Session { get; set; } = CreateSession();
        public MapAttraction? Attraction { get; set; } = MapAttraction.Create(AttractionId, "Canonical attraction", 0, 0);
        public MapContributionLocationEvidence? Observation { get; set; } = new(0, 0, null, null, 10, null, null);
        public MapContributionPhotoVerificationResult PhotoResult { get; init; } = MapContributionPhotoVerificationResult.Verified;
        public List<string> Calls { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        public (Guid Session, Guid Owner, Guid Attraction) CaptureScope { get; private set; }
        public (Guid Owner, Guid Attraction, Guid Session, string? Key) PhotoScope { get; private set; }
        public string? CancelAt { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public string? FailAt { get; init; }
        public InvalidOperationException Failure { get; } = new("Test dependency failure.");

        public Task<bool> CheckAsync(CancellationToken cancellationToken = default) =>
            new MapContributionSubmissionPreflight(this, this, this, this)
                .PassesChecksAsync(AttractionId, SessionId, Observation, PhotoKey, cancellationToken);

        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attractionId, CancellationToken cancellationToken)
        {
            Assert.Equal(AttractionId, attractionId);
            return Respond("access", AuthorizedOwner, cancellationToken);
        }

        public Task<MapContributionCaptureSession?> FindOwnedAsync(
            Guid sessionId, Guid ownerHobbiesModuleProfileId, Guid attractionId, CancellationToken cancellationToken)
        {
            CaptureScope = (sessionId, ownerHobbiesModuleProfileId, attractionId);
            return Respond("capture", Session, cancellationToken);
        }

        public Task<MapAttraction?> FindByIdAsync(Guid attractionId, CancellationToken cancellationToken)
        {
            Assert.Equal(AttractionId, attractionId);
            return Respond("attraction", Attraction, cancellationToken);
        }

        public Task<MapContributionPhotoVerificationResult> VerifyAsync(
            Guid ownerHobbiesModuleProfileId, Guid attractionId, Guid captureSessionId,
            string? privatePhotoStorageObjectKey, CancellationToken cancellationToken)
        {
            PhotoScope = (ownerHobbiesModuleProfileId, attractionId, captureSessionId, privatePhotoStorageObjectKey);
            return Respond("photo", PhotoResult, cancellationToken);
        }

        private Task<T> Respond<T>(string port, T response, CancellationToken token)
        {
            Calls.Add(port);
            Tokens.Add(token);
            if (port == FailAt)
                return Task.FromException<T>(Failure);
            if (port == CancelAt)
                Cancellation!.Cancel();
            return Task.FromResult(response);
        }

        public Task AddAsync(MapContributionCaptureSession session, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never write capture sessions.");

        public Task<bool> TryCompleteAsync(Guid sessionId, Guid ownerHobbiesModuleProfileId, Guid attractionId,
            DateTimeOffset completedAt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never complete capture sessions.");

        public Task AddAsync(MapAttraction attraction, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never write attractions.");

        public Task<IReadOnlyList<MapAttractionDiscoveryItem>> DiscoverAsync(
            MapAttractionDiscoveryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must never query attraction discovery.");
    }
}
