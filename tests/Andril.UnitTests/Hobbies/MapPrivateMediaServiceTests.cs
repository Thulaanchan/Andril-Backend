using Andril.Hobbies.Map.Contributions;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapPrivateMediaServiceTests
{
    [Fact]
    public async Task OnlyOneGrantAndFinalizedBindingSurviveConcurrentRequests()
    {
        var fixture = new Fixture();
        var grants = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => fixture.Service.AuthorizeAsync(fixture.Attraction, fixture.Session.SessionId))));
        var grant = Assert.Single(grants.OfType<MapUploadGrant>());
        Assert.NotEqual(Guid.Empty, grant.MediaId);
        Assert.NotEqual(Guid.Empty, grant.CaptureChallenge);
        Assert.Null(await fixture.FindAsync());
        fixture.CompleteCapture();

        var completed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => fixture.Service.CompleteAsync(fixture.Attraction, fixture.Session.SessionId, grant.MediaId))));
        Assert.All(completed, value => Assert.True(value));
        Assert.Equal(1, fixture.FinalizationCount);
        var reader = new TrustedMapContributionPhotoEvidenceReader(fixture, fixture, fixture, fixture.Service, fixture);
        Assert.Equal(MapContributionPhotoVerificationResult.Verified, await reader.VerifyAsync(
            fixture.Profile, fixture.Attraction, fixture.Session.SessionId, Fixture.PrivateReference));
        Assert.Equal(MapContributionPhotoVerificationResult.Denied, await reader.VerifyAsync(
            fixture.Profile, fixture.Attraction, fixture.Session.SessionId, "other-object"));

        fixture.Version = "replacement";
        Assert.False(await fixture.Service.CompleteAsync(fixture.Attraction, fixture.Session.SessionId, grant.MediaId));
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(1, fixture.FinalizationCount);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("inactive")]
    [InlineData("foreign profile")]
    [InlineData("foreign session")]
    [InlineData("foreign attraction")]
    [InlineData("expired")]
    [InlineData("overlong capture")]
    public async Task InvalidScopeCannotReserveOrAuthorizeUpload(string reason)
    {
        var fixture = new Fixture(reason == "overlong capture" ? 121 : 10);
        var attraction = fixture.Attraction;
        var session = fixture.Session.SessionId;
        switch (reason)
        {
            case "anonymous": fixture.IsAuthenticated = false; break;
            case "inactive": fixture.AuthorizedProfile = null; break;
            case "foreign profile": fixture.AuthorizedProfile = Guid.NewGuid(); break;
            case "foreign session": session = Guid.NewGuid(); break;
            case "foreign attraction": attraction = Guid.NewGuid(); break;
            case "expired": fixture.Now = fixture.Now.AddMinutes(11); break;
        }
        Assert.Null(await fixture.Service.AuthorizeAsync(attraction, session));
        Assert.Equal(0, fixture.ReservationCount);
    }

    [Theory]
    [InlineData("missing object")]
    [InlineData("invalid integrity")]
    [InlineData("wrong identity")]
    [InlineData("untrusted origin")]
    [InlineData("stale provenance")]
    [InlineData("revoked")]
    public async Task IncompleteOrUntrustedMediaCannotFinalize(string reason)
    {
        var fixture = new Fixture();
        var grant = await fixture.Service.AuthorizeAsync(fixture.Attraction, fixture.Session.SessionId);
        Assert.NotNull(grant);
        fixture.CompleteCapture();
        var mediaId = grant.MediaId;
        switch (reason)
        {
            case "missing object": fixture.HasObject = false; break;
            case "invalid integrity": fixture.Sha256 = "invalid"; break;
            case "wrong identity": mediaId = Guid.NewGuid(); break;
            case "untrusted origin": fixture.CapturedAt = null; break;
            case "stale provenance": fixture.CapturedAt = fixture.Session.StartedAtUtc.AddTicks(-1); break;
            case "revoked": fixture.AuthorizedProfile = null; break;
        }
        Assert.False(await fixture.Service.CompleteAsync(fixture.Attraction, fixture.Session.SessionId, mediaId));
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(0, fixture.FinalizationCount);
    }

    private sealed class Fixture : TimeProvider, ICurrentUser, IMapCaptureSessionAccessReader,
        IMapContributionCaptureSessionRepository, IMapPrivateMediaStore, IMapPrivateMediaStorage, IMapCameraProvenance
    {
        internal const string PrivateReference = "test-only-private-reference";
        private readonly object gate = new();
        private MapMediaReservation? reservation;
        private FinalizedMapMedia? finalized;

        internal Fixture(int captureSeconds = 10)
        {
            AuthorizedProfile = Profile;
            Session = MapContributionCaptureSession.Create(Guid.NewGuid(), Profile, Attraction, Now.AddSeconds(-captureSeconds));
            Service = new MapPrivateMediaService(this, this, this, this, this, this, this);
        }

        internal void CompleteCapture()
        {
            CapturedAt = Now.AddSeconds(1);
            Now = Now.AddSeconds(2);
            Session.Complete(Now);
        }

        public bool IsAuthenticated { get; set; } = true;
        public Guid? UserId { get; } = Guid.NewGuid();
        internal Guid Profile { get; } = Guid.NewGuid();
        internal Guid Attraction { get; } = Guid.NewGuid();
        internal Guid? AuthorizedProfile { get; set; }
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        internal DateTimeOffset? CapturedAt { get; set; }
        internal bool HasObject { get; set; } = true;
        internal string Version { get; set; } = "original";
        internal string Sha256 { get; set; } = new('A', 64);
        internal int ReservationCount { get; private set; }
        internal int FinalizationCount { get; private set; }
        internal MapContributionCaptureSession Session { get; }
        internal MapPrivateMediaService Service { get; }

        public override DateTimeOffset GetUtcNow() => Now;
        internal Task<MapCameraEvidence?> FindAsync() =>
            Service.FindAsync(UserId!.Value, Profile, Attraction, Session.SessionId, default);
        public Task<Guid?> ResolveAuthorizedProfileIdAsync(Guid attraction, CancellationToken token) =>
            Task.FromResult(attraction == Attraction ? AuthorizedProfile : null);
        public Task<MapContributionCaptureSession?> FindOwnedAsync(Guid session, Guid owner, Guid attraction, CancellationToken token) =>
            Task.FromResult(session == Session.SessionId && owner == Profile && attraction == Attraction ? Session : null);
        public Task AddAsync(MapContributionCaptureSession session, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryCompleteAsync(Guid session, Guid owner, Guid attraction, DateTimeOffset time, CancellationToken token) =>
            throw new NotSupportedException();

        public Task<bool> TryReserveAsync(MapMediaReservation candidate, CancellationToken token)
        {
            lock (gate)
            {
                if (reservation is not null)
                    return Task.FromResult(false);
                reservation = candidate;
                ReservationCount++;
                return Task.FromResult(true);
            }
        }

        public Task<MapMediaReservation?> FindReservationAsync(MapMediaScope scope, CancellationToken token) =>
            Task.FromResult(reservation?.Scope == scope ? reservation : null);
        public Task<FinalizedMapMedia?> FindFinalizedAsync(MapMediaScope scope, CancellationToken token)
        {
            lock (gate)
                return Task.FromResult(finalized?.Reservation.Scope == scope ? finalized : null);
        }

        public Task<bool> TryFinalizeAsync(FinalizedMapMedia candidate, CancellationToken token)
        {
            lock (gate)
            {
                if (finalized is not null || candidate.Reservation != reservation)
                    return Task.FromResult(false);
                finalized = candidate;
                FinalizationCount++;
                return Task.FromResult(true);
            }
        }

        public Task<MapUploadGrant?> AuthorizeCreateAsync(MapMediaReservation candidate, CancellationToken token) =>
            Task.FromResult<MapUploadGrant?>(new(candidate.MediaId, "test-only-upload-capability"));
        public Task<VerifiedMapMedia?> InspectAsync(MapMediaReservation candidate, CancellationToken token) =>
            Task.FromResult<VerifiedMapMedia?>(HasObject ? new(candidate.MediaId, PrivateReference, Version, Sha256) : null);
        public Task<DateTimeOffset?> VerifyCaptureAsync(MapMediaReservation candidate, VerifiedMapMedia media, CancellationToken token) =>
            Task.FromResult(CapturedAt);
    }
}
