using Andril.Common.Accounts.Profiles;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Services;

namespace Andril.API.Authorization.Social;

/// <summary>
/// Requires trusted Social module access before questionnaire operations.
/// Keep unregistered until the M2 security dependencies are approved and operational.
/// </summary>
public sealed class QuestionnaireAccessService
{
    private readonly IPrivateModuleAccess _moduleAccess;
    private readonly QuestionnaireApplicationService _questionnaire;

    public QuestionnaireAccessService(
        IPrivateModuleAccess moduleAccess,
        QuestionnaireApplicationService questionnaire)
    {
        _moduleAccess = moduleAccess ?? throw new ArgumentNullException(nameof(moduleAccess));
        _questionnaire = questionnaire ?? throw new ArgumentNullException(nameof(questionnaire));
    }

    public async Task<QuestionnaireDefinitionDto?> GetDefinitionAsync(
        string? version,
        CancellationToken cancellationToken = default)
    {
        await _moduleAccess.RequireAsync("social", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return _questionnaire.GetDefinition(version);
    }

    public async Task<QuestionnaireValidationResult> ValidateAsync(
        QuestionnaireSubmission? submission,
        CancellationToken cancellationToken = default)
    {
        await _moduleAccess.RequireAsync("social", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return _questionnaire.Validate(submission);
    }
}
