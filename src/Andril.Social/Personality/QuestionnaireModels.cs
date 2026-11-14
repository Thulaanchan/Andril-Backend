
namespace Andril.Social.Personality;

public enum QuestionnaireParticipationStatus
{
    NotStarted,
    InProgress,
    Completed,
    Skipped
}

public sealed record QuestionOption(
    string Id,
    string Label
);

public sealed record QuestionDefinition(
    string Id,
    string Prompt,
    bool IsRequired,
    IReadOnlyList<QuestionOption> Options
);

public sealed record QuestionnaireDefinition(
    string Version,
    IReadOnlyList<QuestionDefinition> Questions
);

public sealed record QuestionnaireAnswer(
    string QuestionId,
    string OptionId
);

public sealed record QuestionnaireSubmission(
    string Version,
    IReadOnlyList<QuestionnaireAnswer> Answers,
    bool Skip = false
);

public sealed record QuestionnaireValidationResult(
    bool IsValid,
    QuestionnaireParticipationStatus Status,
    IReadOnlyList<string> Errors
);
