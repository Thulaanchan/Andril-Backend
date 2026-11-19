using Andril.Hobbies.Map.LocationValidation;

namespace Andril.Hobbies.Map.Contributions;

public enum MapContributionModerationStatus
{
    PendingReview = 1
}

/// <summary>
/// Represents a Hobbies Map contribution awaiting moderation.
/// </summary>
/// <remarks>
/// Before persistence, the trusted submission service must verify account ownership,
/// active Hobbies profile and attraction eligibility, capture-session binding,
/// camera freshness, location evidence, and private photo ownership. Constructing
/// a contribution does not perform those checks or authorize publication or rewards.
/// </remarks>
public sealed class MapContribution
{
    private MapContribution(
        Guid contributionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        Guid captureSessionId,
        string privatePhotoStorageObjectKey,
        string description,
        MapContributionLocationEvidence locationEvidence,
        DateTimeOffset submittedAtUtc)
    {
        ContributionId = contributionId;
        OwnerHobbiesModuleProfileId = ownerHobbiesModuleProfileId;
        AttractionId = attractionId;
        CaptureSessionId = captureSessionId;
        PrivatePhotoStorageObjectKey = privatePhotoStorageObjectKey;
        Description = description;
        LocationEvidence = locationEvidence;
        SubmittedAtUtc = submittedAtUtc.ToUniversalTime();
    }

    public Guid ContributionId { get; }
    public Guid OwnerHobbiesModuleProfileId { get; }
    public Guid AttractionId { get; }
    public Guid CaptureSessionId { get; }
    public string PrivatePhotoStorageObjectKey { get; }
    public string Description { get; }
    public MapContributionLocationEvidence LocationEvidence { get; }
    public DateTimeOffset SubmittedAtUtc { get; }
    public MapContributionModerationStatus ModerationStatus { get; } =
        MapContributionModerationStatus.PendingReview;

    public static MapContribution Create(
        Guid contributionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        Guid captureSessionId,
        string privatePhotoStorageObjectKey,
        string description,
        MapContributionLocationEvidence locationEvidence,
        DateTimeOffset submittedAt)
    {
        if (contributionId == Guid.Empty)
            throw new ArgumentException("A contribution ID is required.", nameof(contributionId));

        if (ownerHobbiesModuleProfileId == Guid.Empty)
            throw new ArgumentException("An owner Hobbies module profile ID is required.", nameof(ownerHobbiesModuleProfileId));

        if (attractionId == Guid.Empty)
            throw new ArgumentException("An attraction ID is required.", nameof(attractionId));

        if (captureSessionId == Guid.Empty)
            throw new ArgumentException("A capture session ID is required.", nameof(captureSessionId));

        ArgumentException.ThrowIfNullOrWhiteSpace(privatePhotoStorageObjectKey);
        var key = privatePhotoStorageObjectKey.AsSpan().Trim();
        if (key.StartsWith("//", StringComparison.Ordinal)
            || key.Contains('\\')
            || Uri.TryCreate(key.ToString(), UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "A private photo storage object key is required.",
                nameof(privatePhotoStorageObjectKey));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(locationEvidence);

        return new MapContribution(
            contributionId,
            ownerHobbiesModuleProfileId,
            attractionId,
            captureSessionId,
            privatePhotoStorageObjectKey,
            description,
            locationEvidence,
            submittedAt);
    }
}
