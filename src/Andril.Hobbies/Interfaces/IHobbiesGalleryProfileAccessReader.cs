
namespace Andril.Hobbies.Interfaces;

/// <summary>
/// Reads authoritative Hobbies module-profile ownership
/// from persisted backend data.
///
/// The accountId must be the internal persisted Account ID,
/// not the external authentication subject or a client ID.
/// </summary>
public interface IHobbiesGalleryProfileAccessReader
{
    /// <summary>
    /// Returns the active Hobbies module profile ID only when
    /// it belongs to the specified persisted account.
    ///
    /// Implementations must:
    /// - Query trusted persisted profile data.
    /// - Verify account ownership.
    /// - Require the Hobbies module.
    /// - Require an active, eligible profile.
    /// - Return null when access cannot be verified.
    /// - Never trust client-supplied ownership claims.
    /// </summary>
    Task<Guid?> FindActiveOwnedHobbiesProfileIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);
}
