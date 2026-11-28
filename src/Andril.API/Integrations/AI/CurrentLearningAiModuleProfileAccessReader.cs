using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Andril.API.Integrations.Learning;

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

        var value=await new CurrentLearningOwnerBoundary(currentUser,accounts,profiles,moduleAccess)
            .WithCurrentOwnerAsync<ProfileResult>((owner,_)=>Task.FromResult<ProfileResult?>(new ProfileResult(owner.ProfileId)),cancellationToken,accountId);
        return value?.Id;
    }
    private sealed record ProfileResult(Guid Id);
}
