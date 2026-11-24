using System.Text.Json.Serialization;

namespace Andril.Hobbies.Map.Contributions;

// Backend-only handoff. No serialization/transport or durable restoration contract is supplied.
internal sealed class MapSubmissionAttempt(MapSubmissionResult result, MapSubmissionOriginalEvidence? originalEvidence)
{
    [JsonIgnore] internal MapSubmissionResult Result { get; } = result;
    [JsonIgnore] internal MapSubmissionOriginalEvidence? OriginalEvidence { get; } = originalEvidence;

    public override string ToString() => "Submission attempt (internal).";
}
