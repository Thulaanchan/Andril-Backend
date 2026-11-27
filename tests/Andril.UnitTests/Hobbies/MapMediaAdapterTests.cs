using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Andril.Hobbies.Map.Contributions;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapMediaAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private readonly MapMediaReservation reservation = new(
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
        Guid.NewGuid(), Guid.NewGuid(), Now, Now.AddSeconds(120));

    [Fact]
    public async Task JpegInspection_RequiresDecodedImageAndRejectsTruncationOrExtraContent()
    {
        // Encoded 3 × 3 test image. No platform image APIs are needed to run the test.
        var jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAx" +
            "NDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIy" +
            "MjIyMjIyMjL/wAARCAADAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
            "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
            "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
            "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
            "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
            "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
            "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooooA//2Q==");
        var inspector = new MapJpegInspector();
        Assert.True(await inspector.IsValidJpegAsync(jpeg, default));
        Assert.False(await inspector.IsValidJpegAsync(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }, default));
        Assert.False(await inspector.IsValidJpegAsync(jpeg[..^8].Concat(new byte[] { 0xff, 0xd9 }).ToArray(), default));
        Assert.False(await inspector.IsValidJpegAsync(jpeg.Concat(jpeg).ToArray(), default));
    }

    [Fact]
    public async Task Storage_UsesCreateOnlyAndRequiresMatchingVersionedContentOnRetry()
    {
        byte[] jpeg = [1, 2, 3];
        var requests = 0;
        var served = jpeg;
        var version = new EntityTagHeaderValue("\"v1\"");
        var clock = new TestClock();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            requests++;
            var route = request.Method == HttpMethod.Get ? "object/authenticated" : "object";
            Assert.Equal($"/storage/v1/{route}/hobbies-map-private/map/{reservation.MediaId:N}.jpg",
                request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-upsert")));
                Assert.Equal(jpeg, await request.Content!.ReadAsByteArrayAsync(token));
                return new(HttpStatusCode.Conflict);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Headers = { ETag = version }, Content = new ByteArrayContent(served) };
        }));
        var storage = new SupabaseMapPrivateMediaStorage(new("https://map-test.supabase.co"), "test-key",
            new Inspector(), clock, client);
        Assert.False(await storage.CreateAsync(reservation, new byte[] { 0, 2, 3 }, default));
        Assert.False(await storage.CreateAsync(reservation, new byte[SupabaseMapPrivateMediaStorage.MaximumBytes + 1], default));
        Assert.True(await storage.CreateAsync(reservation, jpeg, default));
        var media = Assert.IsType<VerifiedMapMedia>(await storage.InspectAsync(reservation, default));
        Assert.Equal(version.Tag, media.Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(jpeg)), media.Sha256);

        served = [1, 2, 4];
        Assert.False(await storage.CreateAsync(reservation, jpeg, default));
        version = new EntityTagHeaderValue("\"v1\"", isWeak: true);
        Assert.Null(await storage.InspectAsync(reservation, default));
        version = EntityTagHeaderValue.Any;
        Assert.Null(await storage.InspectAsync(reservation, default));
        clock.Now = reservation.UploadExpiresAt;
        Assert.Null(await storage.AuthorizeCreateAsync(reservation, default));
        Assert.False(await storage.CreateAsync(reservation, jpeg, default));
        Assert.Equal(7, requests);
    }

    [Fact]
    public async Task Provenance_RequiresExactClaimAndIndependentIntegritySignals()
    {
        var media = new VerifiedMapMedia(reservation.MediaId, "private", "version", new string('A', 64));
        var claim = new MapCaptureStatement(reservation.Scope, media.MediaId, reservation.CaptureChallenge,
            media.Version, media.Sha256, Now, true, true, true);
        var verifier = new Verifier { Claim = claim };
        var provenance = new VerifiedMapCameraProvenance(verifier);
        Assert.Equal(Now, await provenance.VerifyCaptureAsync(reservation, media, default));
        foreach (var rejected in new MapCaptureStatement?[]
        {
            null,
            claim with { ApplicationIntegrityVerified = false },
            claim with { DeviceIntegrityVerified = false },
            claim with { FreshCameraCaptureVerified = false },
            claim with { Challenge = Guid.NewGuid() },
            claim with { CapturedAt = Now.AddTicks(-1) },
            claim with { CapturedAt = reservation.UploadExpiresAt },
            claim with { Scope = claim.Scope with { ProfileId = Guid.NewGuid() } },
            claim with { Version = "different" },
            claim with { Sha256 = new string('B', 64) }
        })
        {
            verifier.Claim = rejected;
            Assert.Null(await provenance.VerifyCaptureAsync(reservation, media, default));
        }
    }

    private sealed class Inspector : IMapJpegInspector
    {
        public Task<bool> IsValidJpegAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            Task.FromResult(!bytes.IsEmpty && bytes.Span[0] == 1);
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MapMediaAdapterTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Verifier : IMapCaptureStatementVerifier
    {
        internal MapCaptureStatement? Claim { get; set; }
        public Task<MapCaptureStatement?> FindVerifiedAsync(
            MapMediaScope scope, Guid mediaId, Guid challenge, CancellationToken cancellationToken) =>
            Task.FromResult(Claim);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
