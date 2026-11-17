
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Hobbies.Map.Places;

/// <summary>
/// Defines the trusted attraction authorization boundary
/// for Andril Map contributions.
/// </summary>
public interface IMapContributionAttractionAccessReader
{
    /// <summary>
    /// Determines whether the specified canonical attraction
    /// is eligible for contributions from the authorized
    /// Hobbies module profile.
    /// </summary>
    /// <remarks>
    /// Implementations must use authoritative backend data.
    ///
    /// Return false when:
    /// - An identifier is empty.
    /// - The attraction does not exist.
    /// - The attraction is not eligible for contributions.
    /// - The contributor is not permitted to contribute.
    ///
    /// The owner profile ID must originate from trusted
    /// backend authorization, never directly from the client.
    ///
    /// Client-provided attraction data or coordinates must
    /// not be treated as authoritative.
    ///
    /// Infrastructure failures must not be converted into
    /// successful authorization.
    ///
    /// Successful authorization does not establish GPS
    /// authenticity, camera freshness, contribution approval
    /// or Flight Points eligibility.
    /// </remarks>
    Task<bool> IsEligibleForContributionAsync(
        Guid attractionId,
        Guid ownerHobbiesModuleProfileId,
        CancellationToken cancellationToken = default);
}
