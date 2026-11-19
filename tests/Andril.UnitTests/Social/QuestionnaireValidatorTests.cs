
using Andril.Social.Personality;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

public class QuestionnaireValidatorTests
{
    private static QuestionnaireDefinition Definition() =>
        new(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "I enjoy meeting new people.",
                    true,
                    new[]
                    {
                        new QuestionOption("agree", "Agree"),
                        new QuestionOption("disagree", "Disagree")
                    }),

                new QuestionDefinition(
                    "q2",
                    "I enjoy travelling.",
                    false,
                    new[]
                    {
                        new QuestionOption("yes", "Yes"),
                        new QuestionOption("no", "No")
                    })
            });

    [Fact]
    public void ValidAnswer_CompletesRequiredQuestions()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[] { new QuestionnaireAnswer("q1", "agree") });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.Completed,
            result.Status);
    }

    [Fact]
    public void UnknownQuestion_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[] { new QuestionnaireAnswer("unknown", "agree") });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("UNKNOWN_QUESTION", result.Errors);
    }

    [Fact]
    public void InvalidOption_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[] { new QuestionnaireAnswer("q1", "invalid") });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_OPTION", result.Errors);
    }

    [Fact]
    public void DuplicateAnswer_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[]
            {
                new QuestionnaireAnswer("q1", "agree"),
                new QuestionnaireAnswer("q1", "disagree")
            });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("DUPLICATE_ANSWER", result.Errors);
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v2",
            new[] { new QuestionnaireAnswer("q1", "agree") });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("VERSION_MISMATCH", result.Errors);
    }

    [Fact]
    public void Questionnaire_CanBeSkipped()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            Array.Empty<QuestionnaireAnswer>(),
            Skip: true);

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.Skipped,
            result.Status);
    }

    [Fact]
    public void MissingRequiredAnswer_IsInProgress()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[] { new QuestionnaireAnswer("q2", "yes") });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.InProgress,
            result.Status);
    }

    [Fact]
    public void SkipWithAnswers_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[] { new QuestionnaireAnswer("q1", "agree") },
            Skip: true);

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("SKIP_WITH_ANSWERS", result.Errors);
    }

    [Fact]
    public void DuplicateQuestionDefinition_IsRejected()
    {
        var question = Definition().Questions[0];

        var definition = new QuestionnaireDefinition(
            "test-v1",
            new[] { question, question });

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains("DUPLICATE_QUESTION_ID", result.Errors);
    }

    [Fact]
    public void InvalidDefinition_IsRejected()
    {
        var result =
            QuestionnaireValidator.ValidateDefinition(null);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_DEFINITION", result.Errors);
    }

    [Fact]
    public void DuplicateOptions_AreRejected()
    {
        var question = new QuestionDefinition(
            "q1",
            "Test question",
            true,
            new[]
            {
                new QuestionOption("same", "First"),
                new QuestionOption("same", "Second")
            });

        var definition = new QuestionnaireDefinition(
            "test-v1",
            new[] { question });

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains("DUPLICATE_OPTION_ID", result.Errors);
    }

    [Fact]
    public void NoAnswers_ReturnsNotStarted()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            Array.Empty<QuestionnaireAnswer>());

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.NotStarted,
            result.Status);
    }
}
