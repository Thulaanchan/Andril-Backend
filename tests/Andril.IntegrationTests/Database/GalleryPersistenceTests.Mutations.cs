using System.Net;
using System.Net.Http.Headers;
using Andril.API.Authentication;
using Andril.API.Authorization.Hobbies;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    [Fact]
    public async Task UploadMutation_ReconcilesUnknownCreateWithoutOverwritingAndRejectsSubstitution()
    {
        var authority = new TestGalleryAuthorization();
        var repo = new TestGalleryReadRepository();
        var boundary = authority.CreateBoundary(repo, repo);
        var validator = new GalleryJpegUploadValidator(1024, 16, 768);
        using var input = new MemoryStream(MutationJpeg());
        var store = new ControlledUpload { LoseCreateAcknowledgment = true };
        var operationId = Guid.NewGuid();
        async Task<GalleryMetadataResult<GalleryMutationReceipt>> Upload()
        {
            input.Position = 0;
            return await boundary.UploadPrivateMediaAsync(operationId, input, "image/jpeg", validator, store, store);
        }

        Assert.Equal(GalleryMetadataStatus.MutationPending, (await Upload()).Status);
        Assert.Equal(1, store.Creates);
        Assert.Equal(0, store.Commits);
        store.Substitute = true;
        Assert.Equal(GalleryMetadataStatus.MutationPending, (await Upload()).Status);
        store.Substitute = false;
        store.FailCommit = true;
        Assert.Equal(GalleryMetadataStatus.MutationPending, (await Upload()).Status);
        store.FailCommit = false;
        store.BeforeInspect = () => authority.ProfileId = null;
        Assert.Equal(GalleryMetadataStatus.Denied, (await Upload()).Status);
        Assert.Equal(0, store.Commits);
        authority.ProfileId = Owner;
        store.BeforeInspect = null;
        Assert.Equal(Newer, (await Upload()).Value?.MediaId);
        Assert.Equal(Newer, (await Upload()).Value?.MediaId);
        Assert.Equal(1, store.Creates);
        Assert.Equal(1, store.Commits);
        Assert.Equal(4, store.Inspections);
        var original = MutationJpeg();
        using var modified = new MemoryStream([.. original[..2], 255, 254, 0, 3, 65, .. original[2..]]);
        Assert.Equal(GalleryMetadataStatus.MutationConflict,
            (await boundary.UploadPrivateMediaAsync(operationId, modified, "image/jpeg", validator, store, store)).Status);
        Assert.Equal(1, store.Creates);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.UploadPrivateMediaAsync(
            operationId, input, "image/jpeg", validator, store, store, cancellation.Token));
    }

    [Fact]
    public async Task UploadMutationHttp_AuthenticatesAndSerializesOnlyTheCompletedMediaId()
    {
        const string path = "/controlled/gallery/upload";
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository();
        var store = new ControlledUpload { LoseCreateAcknowledgment = true };
        var operation = Guid.NewGuid();
        var uploadPath = $"{path}/{operation:D}";
        var validator = new GalleryJpegUploadValidator(1024, 16, 768);
        await using var app = await StartMetadataHostAsync(authority, repository, repository,
            new EphemeralDataProtectionProvider(), host => host.MapPost(path + "/{operationId:guid}",
                async (Guid operationId, HttpContext http, HttpCurrentUser current) =>
                {
                    http.Response.Headers.CacheControl = "no-store";
                    var result = await authority.CreateBoundary(repository, repository, current).UploadPrivateMediaAsync(
                        operationId, http.Request.Body, http.Request.ContentType, validator, store, store, http.RequestAborted);
                    return result.Status switch
                    {
                        GalleryMetadataStatus.MutationPending => Results.Json(new { code = "gallery_upload_pending" }, statusCode: 202),
                        GalleryMetadataStatus.MutationConflict => Results.Conflict(new { code = "gallery_upload_conflict" }),
                        GalleryMetadataStatus.InvalidMediaContent => Results.BadRequest(new { code = "invalid_gallery_upload" }),
                        _ => AsTestResponse(result)
                    };
                }).RequireAuthorization());
        using var client = app.GetTestClient();
        using var content = new ByteArrayContent(MutationJpeg());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var anonymous = await client.PostAsync(uploadPath, content);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, store.Creates);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic");
        using var pending = await client.PostAsync(uploadPath, content);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.Equal("""{"code":"gallery_upload_pending"}""", await pending.Content.ReadAsStringAsync());
        using var accepted = await client.PostAsync(uploadPath, content);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal($$"""{"mediaId":"{{Newer}}"}""", await accepted.Content.ReadAsStringAsync());
        Assert.Equal(1, store.Commits);
    }

    [MapCapturePostgresFact]
    public Task LogicalDelete_PostgresPreservesCoverAndRollsBackOnRevocation() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [KeysetMedia(1, CreatedAt), KeysetMedia(2, CreatedAt),
            KeysetMedia(3, CreatedAt, OtherOwner), KeysetMedia(4, CreatedAt, module: "Learning"),
            KeysetMedia(5, CreatedAt)]);
        await using var context = CreateContext(connection);
        var repo = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var lifecycle = (IGalleryLifecycleRepository)repo;
        var authority = new TestGalleryAuthorization();
        var boundary = authority.CreateBoundary(repo, repo);
        Assert.True(await repo.SetCoverAsync(KeysetId(1), Owner));
        Assert.Equal(GalleryMetadataStatus.MutationConflict, (await boundary.DeletePrivateMediaAsync(KeysetId(1), lifecycle)).Status);
        foreach (var unavailable in new[] { 3, 4, 99 })
            Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
                (await boundary.DeletePrivateMediaAsync(KeysetId(unavailable), lifecycle)).Status);

        var checks = 0;
        Assert.Equal(GalleryLogicalDeleteOutcome.Denied, await lifecycle.DeleteOwnedAsync(KeysetId(2), Owner,
            _ => Task.FromResult(++checks == 1), default));
        Assert.NotNull(await repo.GetOwnedByIdAsync(KeysetId(2), Owner));

        Assert.Equal(GalleryMetadataStatus.Success, (await boundary.DeletePrivateMediaAsync(KeysetId(2), lifecycle)).Status);
        Assert.Equal(GalleryMetadataStatus.Success, (await boundary.DeletePrivateMediaAsync(KeysetId(2), lifecycle)).Status);
        Assert.Null(await repo.GetOwnedByIdAsync(KeysetId(2), Owner));
        Assert.False(await repo.SetCoverAsync(KeysetId(2), Owner));
        Assert.Equal(KeysetId(1), await repo.GetCoverIdAsync(Owner));
        var reader = new ControlledObjectReader();
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable, (await boundary.ReadPrivateMediaAsync(KeysetId(2), reader, 3)).Status);
        Assert.Equal(0, reader.Reads);

        await using var coverContext = CreateContext(connection);
        var coverRepo = new GalleryRepository<HobbiesGalleryDbContext>(coverContext);
        var coverSelected = false;
        var error = await Record.ExceptionAsync(() => lifecycle.DeleteOwnedAsync(KeysetId(5), Owner, async token =>
        {
            if (!coverSelected)
                coverSelected = await coverRepo.SetCoverAsync(KeysetId(5), Owner, token);
            return true;
        }, default));
        Assert.True(coverSelected);
        Assert.NotNull(error);
        Assert.Equal(KeysetId(5), await repo.GetCoverIdAsync(Owner));
        Assert.NotNull(await repo.GetOwnedByIdAsync(KeysetId(5), Owner));
    });

    private static byte[] MutationJpeg()
    {
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
        return jpeg;
    }

    [Fact]
    public async Task UploadReceipt_ConcurrentRetryInspectsAndLostCommitReplyReplaysConfirmedReceipt()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository();
        var validator = new GalleryJpegUploadValidator(1024, 16, 768);
        var operationId = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledUpload
        {
            LoseCommitAcknowledgment = true,
            BeforeCreate = async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
        };
        async Task<GalleryMetadataResult<GalleryMutationReceipt>> SendAsync(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            return await authority.CreateBoundary(repository, repository).UploadPrivateMediaAsync(
                operationId, stream, "image/jpeg", validator, store, store);
        }

        var jpeg = MutationJpeg();
        var first = SendAsync(jpeg);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GalleryMetadataStatus.MutationPending, (await SendAsync(jpeg)).Status);
            Assert.Equal(GalleryMetadataStatus.MutationConflict,
                (await SendAsync([.. jpeg[..2], 255, 254, 0, 3, 65, .. jpeg[2..]])).Status);
            Assert.Equal(1, store.Creates);
            Assert.Equal(1, store.Inspections);
            Assert.Equal(0, store.Commits);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(GalleryMetadataStatus.MutationPending, (await first).Status);
        Assert.Equal(Newer, (await SendAsync(jpeg)).Value?.MediaId);
        Assert.Equal(1, store.Creates);
        Assert.Equal(1, store.Commits);
    }

    private sealed class ControlledUpload : IGalleryUploadOperations, IGalleryPrivateObjectMutations
    {
        private readonly object gate = new();
        private GalleryUploadObject? providerEvidence;
        private GalleryUploadReceipt? receipt;
        public int Creates { get; private set; }
        public int Inspections { get; private set; }
        public int Commits { get; private set; }
        public bool LoseCreateAcknowledgment { get; init; }
        public bool LoseCommitAcknowledgment { get; init; }
        public bool Substitute { get; set; }
        public bool FailCommit { get; set; }
        public Action? BeforeInspect { get; set; }
        public Func<CancellationToken, Task>? BeforeCreate { get; init; }

        public Task<GalleryUploadReservation> ReserveAsync(GalleryUploadIntent intent, CancellationToken token)
        {
            lock (gate)
            {
                if (receipt is not null)
                    return Task.FromResult(receipt.Replay(intent));
                Assert.Equal(Owner, intent.OwnerProfileId);
                var binding = new GalleryUploadBinding(intent, new(Newer, Owner, "Hobbies", "synthetic-upload-object"));
                receipt = Assert.IsType<GalleryUploadReceipt>(GalleryUploadReceipt.Reserve(binding));
                return Task.FromResult(new GalleryUploadReservation(GalleryUploadReservationState.Create, binding));
            }
        }

        public async Task<GalleryUploadObject?> CreateOnlyAsync(GalleryUploadBinding expected, ReadOnlyMemory<byte> content,
            CancellationToken token)
        {
            Assert.Null(providerEvidence);
            Assert.Equal(expected.Intent.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Span)));
            Creates++;
            if (BeforeCreate is not null)
                await BeforeCreate(token);
            providerEvidence = new(expected, "synthetic-generation");
            if (LoseCreateAcknowledgment)
                throw new TimeoutException("synthetic provider diagnostic");
            return providerEvidence;
        }

        public Task<GalleryUploadObject?> InspectAsync(GalleryUploadBinding expected, CancellationToken token)
        {
            Inspections++;
            BeforeInspect?.Invoke();
            return Task.FromResult(Substitute ? providerEvidence! with {
                Binding = expected with { Object = expected.Object with { OwnerModuleProfileId = OtherOwner } }
            } : providerEvidence);
        }

        public async Task<bool> CompleteAsync(GalleryUploadObject evidence, Func<CancellationToken, Task<bool>> authorize,
            CancellationToken token)
        {
            if (!await authorize(token))
                return false;
            if (FailCommit)
                throw new InvalidOperationException("synthetic store diagnostic");
            Assert.Equal(providerEvidence, evidence);
            lock (gate)
            {
                var current = Assert.IsType<GalleryUploadReceipt>(receipt);
                var verified = Assert.IsType<GalleryUploadReceipt>(current.Verify(current.Revision, evidence));
                receipt = Assert.IsType<GalleryUploadReceipt>(verified.Complete(verified.Revision, evidence));
                if (!current.Completed)
                    Commits++;
            }
            if (LoseCommitAcknowledgment)
                throw new TimeoutException("synthetic commit acknowledgment");
            return true;
        }
    }
}
