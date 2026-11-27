using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Interfaces;
using Andril.Shared.Authentication;

namespace Andril.API.Integrations.Learning;

/// <summary>Concrete shared adapter over actual M2/Common subject, account and authorized profile contracts.</summary>
public sealed class CurrentLearningOwnerBoundary(ICurrentUser currentUser,IAccountRepository accounts,
    IPrivateProfileService profiles,IPrivateModuleAccess moduleAccess):ICurrentLearningOwnerBoundary
{
    public async Task<T?> WithCurrentOwnerAsync<T>(Func<CurrentLearningOwner,CancellationToken,Task<T?>> read,
        CancellationToken cancellationToken=default,Guid? expectedAccountId=null) where T:class
    {
        ArgumentNullException.ThrowIfNull(read);cancellationToken.ThrowIfCancellationRequested();
        if(!currentUser.IsAuthenticated||currentUser.UserId is not Guid subject||subject==Guid.Empty)return null;
        var account=await accounts.FindBySubjectAsync(subject,cancellationToken);
        if(account is null||account.Id==Guid.Empty||account.SupabaseUserId!=subject||(expectedAccountId is not null&&account.Id!=expectedAccountId))return null;
        try
        {
            var result=await profiles.WithAuthorizedProfileAsync<T?>("learning",async profile=>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(profile.Module!="learning"||profile.ProfileId==Guid.Empty)return null;
                await moduleAccess.RequireAsync("learning",cancellationToken);
                var value=await read(new(account.Id,profile.ProfileId),cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return currentUser.IsAuthenticated&&currentUser.UserId==subject?value:null;
            },cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return currentUser.IsAuthenticated&&currentUser.UserId==subject?result:null;
        }
        catch(ProfileAccessDeniedException){return null;}
        catch(ProfileMissingException){return null;}
        // Infrastructure/cancellation propagate to existing confidential feature boundaries.
    }
}
