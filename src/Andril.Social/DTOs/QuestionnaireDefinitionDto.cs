
using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Personality;

namespace Andril.Social.DTOs;

public sealed record QuestionnaireOptionDto(
    string Id,
    string Label
);

public sealed record QuestionnaireQuestionDto(
    string Id,
    string Prompt,
    bool IsRequired,
    IReadOnlyList<QuestionnaireOptionDto> Options
);

public sealed record QuestionnaireDefinitionDto(
    string Version,
    IReadOnlyList<QuestionnaireQuestionDto> Questions
)
{
    public static QuestionnaireDefinitionDto FromDefinition(
        QuestionnaireDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var questions = definition.Questions
            .Select(question =>
            {
                var options = question.Options
                    .Select(option => new QuestionnaireOptionDto(
                        option.Id,
                        option.Label))
                    .ToArray();

                return new QuestionnaireQuestionDto(
                    question.Id,
                    question.Prompt,
                    question.IsRequired,
                    Array.AsReadOnly(options));
            })
            .ToArray();

        return new QuestionnaireDefinitionDto(
            definition.Version,
            Array.AsReadOnly(questions));
    }
}
