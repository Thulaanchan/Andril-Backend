using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Defines persistence and private, owner-scoped retrieval for Hobbies Map contributions.
/// </summary>
/// <remarks>
/// Only trusted backend callers may use this contract. The owner profile ID must come
/// from backend authorization for the Hobbies module. Moderation, public visibility
/// and Flight Points eligibility require separate decisions.
/// </remarks>
public interface IMapContributionRepository
{
    /// <summary>
    /// Inserts a valid pending-review contribution.
    /// </summary>
    /// <remarks>
    /// Reject null input and duplicate contribution IDs without overwriting an existing
    /// record. Honor cancellation and propagate persistence failures.
    /// Before insertion, the submission service must verify account and active Hobbies
    /// profile ownership, attraction eligibility, capture-session ownership and
    /// freshness, fresh in-app photo provenance and private media ownership, and
    /// evaluate location evidence using the existing location policy.
    /// A successful insert establishes persistence only. Submission integration must
    /// separately enforce capture-session replay protection and transactional integrity.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="contribution"/> is null.</exception>
    Task AddAsync(
        MapContribution contribution,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves authoritative persisted data by contribution ID and owner Hobbies profile ID.
    /// </summary>
    /// <remarks>
    /// Return null for empty identifiers, a missing contribution, or an ownership
    /// mismatch, without disclosing whether another profile owns the record.
    /// Return an entity that cannot implicitly persist changes. Honor cancellation
    /// and propagate unexpected database failures.
    /// </remarks>
    Task<MapContribution?> FindOwnedAsync(
        Guid contributionId,
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default);
}
