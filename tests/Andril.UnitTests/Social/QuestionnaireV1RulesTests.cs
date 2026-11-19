using System;
using System.Linq;
using Andril.Social.Personality;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class QuestionnaireV1RulesTests
{
    private static QuestionnaireDefinition CreateDefinition(
        int questionCount = 12,
        int? optionalQuestionNumber = null,
        string version = "v1")
    {
        var questions = Enumerable.Range(1, questionCount)
            .Select(number => new QuestionDefinition(
                $"q{number:00}",
                $"Personality statement {number}",
                number != optionalQuestionNumber,
                new[]
                {
                    new QuestionOption("agree", "Agree"),
                    new QuestionOption("disagree", "Disagree")
                }))
            .ToArray();

        return new QuestionnaireDefinition(version, questions);
    }

    private static QuestionnaireSubmission CreateSubmission(
        int answeredCount,
        string version = "v1")
    {
        var answers = Enumerable.Range(1, answeredCount)
            .Select(number => new QuestionnaireAnswer(
                $"q{number:00}",
                "agree"))
            .ToArray();

        return new QuestionnaireSubmission(version, answers);
    }

    [Fact]
    public void V1_AllTwelveRequiredAnswers_MarkCompleted()
    {
        var definition = CreateDefinition();
        var submission = CreateSubmission(12);

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.Completed,
            result.Status);
    }

    [Fact]
    public void V1_ElevenAnswers_RemainInProgress()
    {
        var definition = CreateDefinition();
        var submission = CreateSubmission(11);

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.InProgress,
            result.Status);
    }

    [Fact]
    public void V1_NoAnswers_RemainNotStarted()
    {
        var definition = CreateDefinition();
        var submission = CreateSubmission(0);

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.NotStarted,
            result.Status);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    public void V1_RejectsIncorrectQuestionCount(
        int questionCount)
    {
        var definition = CreateDefinition(questionCount);
        var submission = CreateSubmission(questionCount);

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void V1_RejectsOptionalQuestions()
    {
        var definition = CreateDefinition(
            optionalQuestionNumber: 12);

        var submission = CreateSubmission(11);

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void OtherVersions_AreNotLimitedByV1CountRule()
    {
        var definition = CreateDefinition(
            questionCount: 2,
            version: "test-v2");

        var submission = CreateSubmission(
            answeredCount: 2,
            version: "test-v2");

        var result = QuestionnaireValidator.Validate(
            definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(
            QuestionnaireParticipationStatus.Completed,
            result.Status);
    }
}
