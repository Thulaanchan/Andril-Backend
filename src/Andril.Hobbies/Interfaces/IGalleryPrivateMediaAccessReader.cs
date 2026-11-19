namespace Andril.Hobbies.Interfaces;

/// <summary>
/// A short-lived signed access grant for private Gallery media.
/// Keep the signed URL out of logs and return it only to the authorized requester.
/// </summary>
public sealed class GalleryPrivateMediaAccess(Uri signedUrl, DateTimeOffset expiresAt)
{
    public Uri SignedUrl { get; } = signedUrl ?? throw new ArgumentNullException(nameof(signedUrl));
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAt.ToUniversalTime();
}

/// <summary>
/// Resolves private Gallery media access for the authenticated requester.
/// </summary>
public interface IGalleryPrivateMediaAccessReader
{
    /// <summary>
    /// Returns a short-lived signed access grant only after authoritative backend verification.
    /// </summary>
    /// <remarks>
    /// Resolve an authenticated, active and eligible Hobbies identity through trusted
    /// authorization. Require ownership or an explicitly authorized role. Resolve the
    /// private storage reference from the non-deleted Hobbies Gallery record. Verify that
    /// the referenced private object exists and access is authorized within that module scope.
    /// Use approved signing configuration and expiry policy.
    ///
    /// Return null for empty identifiers, missing records, missing authorization, or
    /// unavailable verification. Denials must not disclose whether another profile's
    /// media or private object exists. Honor cancellation and keep storage keys and
    /// provider credentials inside the trusted backend.
    /// </remarks>
    Task<GalleryPrivateMediaAccess?> FindAuthorizedAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default);
}
