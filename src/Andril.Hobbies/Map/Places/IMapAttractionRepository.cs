using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Hobbies.Map.Places;

/// <summary>
/// Defines persistence and identifier-based retrieval of canonical Map attractions.
/// </summary>
/// <remarks>
/// Operations require trusted backend callers and expose only canonical attraction data.
/// An attraction record alone establishes neither contributor authorization nor
/// contribution eligibility, public marker visibility, camera freshness, GPS authenticity,
/// admin approval or Flight Points eligibility. Those boundaries require separate checks.
/// </remarks>
public interface IMapAttractionRepository
{
    /// <summary>
    /// Persists a new canonical attraction supplied by a trusted backend caller.
    /// </summary>
    /// <remarks>
    /// Reject null input and duplicate attraction identifiers without overwriting
    /// an existing record. Complete successfully only when persistence succeeds.
    /// Propagate persistence failures and honor cancellation.
    /// </remarks>
    Task AddAsync(
        MapAttraction attraction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a canonical attraction and its authoritative stored coordinates by ID.
    /// Returns null for an empty identifier or a missing record.
    /// </summary>
    /// <remarks>
    /// Query authoritative backend records without relying on client-provided attraction
    /// data. Returned entities must not allow implicit persistence through mutation.
    /// Honor cancellation and propagate infrastructure failures rather than treating
    /// them as a missing record. Retrieval exposes no unrelated module data and
    /// does not establish publication or public discovery eligibility.
    /// </remarks>
    Task<MapAttraction?> FindByIdAsync(
        Guid attractionId,
        CancellationToken cancellationToken = default);
}
