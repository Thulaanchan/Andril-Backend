using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Personality;

namespace Andril.Social.Validators;

public static class QuestionnaireValidator
{
    private const string InitialQuestionnaireVersion = "v1";
    private const int InitialQuestionnaireQuestionCount = 12;

    public static QuestionnaireValidationResult ValidateDefinition(
        QuestionnaireDefinition? definition)
    {
        var errors = new List<string>();

        if (definition is null ||
            string.IsNullOrWhiteSpace(definition.Version) ||
            definition.Questions is null ||
            definition.Questions.Count == 0)
        {
            return Fail("INVALID_DEFINITION");
        }

        // Approved SOC-01 v1 rules:
        // - Exactly 12 questions.
        // - Every question is required.
        // - These restrictions apply only to version "v1".
        if (string.Equals(
                definition.Version,
                InitialQuestionnaireVersion,
                StringComparison.Ordinal))
        {
            if (definition.Questions.Count !=
                InitialQuestionnaireQuestionCount)
            {
                errors.Add("V1_INVALID_QUESTION_COUNT");
            }

            if (definition.Questions.Any(
                    question =>
                        question is not null &&
                        !question.IsRequired))
            {
                errors.Add("V1_OPTIONAL_QUESTION_NOT_ALLOWED");
            }
        }

        var questionIds = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var question in definition.Questions)
        {
            if (question is null ||
                string.IsNullOrWhiteSpace(question.Id) ||
                string.IsNullOrWhiteSpace(question.Prompt) ||
                question.Options is null ||
                question.Options.Count == 0)
            {
                errors.Add("INVALID_QUESTION_DEFINITION");
                continue;
            }

            if (!questionIds.Add(question.Id))
            {
                errors.Add("DUPLICATE_QUESTION_ID");
            }

            var optionIds = new HashSet<string>(
                StringComparer.Ordinal);

            foreach (var option in question.Options)
            {
                if (option is null ||
                    string.IsNullOrWhiteSpace(option.Id) ||
                    string.IsNullOrWhiteSpace(option.Label))
                {
                    errors.Add("INVALID_OPTION_DEFINITION");
                    continue;
                }

                if (!optionIds.Add(option.Id))
                {
                    errors.Add("DUPLICATE_OPTION_ID");
                }
            }
        }

        return new QuestionnaireValidationResult(
            errors.Count == 0,
            QuestionnaireParticipationStatus.NotStarted,
            errors);
    }

    public static QuestionnaireValidationResult Validate(
        QuestionnaireDefinition? definition,
        QuestionnaireSubmission? submission)
    {
        var definitionResult = ValidateDefinition(definition);

        if (!definitionResult.IsValid)
        {
            return definitionResult;
        }

        if (submission is null ||
            submission.Answers is null)
        {
            return Fail("INVALID_SUBMISSION");
        }

        if (!string.Equals(
                definition!.Version,
                submission.Version,
                StringComparison.Ordinal))
        {
            return Fail("VERSION_MISMATCH");
        }

        // Existing domain status retained for compatibility.
        // This does not introduce an onboarding skip screen.
        if (submission.Skip)
        {
            return submission.Answers.Count == 0
                ? Success(
                    QuestionnaireParticipationStatus.Skipped)
                : Fail("SKIP_WITH_ANSWERS");
        }

        var questions = definition.Questions.ToDictionary(
            question => question.Id,
            StringComparer.Ordinal);

        var answered = new HashSet<string>(
            StringComparer.Ordinal);

        var errors = new List<string>();

        foreach (var answer in submission.Answers)
        {
            if (answer is null ||
                string.IsNullOrWhiteSpace(answer.QuestionId) ||
                string.IsNullOrWhiteSpace(answer.OptionId))
            {
                errors.Add("INVALID_ANSWER");
                continue;
            }

            if (!answered.Add(answer.QuestionId))
            {
                errors.Add("DUPLICATE_ANSWER");
                continue;
            }

            if (!questions.TryGetValue(
                    answer.QuestionId,
                    out var question))
            {
                errors.Add("UNKNOWN_QUESTION");
                continue;
            }

            if (!question.Options.Any(
                    option => option.Id == answer.OptionId))
            {
                errors.Add("INVALID_OPTION");
            }
        }

        if (errors.Count > 0)
        {
            return new QuestionnaireValidationResult(
                false,
                QuestionnaireParticipationStatus.NotStarted,
                errors);
        }

        if (answered.Count == 0)
        {
            return Success(
                QuestionnaireParticipationStatus.NotStarted);
        }

        bool requiredComplete = definition.Questions
            .Where(question => question.IsRequired)
            .All(question => answered.Contains(question.Id));

        return Success(
            requiredComplete
                ? QuestionnaireParticipationStatus.Completed
                : QuestionnaireParticipationStatus.InProgress);
    }

    private static QuestionnaireValidationResult Success(
        QuestionnaireParticipationStatus status)
    {
        return new QuestionnaireValidationResult(
            true,
            status,
            Array.Empty<string>());
    }

    private static QuestionnaireValidationResult Fail(
        string error)
    {
        return new QuestionnaireValidationResult(
            false,
            QuestionnaireParticipationStatus.NotStarted,
            new[] { error });
    }
}
