using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Andril.API.Configuration;
using Andril.API.Integrations;
using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Entities;
using Andril.Common.Interfaces;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Authentication;

public sealed class SupabaseProfilePhotoObjectStorageTests
{
    private static readonly byte[] Synthetic = [13, 42, 7];
    private static readonly StoredProfilePhoto Photo = new(
        new(new(Guid.NewGuid(), "learning"), Guid.NewGuid()), PhotoState.PendingStorage, false,
        PhotoHashes.Compute(Synthetic), "image/jpeg", Synthetic.Length, 300, 300);
    private const string TestKey = "sb_secret_test_only";

    [Fact]
    public async Task UsesTrustedOriginServerKeyAndFixedNonOverwritingPaths()
    {
        var calls = new List<HttpMethod>();
        using var handler = new Handler(async (request, ct) =>
        {
            calls.Add(request.Method);
            Assert.Equal("https://photo-test.supabase.co/storage/v1/object/andril-profile-photos/" + Photo.Address.Key,
                request.RequestUri!.AbsoluteUri);
            Assert.Equal(TestKey, Assert.Single(request.Headers.GetValues("apikey")));
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-upsert")));
                Assert.Equal("image/jpeg", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(Synthetic, await request.Content.ReadAsByteArrayAsync(ct));
            }
            var reply = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Get) reply.Content = Jpeg();
            return reply;
        });
        using var client = new HttpClient(handler);
        var storage = Create(client);
        Assert.Equal(PhotoObjectOutcome.Success, await storage.UploadAsync(Photo, Synthetic, default));
        Assert.Equal(Synthetic, (await storage.ReadAsync(Photo, default)).Bytes);
        Assert.Equal(PhotoObjectOutcome.Success, await storage.DeleteAsync(Photo.Address, default));
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete }, calls);
    }

    [Theory]
    [InlineData(409, "", PhotoObjectOutcome.Conflict)]
    [InlineData(400, "Duplicate", PhotoObjectOutcome.Conflict)]
    [InlineData(401, "", PhotoObjectOutcome.AuthenticationFailure)]
    [InlineData(403, "", PhotoObjectOutcome.PermissionFailure)]
    [InlineData(429, "", PhotoObjectOutcome.RateLimited)]
    [InlineData(500, "", PhotoObjectOutcome.Ambiguous)]
    [InlineData(408, "", PhotoObjectOutcome.Ambiguous)]
    [InlineData(415, "", PhotoObjectOutcome.PermanentFailure)]
    public async Task UploadClassifiesResponsesWithoutExposingProviderDiagnostics(int status, string code, PhotoObjectOutcome expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { code, message = TestKey }))
        }));
        using var client = new HttpClient(handler);
        Assert.Equal(expected, await Create(client).UploadAsync(Photo, Synthetic, default));
    }

    private const string LegacyMissing = """{"statusCode":"404","error":"not_found","message":"Object not found","code":"NoSuchKey"}""";

    [Theory]
    [InlineData(404, """{"code":"NoSuchKey"}""", PhotoObjectOutcome.Missing)]
    [InlineData(400, LegacyMissing, PhotoObjectOutcome.Missing)]
    [InlineData(400, """{"code":"InvalidRequest"}""", PhotoObjectOutcome.InvalidRequest)]
    [InlineData(400, """{"code":"InvalidKey"}""", PhotoObjectOutcome.InvalidObjectPath)]
    [InlineData(404, """{"code":"NoSuchBucket"}""", PhotoObjectOutcome.MissingBucket)]
    [InlineData(400, """{"statusCode":"404","code":"NoSuchBucket"}""", PhotoObjectOutcome.MissingBucket)]
    [InlineData(401, LegacyMissing, PhotoObjectOutcome.AuthenticationFailure)]
    [InlineData(403, LegacyMissing, PhotoObjectOutcome.PermissionFailure)]
    [InlineData(400, """{"code":"FutureProviderCode"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(404, """{"statusCode":"404","error":"not_found","message":"Object not found"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, """{"code":"NoSuchKey"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, """{"statusCode":"403","code":"NoSuchKey"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, """{"statusCode":404,"code":"NoSuchKey"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, """{"statusCode":"404","code":41}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, """{"statusCode":"404","code":"Unknown","code":"NoSuchKey"}""", PhotoObjectOutcome.InvalidResponse)]
    [InlineData(400, "{", PhotoObjectOutcome.InvalidResponse)]
    public async Task OnlyConfirmedMissingObjectCanCompleteCleanup(int status, string body, PhotoObjectOutcome expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body)
        }));
        using var client = new HttpClient(handler);
        var storage = Create(client);
        var read = await storage.ReadAsync(Photo, default);
        Assert.Equal(expected, read.Outcome);
        Assert.Null(read.Bytes);
        Assert.Equal(expected, await storage.DeleteAsync(Photo.Address, default));
    }

    [Fact]
    public async Task OversizedErrorEnvelopeCannotConfirmDeletion()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(LegacyMissing + new string(' ', 4096))
        }));
        using var client = new HttpClient(handler);
        Assert.Equal(PhotoObjectOutcome.InvalidResponse, await Create(client).DeleteAsync(Photo.Address, default));
    }

    [Fact]
    public async Task LegacyMissingProbeAllowsImmutableUploadAndVerifiedPublicationThroughTheRealAdapter()
    {
        var fixture = new UploadWorkflow();
        var calls = new List<HttpMethod>();
        using var handler = new Handler(async (request, ct) =>
        {
            var row = Assert.IsType<PrivateProfilePhoto>(fixture.Row);
            Assert.Equal(PhotoState.PendingStorage, row.State);
            Assert.EndsWith("/andril-profile-photos/" + row.ObjectKey, request.RequestUri!.AbsolutePath);
            calls.Add(request.Method);
            if (calls.Count == 1)
                return new(HttpStatusCode.BadRequest) { Content = new StringContent(LegacyMissing) };
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-upsert")));
                Assert.Equal(Synthetic, await request.Content!.ReadAsByteArrayAsync(ct));
                return new(HttpStatusCode.OK);
            }
            return new(HttpStatusCode.OK) { Content = Jpeg() };
        });
        using var client = new HttpClient(handler);
        var service = new ProfilePhotoService(fixture, fixture, fixture, fixture, Create(client));
        var requestId = Guid.NewGuid();
        var status = await service.UploadAsync("learning", requestId, new MemoryStream([31]), default);
        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Get }, calls);
        Assert.Equal(requestId, fixture.Row!.RequestId);
        Assert.NotEqual(requestId, status.UploadId);
        Assert.Equal(PhotoState.Stored, fixture.Row.State);
        Assert.True(status.IsCurrent);
        Assert.Equal(PhotoHashes.Compute(Synthetic), fixture.Row.StoredContentHash);
    }

    private sealed class UploadWorkflow : IPrivateProfileService, IPrivateProfilePhotoStore, IProfileImageInspector, IPhotoUploadPolicy
    {
        internal PrivateProfilePhoto? Row;
        private readonly Guid profile = Guid.NewGuid();
        private static readonly DateTimeOffset Now = new(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        public bool IsApprovedAndConfigured => true;
        public int RequireMaximumBytes() => ProfilePhotoLimits.UploadBytes;
        public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken ct) =>
            Task.FromResult(new SafeProfileImage(Synthetic, "image/jpeg", 300, 300));
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken ct) =>
            Task.FromResult(new PrivateProfileDto(profile, module, Now));
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken ct) => throw new NotSupportedException();
        public async Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken ct) =>
            await action(await GetAsync(module, ct));
        public Task<PrivateProfilePhoto?> FindRequestAsync(PhotoScope scope, Guid requestId, CancellationToken ct) =>
            Task.FromResult(Row);
        public Task<PrivateProfilePhoto> ReserveAsync(PhotoScope scope, Guid requestId, string originalHash, PhotoUploadEvidence? evidence, CancellationToken ct)
        {
            Assert.Null(Row);
            Row = PrivateProfilePhoto.Create(scope, Guid.NewGuid(), requestId, evidence!, Guid.NewGuid(), Now, Now.AddMinutes(2));
            Assert.Equal(PhotoHashes.Compute([31]), originalHash);
            return Task.FromResult(Row);
        }
        public Task<bool> OwnsLeaseAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) =>
            Task.FromResult(Row!.Id == id && Row.OwnsLease(token, Now));
        public Task<PrivatePhotoStatus> FinalizeAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct)
        {
            Assert.Equal(id, Row!.Id);
            Row.MarkStored(token, Now);
            return Task.FromResult(Row.Status());
        }
        public Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct) =>
            throw new InvalidOperationException("The verified object must be published.");
        public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct) => Task.FromResult(Row?.Status());
        public Task<StoredProfilePhoto?> ReadCurrentAsync(PhotoScope scope, CancellationToken ct) => Task.FromResult(Row?.Evidence());
        public Task<PhotoCleanupClaim?> ClaimCleanupAsync(PhotoScope scope, CancellationToken ct) =>
            Task.FromResult<PhotoCleanupClaim?>(null);
        public Task<bool> RecordCleanupAsync(PhotoCleanupClaim claim, bool removed, TimeSpan retryDelay, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task DownloadRejectsWrongHashAndOverrunWithoutReturningProviderBytes()
    {
        var first = true;
        using var handler = new Handler((_, _) =>
        {
            HttpContent content = first ? Jpeg([33, 44, 55]) : new UnboundedLengthContent();
            first = false;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new HttpClient(handler);
        var storage = Create(client);
        for (int i = 0; i < 2; i++)
        {
            var result = await storage.ReadAsync(Photo, default);
            Assert.Equal(PhotoObjectOutcome.IntegrityFailure, result.Outcome);
            Assert.Null(result.Bytes);
        }
    }

    [Fact]
    public async Task RedirectsAndUntrustedOriginsNeverSendCredentialsToForeignHost()
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("photo-test.supabase.co", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://foreign.example/") }
            });
        });
        using var client = new HttpClient(handler);
        Assert.Equal(PhotoObjectOutcome.TransientFailure, (await Create(client).ReadAsync(Photo, default)).Outcome);
        foreach (var origin in new[] { "http://photo-test.supabase.co", "https://photo-test.supabase.co.foreign.example", "https://photo-test.supabase.co/path" })
        {
            var rejected = Create(client, origin);
            Assert.False(rejected.IsConfigured);
            Assert.Equal(PhotoObjectOutcome.PermanentFailure, await rejected.DeleteAsync(Photo.Address, default));
        }
        Assert.Equal(PhotoObjectOutcome.PermanentFailure, await Create(client).DeleteAsync(
            new(new(Photo.Address.Scope.ProfileId, "../social"), Photo.Address.UploadId), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TransportTimeoutIsAmbiguousAndCallerCancellationPropagates()
    {
        using var handler = new Handler((_, ct) => ct.IsCancellationRequested
            ? Task.FromCanceled<HttpResponseMessage>(ct)
            : Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        using var client = new HttpClient(handler);
        var storage = Create(client);
        Assert.Equal(PhotoObjectOutcome.Ambiguous, await storage.UploadAsync(Photo, Synthetic, default));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.UploadAsync(Photo, Synthetic, cts.Token));
    }

    private static ByteArrayContent Jpeg(byte[]? content = null)
    {
        var result = new ByteArrayContent(content ?? Synthetic);
        result.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return result;
    }

    private static SupabaseProfilePhotoObjectStorage Create(HttpClient client, string origin = "https://photo-test.supabase.co") =>
        new(client, Options.Create(new ProfilePhotoOptions
        {
            Enabled = true, Storage = new() { ProjectUrl = origin, SecretKey = TestKey }
        }), new ReadinessOnlyInspector());

    private sealed class ReadinessOnlyInspector : IProfileImageInspector
    {
        public bool IsApprovedAndConfigured => true;
        public Task<SafeProfileImage> InspectAsync(byte[] bytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class UnboundedLengthContent : HttpContent
    {
        public UnboundedLengthContent() => Headers.ContentType = new("image/jpeg");
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => CreateContentReadStreamAsync();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    }
}
