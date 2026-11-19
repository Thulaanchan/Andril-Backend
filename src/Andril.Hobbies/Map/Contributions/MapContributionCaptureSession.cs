
namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Records capture session timing and identity binding.
/// The backend caller must supply the authorized profile,
/// canonical attraction reference and trusted timestamps.
/// </summary>
public sealed class MapContributionCaptureSession
{
    private MapContributionCaptureSession(
        Guid sessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        DateTimeOffset startedAtUtc)
    {
        SessionId = sessionId;
        OwnerHobbiesModuleProfileId = ownerHobbiesModuleProfileId;
        AttractionId = attractionId;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
    }

    public Guid SessionId { get; }

    public Guid OwnerHobbiesModuleProfileId { get; }

    public Guid AttractionId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static MapContributionCaptureSession Create(
        Guid sessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        DateTimeOffset startedAt)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException(
                "A session ID is required.",
                nameof(sessionId));

        if (ownerHobbiesModuleProfileId == Guid.Empty)
            throw new ArgumentException(
                "An owner Hobbies module profile ID is required.",
                nameof(ownerHobbiesModuleProfileId));

        if (attractionId == Guid.Empty)
            throw new ArgumentException(
                "An attraction ID is required.",
                nameof(attractionId));

        return new MapContributionCaptureSession(
            sessionId,
            ownerHobbiesModuleProfileId,
            attractionId,
            startedAt);
    }

    public void Complete(DateTimeOffset completedAt)
    {
        if (CompletedAtUtc.HasValue)
            throw new InvalidOperationException(
                "The capture session is already complete.");

        if (completedAt < StartedAtUtc)
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                "Completion cannot precede the session start.");

        CompletedAtUtc = completedAt.ToUniversalTime();
    }
}
