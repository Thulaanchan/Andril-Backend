namespace Andril.Hobbies.Map.Contributions;

public enum MapContributionPhotoVerificationResult
{
    Denied = 0,
    Verified = 1
}

/// <summary>
/// Verifies one private in-app camera photo for a Map contribution from authoritative backend evidence.
/// </summary>
/// <remarks>
/// The caller supplies a Hobbies profile ID resolved through trusted backend authorization,
/// along with the intended attraction and capture-session IDs. Treat the object key as an
/// untrusted reference. Never disclose private storage details or resolve public URLs.
/// </remarks>
public interface IMapContributionPhotoEvidenceReader
{
    /// <summary>
    /// Returns Verified only when authoritative media infrastructure confirms the exact object
    /// exists in private storage, belongs to the specified Hobbies profile within the Hobbies
    /// module, is bound to the specified attraction and capture session, and has trusted
    /// evidence of fresh in-app camera capture and eligibility for this submission.
    /// </summary>
    /// <remarks>
    /// Honor cancellation. Deny when evidence is missing, inconsistent or unavailable.
    /// Gallery uploads are ineligible. An object key, upload timestamp, EXIF metadata,
    /// or client assertion alone is insufficient for verification.
    ///
    /// Verification does not consume a capture session or grant approval, visibility or
    /// rewards. After all submission checks pass, future integration must atomically
    /// persist the pending contribution and consume capture-session eligibility to
    /// prevent concurrent replay and support safe retries after failures.
    /// </remarks>
    Task<MapContributionPhotoVerificationResult> VerifyAsync(
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        Guid captureSessionId,
        string? privatePhotoStorageObjectKey,
        CancellationToken cancellationToken = default);
}
