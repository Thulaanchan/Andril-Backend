
using Andril.AI.ChatBox;

namespace Andril.AI.Interfaces;

/// <summary>
/// Trusted backend-only authorization boundary.
///
/// Implement using validated Supabase identity PLUS
/// database-verified module-profile ownership
/// and active eligibility.
///
/// For Social, verify the approved 18+
/// and opt-in conditions.
///
/// Never implement using unverified client-provided
/// account or module-profile IDs.
///
/// Return null for denial, unknown profiles,
/// or disabled profiles (fail closed).
/// </summary>
public interface IAiActorAuthorization
{
    Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
        AiModule module,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an account authorized to use AI
/// through one specific module profile.
/// </summary>
public sealed record AiAuthorizedActor(
    Guid UserId,
    Guid ModuleProfileId,
    AiModule Module);
