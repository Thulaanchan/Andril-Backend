
using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// Reads the ONE module profile that the given internal ANDRIL account
/// is currently allowed to use. This is a backend/database boundary,
/// not an authorization decision based on Flutter-provided IDs.
/// </summary>
public interface IAiModuleProfileAccessReader
{
    /// <summary>
    /// Returns the active, permitted module-profile ID, or null if the
    /// account does not currently have an authorized profile in this module.
    ///
    /// Implementations MUST:
    /// - Use accountId from the server-resolved Account.Id (NOT a client ID
    ///   or the raw Supabase subject).
    /// - Verify profile ownership, module membership, active state and
    ///   applicable privacy restrictions in the database.
    /// - For Social, also verify 18+ eligibility and explicit Social opt-in.
    /// - Return null for absent, inactive or ineligible profiles. Never
    ///   return a profile belonging to a different account or module.
    /// - Fail closed on database errors; do not invent or default a profile.
    /// - Respect cancellation; recheck permissions at sensitive boundaries.
    ///
    /// Separate AI first-use privacy acknowledgment, quota and conversation
    /// authorization checks are enforced in later integration steps.
    /// </summary>
    Task<Guid?> FindAuthorizedProfileIdAsync(
        Guid accountId,
        AiModule module,
        CancellationToken cancellationToken = default);
}
