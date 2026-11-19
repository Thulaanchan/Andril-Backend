using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Andril.Common.Authentication;

namespace Andril.API.Authentication;

public enum SupabaseUserVerification { Confirmed, Unconfirmed, Unauthorized, Unavailable }

public interface ISupabaseUserVerifier
{
    Task<SupabaseUserVerification> VerifyAsync(Guid subject, string accessToken, CancellationToken cancellationToken);
}

public sealed class SupabaseUserVerifier(HttpClient http, IConfiguration configuration, TimeProvider clock) : ISupabaseUserVerifier
{
    public async Task<SupabaseUserVerification> VerifyAsync(Guid subject, string accessToken, CancellationToken cancellationToken)
    {
        if (subject == Guid.Empty || string.IsNullOrWhiteSpace(accessToken)) return SupabaseUserVerification.Unauthorized;
        var settings = configuration.GetSection("SupabaseAuth").Get<SupabaseAuthOptions>() ?? new();
        var publicKey = configuration["SupabaseIdentity:PublishableKey"];
        // Only the non-secret publishable key is supported; no admin/service-role key is needed.
        if (!settings.TryGetIssuer(out var issuer) || publicKey is null
            || !publicKey.StartsWith("sb_publishable_", StringComparison.Ordinal) || publicKey.Length <= 15
            || publicKey.Any(char.IsWhiteSpace) || publicKey.Any(char.IsControl))
            return SupabaseUserVerification.Unavailable;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, issuer + "/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("apikey", publicKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return SupabaseUserVerification.Unauthorized;
            if (response.StatusCode != HttpStatusCode.OK) return SupabaseUserVerification.Unavailable;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken),
                new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 });
            var user = document.RootElement;
            if (user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var actual) || actual != subject)
                return SupabaseUserVerification.Unauthorized;
            if (!user.TryGetProperty("is_anonymous", out var anonymous) || anonymous.ValueKind != JsonValueKind.False
                || !user.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "authenticated"
                || !user.TryGetProperty("aud", out var audience) || audience.ValueKind != JsonValueKind.String || audience.GetString() != "authenticated")
                return SupabaseUserVerification.Unauthorized;
            var hasEmail = user.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(email.GetString());
            DateTimeOffset? confirmedAt = null;
            if (user.TryGetProperty("email_confirmed_at", out var timestamp) && timestamp.ValueKind != JsonValueKind.Null)
            {
                if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var parsed))
                    return SupabaseUserVerification.Unavailable;
                confirmedAt = parsed;
            }
            return new ConfirmedUserEvidence(actual, false, hasEmail, confirmedAt).IsConfirmedFor(subject, clock.GetUtcNow())
                ? SupabaseUserVerification.Confirmed : SupabaseUserVerification.Unconfirmed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return SupabaseUserVerification.Unavailable; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            // Never log user payloads, tokens, HTTP headers or upstream exception details.
            return SupabaseUserVerification.Unavailable;
        }
    }
}
