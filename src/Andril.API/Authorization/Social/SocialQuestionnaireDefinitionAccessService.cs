using Andril.Common.Accounts.Profiles;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Services;

namespace Andril.API.Authorization.Social;

public interface ISocialQuestionnaireDefinitionAccessService
{
    Task<QuestionnaireDefinitionDto> GetActiveDefinitionAsync(CancellationToken cancellationToken);
}

public sealed class SocialQuestionnaireDefinitionAccessService(
    IPrivateProfileService profileService,
    QuestionnaireApplicationService questionnaireApplicationService) : ISocialQuestionnaireDefinitionAccessService
{
    private const string SocialModule = "social";
    private const string ActiveVersion = QuestionnaireV1Definition.Version;

    public async Task<QuestionnaireDefinitionDto> GetActiveDefinitionAsync(CancellationToken cancellationToken)
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

            var definition = questionnaireApplicationService.GetDefinition(ActiveVersion);
            if (definition is null)
            {
                throw new InvalidOperationException("Active questionnaire version V1 definition is missing.");
            }

            return await Task.FromResult(definition);
        }, cancellationToken);
    }
}
