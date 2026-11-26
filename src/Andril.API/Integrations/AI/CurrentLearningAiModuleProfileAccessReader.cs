using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;

namespace Andril.API.Integrations.AI;

/// <summary>
/// Resolves only the current user's Learning profile through Common's authorized release boundary.
/// This adapter does not establish AI privacy consent, admission or provider-dispatch authority.
/// It remains unregistered until those production coordination dependencies are satisfied.
/// </summary>
public sealed class CurrentLearningAiModuleProfileAccessReader(
    ICurrentUser currentUser,
    IAccountRepository accounts,
    IPrivateProfileService profiles,
    IPrivateModuleAccess moduleAccess) : IAiModuleProfileAccessReader
{
    public async Task<Guid?> FindAuthorizedProfileIdAsync(
        Guid accountId, AiModule module, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (module != AiModule.Learning || accountId == Guid.Empty ||
            !currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            return null;

        // Never provision an account, or use the caller's account ID as identity evidence.
        var account = await accounts.FindBySubjectAsync(subject, cancellationToken);
        if (account is null || account.Id != accountId || account.SupabaseUserId != subject)
            return null;

        try
        {
            var profileId = await profiles.WithAuthorizedProfileAsync<Guid?>("learning", async profile =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (profile.Module != "learning" || profile.ProfileId == Guid.Empty)
                    return null;
                await moduleAccess.RequireAsync("learning", cancellationToken);
                return profile.ProfileId;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return currentUser.IsAuthenticated && currentUser.UserId == subject ? profileId : null;
        }
        catch (ProfileAccessDeniedException) { return null; }
        catch (ProfileMissingException) { return null; }
        // Unavailable authority and cancellation propagate to the existing confidential error boundary.
    }
}
