using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Andril.Hobbies.Map.Contributions;

// The decoder must validate actual JPEG content under the approved Map policy.
internal interface IMapJpegInspector
{
    Task<bool> IsValidJpegAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

internal sealed class SupabaseMapPrivateMediaStorage(
    Uri origin, string serviceKey, IMapJpegInspector inspector, TimeProvider clock,
    HttpClient? client = null) : IMapPrivateMediaStorage, IMapPrivateMediaObjectWriter
{
    internal const string Bucket = "hobbies-map-private";
    internal const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly HttpClient PrivateClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient transport = client ?? PrivateClient;
    private readonly Uri trustedOrigin = ValidateOrigin(origin);

    internal SupabaseMapPrivateMediaStorage(Uri origin, string serviceKey, TimeProvider clock)
        : this(origin, serviceKey, new MapJpegInspector(), clock)
    {
    }

    public Task<MapUploadGrant?> AuthorizeCreateAsync(MapMediaReservation reservation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MapUploadGrant?>(reservation.MediaId != Guid.Empty
            && clock.GetUtcNow() < reservation.UploadExpiresAt
            ? new(reservation.MediaId, reservation.MediaId.ToString("N")) : null);
    }

    public async Task<bool> CreateAsync(
        MapMediaReservation reservation, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reservation.MediaId == Guid.Empty || bytes.Length is < 1 or > MaximumBytes
            || clock.GetUtcNow() >= reservation.UploadExpiresAt)
            return false;
        var content = bytes.ToArray();
        if (!await inspector.IsValidJpegAsync(content, cancellationToken))
            return false;
        var remaining = reservation.UploadExpiresAt - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            using var request = CreateRequest(HttpMethod.Post, reservation.MediaId);
            request.Headers.Add("x-upsert", "false");
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                return clock.GetUtcNow() < reservation.UploadExpiresAt;

            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.BadRequest))
                return false;
            var existing = await InspectAsync(reservation, deadline.Token);
            return existing?.Sha256 == Convert.ToHexString(SHA256.HashData(content))
                && clock.GetUtcNow() < reservation.UploadExpiresAt;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public async Task<VerifiedMapMedia?> InspectAsync(MapMediaReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.MediaId == Guid.Empty)
            return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(InspectionTimeout);
        try
        {
            using var request = CreateRequest(HttpMethod.Get, reservation.MediaId);
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.ETag is not { IsWeak: false, Tag.Length: > 2 } version
                || response.Content.Headers.ContentLength is <= 0 or > MaximumBytes
                || response.Content.Headers.ContentEncoding.Count != 0)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + read > MaximumBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            return bytes.Length > 0
                && (response.Content.Headers.ContentLength is not long length || length == bytes.Length)
                && await inspector.IsValidJpegAsync(bytes, deadline.Token)
                ? new(reservation.MediaId, ObjectKey(reservation.MediaId), version.Tag,
                    Convert.ToHexString(SHA256.HashData(bytes.Span)))
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    public async Task<bool> DeleteOrphanAsync(MapMediaReservation reservation, CancellationToken cancellationToken)
    {
        if (reservation.MediaId == Guid.Empty)
            return false;
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, reservation.MediaId);
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Guid id)
    {
        var route = method == HttpMethod.Get ? "object/authenticated" : "object";
        var request = new HttpRequestMessage(method, new Uri(trustedOrigin, $"/storage/v1/{route}/{Bucket}/{ObjectKey(id)}"));
        request.Headers.Add("apikey", serviceKey);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static string ObjectKey(Guid id) => $"map/{id:N}.jpg";

    private static Uri ValidateOrigin(Uri origin)
    {
        const string suffix = ".supabase.co";
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort
            || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0
            || origin.Fragment.Length != 0 || !origin.IdnHost.EndsWith(suffix, StringComparison.Ordinal)
            || origin.IdnHost[..^suffix.Length] is not { Length: > 0 and <= 63 } project
            || !project.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("An approved Supabase project origin is required.", nameof(origin));
        return origin;
    }
}
