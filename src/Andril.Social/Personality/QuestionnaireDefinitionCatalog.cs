
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

            if (!_definitions.TryAdd(definition.Version, definition))
            {
                throw new ArgumentException(
                    "Duplicate questionnaire version.",
                    nameof(definitions));
            }
        }
    }

    public QuestionnaireDefinition? FindByVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return _definitions.GetValueOrDefault(version);
    }
}
