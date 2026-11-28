using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Andril.API.Authentication;
using Andril.API.Authorization.Hobbies;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    private const string ProposedGalleryPath = "/api/v1/hobbies/gallery/media";
    private static readonly GalleryPageBounds TestMetadataBounds = new(1, 2, 3);

    [Fact]
    public void MetadataCursor_AuthenticatesVersionOwnerModuleQueryAndOrderingWithinBounds()
    {
        var protection = new EphemeralDataProtectionProvider();
        var codec = new GalleryMetadataCursor(protection);
        var actor = new GalleryActor(Owner, "Hobbies", true);
        var expected = new GalleryPagePosition(Owner, "Hobbies", CreatedAt, Newer);
        var encoded = codec.Encode(actor, expected);
        Assert.True(codec.TryDecode(actor, encoded, out var decoded));
        Assert.Equal(expected, decoded);

        var bytes = WebEncoders.Base64UrlDecode(encoded);
        bytes[^1] ^= 1;
        Assert.False(codec.TryDecode(actor, WebEncoders.Base64UrlEncode(bytes), out _));
        Assert.False(codec.TryDecode(actor with { ModuleProfileId = OtherOwner }, encoded, out _));
        Assert.False(codec.TryDecode(actor with { Module = "Learning" }, encoded, out _));
        Assert.False(codec.TryDecode(actor with { IsActive = false }, encoded, out _));
        Assert.False(new GalleryMetadataCursor(new EphemeralDataProtectionProvider()).TryDecode(actor, encoded, out _));
        foreach (var invalid in new[] { "", "a", "ab=", "not+url", new string('a', GalleryMetadataCursor.MaximumEncodedLength + 1) })
            Assert.False(codec.TryDecode(actor, invalid, out _));

        IDataProtector ProtectFor(string query, string sort) => protection.CreateProtector("Andril.Gallery.Cursor",
            "v1", "Hobbies", Owner.ToString("N"), query, sort);
        var valid = ProtectFor("owned-metadata", "created-desc.id-asc");
        var payload = valid.Unprotect(WebEncoders.Base64UrlDecode(encoded));
        Assert.False(codec.TryDecode(actor, WebEncoders.Base64UrlEncode(
            ProtectFor("other-query", "created-desc.id-asc").Protect(payload)), out _));
        Assert.False(codec.TryDecode(actor, WebEncoders.Base64UrlEncode(
            ProtectFor("owned-metadata", "created-asc.id-asc").Protect(payload)), out _));
        payload[0] = 2;
        Assert.False(codec.TryDecode(actor, WebEncoders.Base64UrlEncode(valid.Protect(payload)), out _));
        Assert.False(codec.TryDecode(actor, WebEncoders.Base64UrlEncode(valid.Protect(new byte[26])), out _));
    }

    [Fact]
    public async Task MetadataBoundary_RechecksBeforeQueryAndWithholdsRevokedResponses()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository { Media = NewPrivateMedia() };
        var boundary = authority.CreateBoundary(repository, repository);
        var protection = new EphemeralDataProtectionProvider();
        var cursor = new GalleryMetadataCursor(protection);
        var valid = cursor.Encode(new GalleryActor(Owner, "Hobbies", true),
            new GalleryPagePosition(Owner, "Hobbies", CreatedAt, Newer));
        var revoking = new GalleryMetadataCursor(new ProbeDataProtectionProvider(protection, () => authority.Allowed = false));
        var preRead = await boundary.ReadMetadataPageAsync(null, valid, TestMetadataBounds, revoking);
        Assert.Equal(GalleryMetadataStatus.Denied, preRead.Status);
        Assert.Equal(0, repository.PageReads);

        authority.Allowed = true;
        repository.DuringPage = () => authority.ProfileId = OtherOwner;
        var page = await boundary.ReadMetadataPageAsync(null, null, TestMetadataBounds, cursor);
        Assert.Equal(GalleryMetadataStatus.Denied, page.Status);
        Assert.Null(page.Value);
        Assert.Equal(1, repository.PageReads);

        authority.ProfileId = Owner;
        repository.DuringDetail = () => authority.AccountUnavailable = true;
        var detail = await boundary.ReadMetadataDetailAsync(Newer.ToString("D"));
        Assert.Equal(GalleryMetadataStatus.DependencyUnavailable, detail.Status);
        Assert.Null(detail.Value);
        Assert.Equal(1, repository.DetailReads);

        authority.AccountUnavailable = false;
        using var cancellation = new CancellationTokenSource();
        repository.DuringDetail = cancellation.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => boundary.ReadMetadataDetailAsync(Newer.ToString("D"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task MetadataHttp_ControlledRoutesValidateInputsAndPreserveConfidentiality()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository { Media = NewPrivateMedia() };
        var protection = new EphemeralDataProtectionProvider();
        await using var app = await StartMetadataHostAsync(authority, repository, repository, protection);
        using var client = app.GetTestClient();
        await AssertMetadataErrorAsync(client, ProposedGalleryPath, HttpStatusCode.Unauthorized, "authentication_required");
        Assert.Equal(0, repository.PageReads);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic");

        var first = await client.GetFromJsonAsync<GalleryMetadataPage>(ProposedGalleryPath);
        Assert.NotNull(first);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(2, repository.LastPageSize);
        Assert.Equal(GalleryReadOperation.MetadataList, authority.LastOperation);
        foreach (var accepted in new[] { "1", "3" })
        {
            using var response = await client.GetAsync(ProposedGalleryPath + "?pageSize=" + accepted);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(int.Parse(accepted, System.Globalization.CultureInfo.InvariantCulture), repository.LastPageSize);
        }
        foreach (var invalid in new[] { "", "0", "-1", "4", "bad", "2147483648", "1%00", "1&pageSize=2" })
            await AssertMetadataErrorAsync(client, ProposedGalleryPath + "?pageSize=" + invalid,
                HttpStatusCode.BadRequest, "invalid_gallery_page_size");
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "?cursor=a&cursor=b",
            HttpStatusCode.BadRequest, "invalid_gallery_cursor");
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "?cursor=bad%2Btoken",
            HttpStatusCode.BadRequest, "invalid_gallery_cursor");
        var foreignModule = protection.CreateProtector("Andril.Gallery.Cursor", "v1", "Learning",
            Owner.ToString("N"), "owned-metadata", "created-desc.id-asc");
        await AssertMetadataErrorAsync(client,
            ProposedGalleryPath + "?cursor=" + WebEncoders.Base64UrlEncode(foreignModule.Protect(new byte[25])),
            HttpStatusCode.BadRequest, "invalid_gallery_cursor");
        authority.ProfileId = OtherOwner;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "?cursor=" + first.NextCursor,
            HttpStatusCode.BadRequest, "invalid_gallery_cursor");
        authority.ProfileId = null;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "?cursor=" + first.NextCursor,
            HttpStatusCode.Forbidden, "gallery_access_denied");
        authority.ProfileId = Owner;
        authority.AccountMissing = true;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath, HttpStatusCode.Forbidden, "gallery_access_denied");
        authority.AccountMissing = false;
        authority.Allowed = false;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath, HttpStatusCode.Forbidden, "gallery_access_denied");
        authority.Allowed = true;
        repository.FinalPage = true;
        var last = await client.GetFromJsonAsync<GalleryMetadataPage>(ProposedGalleryPath + "?cursor=" + first.NextCursor);
        Assert.NotNull(last);
        Assert.Empty(last.Items);
        Assert.Null(last.NextCursor);

        using var detail = await client.GetAsync(ProposedGalleryPath + "/" + Newer);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var json = await detail.Content.ReadAsStringAsync();
        Assert.Contains(Newer.ToString(), json);
        Assert.DoesNotContain(Owner.ToString(), json);
        Assert.DoesNotContain("storageObjectKey", json);
        Assert.DoesNotContain("synthetic/private", json);
        Assert.Equal(GalleryReadOperation.MetadataDetail, authority.LastOperation);
        repository.DuringDetail = () => authority.Allowed = false;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/" + Newer,
            HttpStatusCode.Forbidden, "gallery_access_denied");
        authority.Allowed = true;
        repository.DuringDetail = null;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/invalid", HttpStatusCode.BadRequest, "invalid_gallery_media_id");
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/" + Older, HttpStatusCode.NotFound, "gallery_media_unavailable");
        var media = NewPrivateMedia();
        media.IsDeleted = true;
        repository.Media = media;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/" + Newer, HttpStatusCode.NotFound, "gallery_media_unavailable");
        repository.DetailUnavailable = true;
        await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/" + Newer, HttpStatusCode.ServiceUnavailable, "gallery_unavailable");
    }

    [Fact]
    public async Task MetadataBoundary_ProtectionDependencyFailureReturnsOnlyUnavailable()
    {
        var authority = new TestGalleryAuthorization();
        var repository = new TestGalleryReadRepository();
        var codec = new GalleryMetadataCursor(new ProbeDataProtectionProvider(new EphemeralDataProtectionProvider(),
            () => throw new InvalidOperationException("synthetic key-store details")));
        var response = await authority.CreateBoundary(repository, repository)
            .ReadMetadataPageAsync(null, null, TestMetadataBounds, codec);
        Assert.Equal(GalleryMetadataStatus.DependencyUnavailable, response.Status);
        Assert.Null(response.Value);
    }

    [MapCapturePostgresFact]
    public Task MetadataHttp_PostgresScopesDetailAndTraversesProtectedContinuation() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [.. KeysetRows(), KeysetMedia(10, CreatedAt, OtherOwner),
            KeysetMedia(11, CreatedAt, module: "Learning"), KeysetMedia(12, CreatedAt, deleted: true)]);
        await using var context = CreateContext(connection);
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var authority = new TestGalleryAuthorization();
        await using var app = await StartMetadataHostAsync(authority, repository, repository, new EphemeralDataProtectionProvider());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Synthetic");
        var first = await client.GetFromJsonAsync<GalleryMetadataPage>(ProposedGalleryPath);
        Assert.NotNull(first);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(new[] { KeysetId(1), KeysetId(2) }, first.Items.Select(item => item.Id));
        var continuation = ProposedGalleryPath + "?cursor=" + first.NextCursor;
        authority.Allowed = false;
        await AssertMetadataErrorAsync(client, continuation, HttpStatusCode.Forbidden, "gallery_access_denied");
        authority.Allowed = true;
        var second = await client.GetFromJsonAsync<GalleryMetadataPage>(continuation);
        Assert.NotNull(second);
        Assert.Equal(new[] { KeysetId(3), KeysetId(4) }, second.Items.Select(item => item.Id));
        Assert.NotNull(second.NextCursor);
        var final = await client.GetFromJsonAsync<GalleryMetadataPage>(ProposedGalleryPath + "?cursor=" + second.NextCursor);
        Assert.NotNull(final);
        Assert.Equal(new[] { KeysetId(5), KeysetId(6) }, final.Items.Select(item => item.Id));
        Assert.Null(final.NextCursor);
        var owned = await client.GetFromJsonAsync<GalleryPhotoDto>(ProposedGalleryPath + "/" + KeysetId(1));
        Assert.Equal(KeysetId(1), owned?.Id);
        foreach (var unavailable in new[] { 10, 11, 12, 99 })
            await AssertMetadataErrorAsync(client, ProposedGalleryPath + "/" + KeysetId(unavailable),
                HttpStatusCode.NotFound, "gallery_media_unavailable");
    });

    // Proposed route and status mappings exist only in this isolated test host.
    private static async Task<WebApplication> StartMetadataHostAsync(TestGalleryAuthorization authority,
        IGalleryRepository repository, IGalleryPageRepository pages, IDataProtectionProvider protection,
        Action<WebApplication>? mapTestRoutes = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(authority);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpCurrentUser>();
        builder.Services.AddAuthentication("Synthetic")
            .AddScheme<AuthenticationSchemeOptions, GalleryTestAuthentication>("Synthetic", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var codec = new GalleryMetadataCursor(protection);
        app.MapGet(ProposedGalleryPath, async (HttpContext http, HttpCurrentUser current) =>
        {
            // Force repeated parameters through typed validation after authorization.
            var query = http.Request.Query;
            var size = query.TryGetValue("pageSize", out var sizes) ? sizes.ToString() : null;
            var cursor = query.TryGetValue("cursor", out var cursors) ? cursors.ToString() : null;
            return AsTestResponse(await authority.CreateBoundary(repository, pages, current)
                .ReadMetadataPageAsync(size, cursor, TestMetadataBounds, codec, http.RequestAborted));
        }).RequireAuthorization();
        app.MapGet(ProposedGalleryPath + "/{mediaId}", async (string mediaId, HttpContext http, HttpCurrentUser current) =>
            AsTestResponse(await authority.CreateBoundary(repository, pages, current)
                .ReadMetadataDetailAsync(mediaId, http.RequestAborted))).RequireAuthorization();
        mapTestRoutes?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    private static IResult AsTestResponse<T>(GalleryMetadataResult<T> result) where T : class => result.Status switch
    {
        GalleryMetadataStatus.Success => Results.Ok(result.Value),
        GalleryMetadataStatus.Denied => Results.Json(new { code = "gallery_access_denied" }, statusCode: 403),
        GalleryMetadataStatus.InvalidPageSize => Results.BadRequest(new { code = "invalid_gallery_page_size" }),
        GalleryMetadataStatus.InvalidCursor => Results.BadRequest(new { code = "invalid_gallery_cursor" }),
        GalleryMetadataStatus.InvalidMediaId => Results.BadRequest(new { code = "invalid_gallery_media_id" }),
        GalleryMetadataStatus.MediaUnavailable => Results.NotFound(new { code = "gallery_media_unavailable" }),
        _ => Results.Json(new { code = "gallery_unavailable" }, statusCode: 503)
    };

    private static async Task AssertMetadataErrorAsync(HttpClient client, string path, HttpStatusCode status, string code)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("{\"code\":\"" + code + "\"}", await response.Content.ReadAsStringAsync());
    }

    private sealed class GalleryTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, TestGalleryAuthorization authority)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.Authorization != "Synthetic" ? AuthenticateResult.NoResult() :
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new("sub", authority.UserId.ToString()!), new("role", "authenticated"), new("is_anonymous", "false")],
                    Scheme.Name)), Scheme.Name)));

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Synthetic";
            return Response.WriteAsJsonAsync(new { code = "authentication_required" });
        }
    }

    private sealed class ProbeDataProtectionProvider(IDataProtectionProvider inner, Action onCreate) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose)
        {
            onCreate();
            return inner.CreateProtector(purpose);
        }
    }
}
