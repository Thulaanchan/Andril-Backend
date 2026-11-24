namespace Andril.Hobbies.Map.Places;

internal enum MapAttractionLifecycleOutcome
{
    OutcomeUnknown = 0,
    Changed,
    Unchanged,
    ExistingAuthorized,
    Conflict,
    Denied,
    Unavailable
}

// Only authorized accepted results may include scoped state/version metadata.
// No reviewer identity, permission evidence, audit contents or provider errors are returned.
internal sealed record MapAttractionLifecycleResult(MapAttractionLifecycleOutcome Outcome,
    Guid? RequestId = null, Guid? AttractionId = null, bool? IsActive = null, long? Version = null);
