
using Andril.Social.Personality;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

public class QuestionnaireValidatorEdgeCaseTests
{
    private static QuestionnaireDefinition Definition() =>
        new(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "Example question",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", "Yes"),
                        new QuestionOption("no", "No")
                    })
            });

    [Fact]
    public void NullSubmission_IsRejected()
    {
        var result = QuestionnaireValidator.Validate(
            Definition(), null);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_SUBMISSION", result.Errors);
    }

    [Fact]
    public void NullAnswerCollection_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            null!);

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_SUBMISSION", result.Errors);
    }

    [Fact]
    public void NullAnswerItem_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new QuestionnaireAnswer[] { null! });

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_ANSWER", result.Errors);
    }

    [Fact]
    public void EmptyQuestionnaireVersion_IsRejected()
    {
        var definition = new QuestionnaireDefinition(
            "",
            Definition().Questions);

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_DEFINITION", result.Errors);
    }

    [Fact]
    public void EmptyQuestionnaire_IsRejected()
    {
        var definition = new QuestionnaireDefinition(
            "test-v1",
            Array.Empty<QuestionDefinition>());

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_DEFINITION", result.Errors);
    }

    [Fact]
    public void QuestionWithoutOptions_IsRejected()
    {
        var definition = new QuestionnaireDefinition(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "Example question",
                    true,
                    Array.Empty<QuestionOption>())
            });

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains(
            "INVALID_QUESTION_DEFINITION",
            result.Errors);
    }

    [Fact]
    public void BlankQuestionId_IsRejected()
    {
        var definition = new QuestionnaireDefinition(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    " ",
                    "Example question",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", "Yes")
                    })
            });

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains(
            "INVALID_QUESTION_DEFINITION",
            result.Errors);
    }

    [Fact]
    public void BlankOptionLabel_IsRejected()
    {
        var definition = new QuestionnaireDefinition(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "Example question",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", " ")
                    })
            });

        var result =
            QuestionnaireValidator.ValidateDefinition(definition);

        Assert.False(result.IsValid);
        Assert.Contains(
            "INVALID_OPTION_DEFINITION",
            result.Errors);
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

    [Fact]
    public void SkipWithAnswers_IsRejected()
    {
        var submission = new QuestionnaireSubmission(
            "test-v1",
            new[]
            {
                new QuestionnaireAnswer("q1", "yes")
            },
            Skip: true);

        var result = QuestionnaireValidator.Validate(
            Definition(), submission);

        Assert.False(result.IsValid);
        Assert.Contains("SKIP_WITH_ANSWERS", result.Errors);
    }
}
