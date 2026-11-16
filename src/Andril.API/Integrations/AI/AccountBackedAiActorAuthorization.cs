
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Common.Accounts;
using Andril.Shared.Authentication;

namespace Andril.API.Integrations.AI;

/// <summary>
/// Resolves the authenticated Supabase subject into an
/// internal ANDRIL account, then checks access to a
/// specific module profile.
///
/// This adapter must remain server-side.
/// It does not use client-supplied account IDs or
/// profile IDs and does not provision missing accounts.
/// </summary>
public sealed class AccountBackedAiActorAuthorization(
    ICurrentUser currentUser,
    IAccountRepository accounts,
    IAiModuleProfileAccessReader moduleProfiles)
    : IAiActorAuthorization
{
    public async Task<AiAuthorizedActor?> GetAuthorizedActorAsync(
        AiModule module,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Step 1: Accept only defined ANDRIL modules.
        if (!Enum.IsDefined(module))
        {
            return null;
        }

        // Step 2: Require server-verified authentication.
        if (!currentUser.IsAuthenticated ||
            currentUser.UserId is not Guid subject ||
            subject == Guid.Empty)
        {
            return null;
        }

        // Step 3: Look up the existing internal account.
        // Never create accounts during authorization.
        var account = await accounts.FindBySubjectAsync(
            subject,
            cancellationToken);

        // Step 4: Reject missing or inconsistent accounts.
        if (account is null ||
            account.Id == Guid.Empty ||
            account.SupabaseUserId != subject)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 5: Verify access to this module profile.
        // The database-backed reader must check ownership,
        // active state, eligibility and Social 18+ opt-in.
        var moduleProfileId =
            await moduleProfiles.FindAuthorizedProfileIdAsync(
                account.Id,
                module,
                cancellationToken);

        // Step 6: Deny missing or invalid profiles.
        if (moduleProfileId is not Guid verifiedProfileId ||
            verifiedProfileId == Guid.Empty)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Step 7: Return the authorized internal account
        // and module-profile identifiers.
        //
        // UserId here means internal Account.Id,
        // not the external Supabase subject.
        return new AiAuthorizedActor(
            account.Id,
            verifiedProfileId,
            module);
    }
}
