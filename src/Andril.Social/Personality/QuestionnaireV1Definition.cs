namespace Andril.Social.Personality;

/// <summary>
/// Authoritative built-in definition provider for the SOC-01 Personality Questionnaire V1.
/// Provides 12 mandatory questions evaluated against a standard 5-point Likert scale.
/// Questionnaire completion is optional for users and associated strictly with the private Social profile.
/// </summary>
public static class QuestionnaireV1Definition
{
    public const string Version = "v1";

    private static readonly (string Id, string Prompt)[] QuestionsData =
    [
        ("q01", "I enjoy meeting new people and starting conversations."),
        ("q02", "I prefer a few meaningful connections over many casual ones."),
        ("q03", "I usually respond to messages within a reasonable time."),
        ("q04", "I need regular personal space even when I feel close to someone."),
        ("q05", "If something bothers me, I prefer to discuss it openly."),
        ("q06", "When I am busy, I still try to send a quick update."),
        ("q07", "I enjoy joining social activities and group plans."),
        ("q08", "Shared values matter more to me than shared hobbies."),
        ("q09", "I am comfortable taking the first step to start a conversation."),
        ("q10", "I prefer planned meetups more than spontaneous plans."),
        ("q11", "I enjoy lighthearted humor and playful conversation."),
        ("q12", "I am open to building connections with people from different backgrounds.")
    ];

    private static readonly (string Id, string Label)[] OptionsData =
    [
        ("1", "Strongly Disagree"),
        ("2", "Disagree"),
        ("3", "Neutral"),
        ("4", "Agree"),
        ("5", "Strongly Agree")
    ];

    /// <summary>
    /// Creates a fresh, immutable instance of the production V1 questionnaire definition.
    /// Collections are independently constructed read-only wrappers ensuring caller isolation.
    /// </summary>
    public static QuestionnaireDefinition Create()
    {
        var questions = new QuestionDefinition[QuestionsData.Length];

        for (var i = 0; i < QuestionsData.Length; i++)
        {
            var (questionId, prompt) = QuestionsData[i];
            var options = new QuestionOption[OptionsData.Length];

            for (var j = 0; j < OptionsData.Length; j++)
            {
                var (optionId, label) = OptionsData[j];
                options[j] = new QuestionOption(optionId, label);
            }

            questions[i] = new QuestionDefinition(
                questionId,
                prompt,
                IsRequired: true,
                Array.AsReadOnly(options));
        }

        return new QuestionnaireDefinition(
            Version,
            Array.AsReadOnly(questions));
    }
}
