using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Andril.API.Configuration;
using Andril.Common.Accounts.Profiles;
using Andril.Common.DTOs;
using Andril.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace Andril.API.Integrations;

public sealed class SupabaseProfilePhotoObjectStorage(HttpClient client, IOptions<ProfilePhotoOptions> options,
    IProfileImageInspector inspector) : IPrivateProfilePhotoObjectStorage
{
    public bool IsConfigured => GetOrigin() is not null;

    public async Task<PhotoObjectOutcome> UploadAsync(StoredProfilePhoto expected, byte[] jpeg, CancellationToken cancellationToken)
    {
        if (!Valid(expected) || !PhotoHashes.Matches(expected, jpeg)) return PhotoObjectOutcome.IntegrityFailure;
        return (await SendAsync(HttpMethod.Post, expected.Address, expected, jpeg, cancellationToken)).Outcome;
    }

    public Task<PhotoObjectRead> ReadAsync(StoredProfilePhoto expected, CancellationToken cancellationToken) =>
        Valid(expected) ? SendAsync(HttpMethod.Get, expected.Address, expected, null, cancellationToken)
            : Task.FromResult(new PhotoObjectRead(PhotoObjectOutcome.IntegrityFailure));

    public async Task<PhotoObjectOutcome> DeleteAsync(PhotoObjectAddress address, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Delete, address, null, null, cancellationToken)).Outcome;

    private Uri? GetOrigin()
    {
        if (!inspector.IsApprovedAndConfigured) return null;
        var settings = options.Value;
        return settings.Enabled && settings.HasApprovedLimits() ? settings.Storage.TrustedOrigin() : null;
    }

    private async Task<PhotoObjectRead> SendAsync(HttpMethod method, PhotoObjectAddress address,
        StoredProfilePhoto? expected, byte[]? upload, CancellationToken ct)
    {
        var origin = GetOrigin();
        if (origin is null || !address.IsValid) return new(PhotoObjectOutcome.PermanentFailure);

        var uri = new Uri(origin, $"/storage/v1/object/{ProfilePhotoOptions.ApprovedBucket}/{address.Key}");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("apikey", options.Value.Storage.SecretKey!);
        if (upload is not null)
        {
            request.Headers.Add("x-upsert", "false");
            request.Headers.CacheControl = new() { NoStore = true };
            request.Content = new ByteArrayContent(upload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return new(await FailureAsync(method, response, deadline.Token));
            if (method == HttpMethod.Post)
                return new(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created
                    ? PhotoObjectOutcome.Success : PhotoObjectOutcome.Ambiguous);
            if (method == HttpMethod.Delete)
                return new(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent
                    ? PhotoObjectOutcome.Success : PhotoObjectOutcome.Ambiguous);

            if (response.StatusCode != HttpStatusCode.OK || expected is null
                || response.Content.Headers.ContentType?.MediaType != "image/jpeg"
                || response.Content.Headers.ContentEncoding.Count != 0
                || (response.Content.Headers.ContentLength is long length && length != expected.Length))
                return new(PhotoObjectOutcome.IntegrityFailure);

            var bytes = await ReadBoundedAsync(response.Content, expected.Length, deadline.Token);
            return PhotoHashes.Matches(expected, bytes)
                ? new(PhotoObjectOutcome.Success, bytes)
                : new(PhotoObjectOutcome.IntegrityFailure);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(method == HttpMethod.Get ? PhotoObjectOutcome.TransientFailure : PhotoObjectOutcome.Ambiguous);
        }
    }

    private static bool Valid(StoredProfilePhoto value) => value.Address.IsValid && value.MediaType == "image/jpeg"
        && value.Length is > 0 and <= ProfilePhotoLimits.UploadBytes && PhotoHashes.IsValid(value.Sha256);

    private static async Task<PhotoObjectOutcome> FailureAsync(HttpMethod method, HttpResponseMessage response, CancellationToken ct)
    {
        int status = (int)response.StatusCode;
        if (status >= 500 || status == 408)
            return method == HttpMethod.Get ? PhotoObjectOutcome.TransientFailure : PhotoObjectOutcome.Ambiguous;
        if (status == 401) return PhotoObjectOutcome.AuthenticationFailure;
        if (status == 403) return PhotoObjectOutcome.PermissionFailure;
        if (status == 429) return PhotoObjectOutcome.RateLimited;
        if (status == 423 || status is >= 300 and < 400) return PhotoObjectOutcome.TransientFailure;

        if (method == HttpMethod.Post && status == 409) return PhotoObjectOutcome.Conflict;
        if (method == HttpMethod.Post && status is 413 or 415) return PhotoObjectOutcome.PermanentFailure;
        if (status is not (400 or 404)) return PhotoObjectOutcome.TransientFailure;

        var body = await ReadBoundedAsync(response.Content, 4096, ct);
        if (body is null or { Length: 0 }) return PhotoObjectOutcome.InvalidResponse;
        try
        {
            using var json = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var error = json.RootElement;
            if (error.ValueKind != JsonValueKind.Object) return PhotoObjectOutcome.InvalidResponse;

            string? code = StringField(error, "code");
            if (code is null)
            {
                // Older duplicate responses can use the structured 'error' field.
                if (status == 400 && method == HttpMethod.Post && !error.TryGetProperty("code", out _)
                    && StringField(error, "error") == "Duplicate") return PhotoObjectOutcome.Conflict;
                return PhotoObjectOutcome.InvalidResponse;
            }

            bool hasInternalStatus = error.TryGetProperty("statusCode", out _);
            bool objectNotFoundStatus = hasInternalStatus
                ? StringField(error, "statusCode") == "404"
                : status == 404;
            if (objectNotFoundStatus && code == "NoSuchKey" && method != HttpMethod.Post)
                return PhotoObjectOutcome.Missing;
            if (objectNotFoundStatus && code == "NoSuchBucket") return PhotoObjectOutcome.MissingBucket;

            if (status == 400 && method == HttpMethod.Post && code is "Duplicate" or "ResourceAlreadyExists" or "KeyAlreadyExists")
                return PhotoObjectOutcome.Conflict;
            if (status == 400 && method == HttpMethod.Post && code is "InvalidMimeType" or "EntityTooLarge")
                return PhotoObjectOutcome.PermanentFailure;

            return status == 400 ? code switch
            {
                "InvalidRequest" => PhotoObjectOutcome.InvalidRequest,
                "InvalidKey" => PhotoObjectOutcome.InvalidObjectPath,
                "InvalidJWT" => PhotoObjectOutcome.AuthenticationFailure,
                "AccessDenied" => PhotoObjectOutcome.PermissionFailure,
                _ => PhotoObjectOutcome.InvalidResponse
            } : PhotoObjectOutcome.InvalidResponse;
        }
        catch (JsonException) { return PhotoObjectOutcome.InvalidResponse; }
    }

    private static string? StringField(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) return null;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximum - buffer.Length + 1)), ct);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximum) return null;
            buffer.Write(chunk, 0, read);
        }
    }
}
