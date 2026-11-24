using System.Text.Json.Serialization;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>Immutable in-process retention of an original trusted preparation, not a durable receipt.</summary>
/// <remarks>
/// Exact comparison follows user-reported M5 approval for MAP-34C. Generated contribution
/// identity and submission time are not retry payload fields. This checks stored request
/// metadata only, not authorization, commit, moderation, media contents or reward eligibility.
/// Never reconstruct this capability from client claims or refresh it with a new preflight.
/// </remarks>
internal sealed class MapSubmissionOriginalEvidence
{
    private readonly MapContribution _original;

    private MapSubmissionOriginalEvidence(MapContribution original) => _original = original;

    [JsonIgnore] internal Guid OwnerHobbiesModuleProfileId => _original.OwnerHobbiesModuleProfileId;
    [JsonIgnore] internal Guid AttractionId => _original.AttractionId;
    [JsonIgnore] internal Guid CaptureSessionId => _original.CaptureSessionId;

    internal static MapSubmissionOriginalEvidence Capture(MapContributionSubmissionPreflight.PreparedSubmission prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        // Contribution, strings and location evidence expose no mutators. Retain the original
        // object so later attempts cannot overwrite the evidence of this attempt.
        return new(prepared.Contribution);
    }

    internal bool MatchesRequest(MapContribution? candidate) => candidate is not null
        && candidate.OwnerHobbiesModuleProfileId == _original.OwnerHobbiesModuleProfileId
        && candidate.AttractionId == _original.AttractionId
        && candidate.CaptureSessionId == _original.CaptureSessionId
        && string.Equals(candidate.PrivatePhotoStorageObjectKey, _original.PrivatePhotoStorageObjectKey, StringComparison.Ordinal)
        && string.Equals(candidate.Description, _original.Description, StringComparison.Ordinal)
        // The immutable record compares all coordinates, reported accuracy and capture times
        // by value. DateTimeOffset equality compares instants, without arbitrary time tolerance.
        && candidate.LocationEvidence == _original.LocationEvidence;

    public override string ToString() => "Original submission evidence (internal).";
}
