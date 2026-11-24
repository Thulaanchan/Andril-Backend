namespace Andril.Hobbies.Map.Places;

/// <summary>Offline lifecycle write boundary. No authoritative implementation is supplied.</summary>
/// <remarks>
/// Implementations must independently authorize every call, including retries, using trusted
/// management identity; an active Hobbies actor is not an Admin grant. They must reject missing
/// state without inferring an initial active value, serialize competing changes, and reuse
/// MapAttractionStatePrototype. Expected versions apply to no-ops too. Accepted request receipts
/// bind trusted reviewer, request ID and the exact command, retaining the original result.
/// A future persistent adapter must atomically coordinate state, receipts, required audit and
/// authorization fences under the approved shared lock order. This contract supplies none of
/// that production integration. It must not create a separate competing submission path.
/// Throw cancellation only when the operation is known not to have committed. Once commit may
/// have occurred, return OutcomeUnknown or an evidenced accepted result; never silently retry.
/// </remarks>
internal interface IMapAttractionLifecycleWriter
{
    Task<MapAttractionLifecycleResult> ChangeAsync(MapAttractionLifecycleChange change,
        CancellationToken cancellationToken = default);
}
