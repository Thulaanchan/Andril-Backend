using Andril.Common.Accounts.Profiles;
using Andril.Social.Personality;
using Andril.Social.Services;

namespace Andril.API.Authorization.Social;

public interface ISocialQuestionnaireSubmissionValidationService
{
    Task<QuestionnaireValidationResult> ValidateSubmissionAsync(
        QuestionnaireSubmission? submission,
        CancellationToken cancellationToken);
}

public sealed class SocialQuestionnaireSubmissionValidationService(
    IPrivateProfileService profileService,
    QuestionnaireApplicationService questionnaireApplicationService) : ISocialQuestionnaireSubmissionValidationService
{
    private const string SocialModule = "social";

    public async Task<QuestionnaireValidationResult> ValidateSubmissionAsync(
        QuestionnaireSubmission? submission,
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

            var result = questionnaireApplicationService.Validate(submission);
            return await Task.FromResult(result);
        }, cancellationToken);
    }
}
