using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Validators;

namespace Andril.Social.Services;

/// <summary>
/// Retrieves definitions and validates submissions for exact server-owned questionnaire versions.
/// Questionnaire completion does not authorize Social access.
/// </summary>
public sealed class QuestionnaireApplicationService
{
    private readonly QuestionnaireDefinitionCatalog _catalog;

    public QuestionnaireApplicationService(
        QuestionnaireDefinitionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public QuestionnaireDefinitionDto? GetDefinition(string? version)
    {
        var definition = _catalog.FindByVersion(version);

        return definition is null
            ? null
            : QuestionnaireDefinitionDto.FromDefinition(definition);
    }

    public QuestionnaireValidationResult Validate(
        QuestionnaireSubmission? submission)
    {
        if (submission is null)
        {
            return Reject("INVALID_SUBMISSION");
        }

        var definition = _catalog.FindByVersion(submission.Version);

        if (definition is null)
        {
            return Reject("VERSION_MISMATCH");
        }

        return QuestionnaireValidator.Validate(definition, submission);
    }

    private static QuestionnaireValidationResult Reject(string error) =>
        new(false, QuestionnaireParticipationStatus.NotStarted, new[] { error });
}
