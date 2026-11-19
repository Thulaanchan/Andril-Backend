
using Andril.Social.Validators;

namespace Andril.Social.Personality;

public sealed class QuestionnaireDefinitionCatalog
{
    private readonly Dictionary<string, QuestionnaireDefinition>
        _definitions = new(StringComparer.Ordinal);

    public QuestionnaireDefinitionCatalog(
        IEnumerable<QuestionnaireDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            var validation =
                QuestionnaireValidator.ValidateDefinition(definition);

            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    "Invalid questionnaire definition.",
                    nameof(definitions));
            }

            // Create independent, read-only copies of
            // the question and option collections.
            var questions = definition.Questions
                .Select(question =>
                {
                    var options = question.Options
                        .Select(option => new QuestionOption(
                            option.Id,
                            option.Label))
                        .ToArray();

                    return new QuestionDefinition(
                        question.Id,
                        question.Prompt,
                        question.IsRequired,
                        Array.AsReadOnly(options));
                })
                .ToArray();

            var snapshot = new QuestionnaireDefinition(
                definition.Version,
                Array.AsReadOnly(questions));

            if (!_definitions.TryAdd(
                snapshot.Version, snapshot))
            {
                throw new ArgumentException(
                    "Duplicate questionnaire version.",
                    nameof(definitions));
            }
        }
    }

    public QuestionnaireDefinition? FindByVersion(
        string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return _definitions.GetValueOrDefault(version);
    }
}
