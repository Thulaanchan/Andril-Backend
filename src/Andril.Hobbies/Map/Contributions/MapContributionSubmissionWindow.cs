namespace Andril.Hobbies.Map.Contributions;

/// <summary>Uses trusted stored capture timing and a server clock, never reported device times.</summary>
internal static class MapContributionSubmissionWindow
{
    private static readonly TimeSpan MaximumElapsed = TimeSpan.FromMinutes(10);

    internal static bool Allows(MapContributionCaptureSession session, DateTimeOffset now) =>
        session.CompletedAtUtc is { } completed
        && completed >= session.StartedAtUtc
        && now >= completed
        && now - completed <= MaximumElapsed;
}
