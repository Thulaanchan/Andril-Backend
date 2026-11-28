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
    private const string ControlledContentPath = "/controlled/gallery/content";
    private const string ControlledValidationPath = "/controlled/gallery/validate";

    [Fact]
    public async Task PrivateDelivery_DeniesWrongVersionAndRechecksBindingAndScopeBeforeRelease()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository { Media = NewPrivateMedia() };
        var boundary = authority.CreateBoundary(repository, repository);
        var provider = new ControlledObjectReader();
        var accepted = await boundary.ReadPrivateMediaAsync(Newer, provider, 3);
        Assert.Equal(GalleryMetadataStatus.Success, accepted.Status);
        Assert.Equal(provider.Bytes, accepted.Value);
        Assert.NotSame(provider.Bytes, accepted.Value);
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
            (await boundary.ReadPrivateMediaAsync(Newer, provider, 2)).Status);

        provider.OnRead = item => item with { Version = "replacement" };
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
            (await boundary.ReadPrivateMediaAsync(Newer, provider, 3)).Status);
        provider.OnRead = item =>
        {
            repository.Media!.IsDeleted = true;
            return item;
        };
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
            (await boundary.ReadPrivateMediaAsync(Newer, provider, 3)).Status);
        repository.Media = NewPrivateMedia();
        provider.OnRead = _ => throw new InvalidOperationException("synthetic provider diagnostic");
        var failure = await boundary.ReadPrivateMediaAsync(Newer, provider, 3);
        Assert.Equal(GalleryMetadataStatus.DependencyUnavailable, failure.Status);
        Assert.Null(failure.Value);

        var reads = provider.Reads;
        provider.OnFind = item => item with { Binding = item.Binding with { OwnerModuleProfileId = OtherOwner } };
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable,
            (await boundary.ReadPrivateMediaAsync(Newer, provider, 3)).Status);
        provider.OnFind = item =>
        {
            authority.Allowed = false;
            return item;
        };
        Assert.Equal(GalleryMetadataStatus.Denied,
            (await boundary.ReadPrivateMediaAsync(Newer, provider, 3)).Status);
        Assert.Equal(reads, provider.Reads);

        authority.Allowed = true;
        provider.OnFind = item => item;
        provider.OnRead = item =>
        {
            authority.ProfileId = null;
            return item;
        };
        var revoked = await boundary.ReadPrivateMediaAsync(Newer, provider, 3);
        Assert.Equal(GalleryMetadataStatus.Denied, revoked.Status);
        Assert.Null(revoked.Value);

        authority.ProfileId = Owner;
        using var cancellation = new CancellationTokenSource();
        provider.OnRead = item =>
        {
            cancellation.Cancel();
            return item;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => boundary.ReadPrivateMediaAsync(Newer, provider, 3, cancellation.Token));
    }

    [Fact]
    public async Task PrivateDeliveryHttp_RequiresIdentityAndReturnsUncacheableBytesOrSafeErrors()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository { Media = NewPrivateMedia() };
        var provider = new ControlledObjectReader();
        await using var app = await StartMetadataHostAsync(authority, repository, repository,
            new EphemeralDataProtectionProvider(), app => MapControlledMedia(app, authority, repository, repository, provider));
        using var client = app.GetTestClient();
        var path = ControlledContentPath + "/" + Newer;
        using var unauthenticated = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(0, provider.Reads);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic");
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(provider.Bytes, await response.Content.ReadAsByteArrayAsync());

        using var upload = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]);
        upload.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var invalid = await client.PostAsync(ControlledValidationPath, upload);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("{\"code\":\"invalid_gallery_upload\"}", await invalid.Content.ReadAsStringAsync());

        authority.Allowed = false;
        await AssertMetadataErrorAsync(client, path, HttpStatusCode.Forbidden, "gallery_access_denied");
        Assert.Equal(1, provider.Reads);
    }

    [MapCapturePostgresFact]
    public Task PrivateDeliveryHttp_PostgresAdmitsOnlyOwnedLivePrivateBinding() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [KeysetMedia(1, CreatedAt), KeysetMedia(10, CreatedAt, OtherOwner),
            KeysetMedia(11, CreatedAt, module: "Learning"), KeysetMedia(12, CreatedAt, deleted: true)]);
        await using var context = CreateContext(connection);
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var authority = new TestGalleryAuthorization();
        var provider = new ControlledObjectReader();
        await using var app = await StartMetadataHostAsync(authority, repository, repository,
            new EphemeralDataProtectionProvider(), app => MapControlledMedia(app, authority, repository, repository, provider));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic");
        Assert.Equal(provider.Bytes, await client.GetByteArrayAsync(ControlledContentPath + "/" + KeysetId(1)));
        foreach (var unavailable in new[] { 10, 11, 12, 99 })
            await AssertMetadataErrorAsync(client, ControlledContentPath + "/" + KeysetId(unavailable),
                HttpStatusCode.NotFound, "gallery_media_unavailable");
        Assert.Equal(1, provider.Reads);
    });

    private static void MapControlledMedia(WebApplication app, TestGalleryAuthorization authority,
        IGalleryRepository repository, IGalleryPageRepository pages, ControlledObjectReader provider)
    {
        var validator = new GalleryJpegUploadValidator(1024, 16, 768);
        app.MapGet(ControlledContentPath + "/{id:guid}", async (Guid id, HttpContext http, HttpCurrentUser current) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var result = await authority.CreateBoundary(repository, pages, current)
                .ReadPrivateMediaAsync(id, provider, 3, http.RequestAborted);
            return result.Status == GalleryMetadataStatus.Success
                ? Results.Bytes(result.Value!, "application/octet-stream", "gallery-media")
                : AsTestResponse(result);
        }).RequireAuthorization();
        app.MapPost(ControlledValidationPath, async (HttpContext http, HttpCurrentUser current) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var result = await authority.CreateBoundary(repository, pages, current)
                .ValidateUploadAsync(http.Request.Body, http.Request.ContentType, validator, http.RequestAborted);
            return result.Status switch
            {
                GalleryMetadataStatus.Success when result.Value is { } upload =>
                    Results.Ok(new { upload.Width, upload.Height }),
                GalleryMetadataStatus.InvalidMediaContent => Results.BadRequest(new { code = "invalid_gallery_upload" }),
                _ => AsTestResponse(result)
            };
        }).RequireAuthorization();
    }

    private sealed class ControlledObjectReader : IGalleryVersionedPrivateObjectReader
    {
        public byte[] Bytes { get; } = [1, 2, 3];
        public int Reads { get; private set; }
        public Func<GalleryVersionedPrivateObject, GalleryVersionedPrivateObject> OnFind { get; set; } = item => item;
        public Func<GalleryVersionedPrivateObject, GalleryVersionedPrivateObject> OnRead { get; set; } = item => item;

        public Task<GalleryVersionedPrivateObject?> FindAuthorizedAsync(GalleryPrivateObjectEvidence binding,
            CancellationToken cancellationToken) => Task.FromResult<GalleryVersionedPrivateObject?>(OnFind(new(binding, "v1")));

        public Task<GalleryPrivateObjectContent?> ReadBoundedAsync(GalleryVersionedPrivateObject expected,
            int maximumBytes, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<GalleryPrivateObjectContent?>(new(OnRead(expected), Bytes));
        }
    }
}
