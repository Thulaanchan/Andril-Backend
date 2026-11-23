using Andril.Common.Accounts.Profiles;
using Andril.Social.Services;

namespace Andril.API.Authorization.Social;

public interface ISocialQuestionnaireResponseReadAccessService
{
    Task<SocialQuestionnaireResponseLookupResult> ReadScopedResponseAsync(
        Guid responseId,
        CancellationToken cancellationToken);
}

public sealed class SocialQuestionnaireResponseReadAccessService(
    IPrivateProfileService profileService,
    SocialQuestionnaireResponseScopedLookupService scopedLookupService) : ISocialQuestionnaireResponseReadAccessService
{
    private const string SocialModule = "social";

    public async Task<SocialQuestionnaireResponseLookupResult> ReadScopedResponseAsync(
        Guid responseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await profileService.WithAuthorizedProfileAsync(SocialModule, async profile =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(profile.Module, SocialModule, StringComparison.OrdinalIgnoreCase)
                || profile.ProfileId == Guid.Empty)
            {
                throw new ProfileAccessDeniedException();
            }

            return await scopedLookupService.LookupResponseAsync(profile.ProfileId, responseId, cancellationToken);
        }, cancellationToken);
    }
}
