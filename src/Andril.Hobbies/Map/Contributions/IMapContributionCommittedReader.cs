namespace Andril.Hobbies.Map.Contributions;

/// <summary>Internal read-only access to independently committed Hobbies submissions.</summary>
/// <remarks>
/// The owner must come from trusted Hobbies authorization, never a client ownership claim.
/// Implementations query all three identifiers on an independently owned connection and
/// READ COMMITTED read-only transaction, without ambient enlistment or write retries.
/// A missing row is a snapshot observation, not proof that an uncertain write failed.
/// Returned contribution evidence stays inside the backend; this port establishes neither
/// original-request equivalence nor permission to publish media or issue rewards.
/// </remarks>
internal interface IMapContributionCommittedReader
{
    Task<MapContribution?> FindOwnedByCaptureSessionAsync(
        Guid captureSessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        CancellationToken cancellationToken = default);
}
