using System.Text.Json;
using Andril.Social.Personality;
using Andril.Social.Services;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class QuestionnaireApplicationServiceTests
{
    private static QuestionnaireDefinition Definition(
        string version = "test-v2",
        int questionCount = 2,
        string optionId = "yes") =>
        new(version, Enumerable.Range(1, questionCount)
            .Select(number => new QuestionDefinition(
                $"q{number}",
                $"Synthetic question {number}",
                true,
                new[] { new QuestionOption(optionId, "Synthetic option") }))
            .ToArray());

    private static QuestionnaireSubmission Submission(
        int answerCount = 2,
        string version = "test-v2",
        string optionId = "yes") =>
        new(version, Enumerable.Range(1, answerCount)
            .Select(number => new QuestionnaireAnswer($"q{number}", optionId))
            .ToArray());

    private static QuestionnaireApplicationService Service(
        params QuestionnaireDefinition[] definitions) =>
        new(new QuestionnaireDefinitionCatalog(definitions));

    [Fact]
    public void Constructor_NullCatalog_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QuestionnaireApplicationService(null!));
    }

    [Fact]
    public void CompleteSubmission_ReturnsCompletedWithoutErrors()
    {
        var service = Service(Definition());

        var result = service.Validate(Submission());

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("test-v1", "old-option")]
    [InlineData("test-v2", "new-option")]
    public void VersionResolution_UsesExactDefinition(
        string version, string optionId)
    {
        var service = Service(
            Definition("test-v1", optionId: "old-option"),
            Definition("test-v2", optionId: "new-option"));

        var result = service.Validate(Submission(version: version, optionId: optionId));

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("unknown")]
    [InlineData("TEST-V2")]
    [InlineData(" test-v2 ")]
    public void UnresolvableVersion_IsRejectedWithoutFallback(string? version)
    {
        var service = Service(Definition());

        var result = service.Validate(Submission(version: version!));

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "VERSION_MISMATCH" }, result.Errors);
    }

    [Fact]
    public void AnotherVersionsOption_IsRejectedBySelectedDefinition()
    {
        var service = Service(
            Definition("test-v1", optionId: "old-option"),
            Definition("test-v2", optionId: "new-option"));

        var result = service.Validate(
            Submission(version: "test-v2", optionId: "old-option"));

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "INVALID_OPTION", "INVALID_OPTION" }, result.Errors);
    }

    [Fact]
    public void NullSubmission_ReturnsInvalidSubmission()
    {
        var service = Service(Definition());

        var result = service.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "INVALID_SUBMISSION" }, result.Errors);
    }

    [Fact]
    public void NullAnswers_ReturnsInvalidSubmission()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission("test-v2", null!));

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "INVALID_SUBMISSION" }, result.Errors);
    }

    [Fact]
    public void NullAnswerItem_ReturnsInvalidAnswer()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission(
            "test-v2", new QuestionnaireAnswer[] { null! }));

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "INVALID_ANSWER" }, result.Errors);
    }

    [Fact]
    public void UnknownQuestion_IsRejected()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission(
            "test-v2", new[] { new QuestionnaireAnswer("unknown-question", "yes") }));

        Assert.False(result.IsValid);
        Assert.Equal(new[] { "UNKNOWN_QUESTION" }, result.Errors);
    }

    [Fact]
    public void InvalidOption_IsRejected()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission(
            "test-v2", new[] { new QuestionnaireAnswer("q1", "unknown-option") }));

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "INVALID_OPTION" }, result.Errors);
    }

    [Fact]
    public void DuplicateAnswers_AreRejected()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission(
            "test-v2", new[]
            {
                new QuestionnaireAnswer("q1", "yes"),
                new QuestionnaireAnswer("q1", "yes")
            }));

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "DUPLICATE_ANSWER" }, result.Errors);
    }

    [Theory]
    [InlineData(0, QuestionnaireParticipationStatus.NotStarted)]
    [InlineData(1, QuestionnaireParticipationStatus.InProgress)]
    public void IncompleteSubmission_PreservesParticipationStatus(
        int answerCount, QuestionnaireParticipationStatus expectedStatus)
    {
        var service = Service(Definition());

        var result = service.Validate(Submission(answerCount));

        Assert.True(result.IsValid);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SkipWithoutAnswers_ReturnsSkipped()
    {
        var service = Service(Definition());

        var result = service.Validate(new QuestionnaireSubmission(
            "test-v2", Array.Empty<QuestionnaireAnswer>(), Skip: true));

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Skipped, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SkipWithAnswers_IsRejected()
    {
        var service = Service(Definition());

        var result = service.Validate(Submission() with { Skip = true });

        Assert.False(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Equal(new[] { "SKIP_WITH_ANSWERS" }, result.Errors);
    }

    [Theory]
    [InlineData(11, QuestionnaireParticipationStatus.InProgress)]
    [InlineData(12, QuestionnaireParticipationStatus.Completed)]
    public void V1_RequiresAllTwelveAnswersForCompletion(
        int answerCount, QuestionnaireParticipationStatus expectedStatus)
    {
        var service = Service(Definition("v1", questionCount: 12));

        var result = service.Validate(Submission(answerCount, "v1"));

        Assert.True(result.IsValid);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("invalid-option", false)]
    public void RepeatedValidation_IsDeterministicAndPreservesInputs(
        string optionId, bool expectedValid)
    {
        var options = new List<QuestionOption> { new("yes", "Synthetic option") };
        var questions = new List<QuestionDefinition>
        {
            new("q1", "Synthetic question", true, options)
        };
        var definition = new QuestionnaireDefinition("test-v2", questions);
        var catalog = new QuestionnaireDefinitionCatalog(new[] { definition });
        var service = new QuestionnaireApplicationService(catalog);
        var answers = new List<QuestionnaireAnswer> { new("q1", optionId) };
        var submission = new QuestionnaireSubmission("test-v2", answers);
        var storedDefinition = catalog.FindByVersion("test-v2");
        Assert.NotNull(storedDefinition);
        var definitionBefore = JsonSerializer.Serialize(definition);
        var catalogBefore = JsonSerializer.Serialize(storedDefinition);
        var submissionBefore = JsonSerializer.Serialize(submission);

        var first = service.Validate(submission);
        var second = service.Validate(submission);

        Assert.Equal(expectedValid, first.IsValid);
        Assert.Equal(expectedValid
            ? QuestionnaireParticipationStatus.Completed
            : QuestionnaireParticipationStatus.NotStarted, first.Status);
        Assert.Equal(first.IsValid, second.IsValid);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Errors.ToArray(), second.Errors.ToArray());
        Assert.Equal(definitionBefore, JsonSerializer.Serialize(definition));
        Assert.Equal(catalogBefore, JsonSerializer.Serialize(catalog.FindByVersion("test-v2")));
        Assert.Equal(submissionBefore, JsonSerializer.Serialize(submission));
        Assert.Same(answers, submission.Answers);
    }
}
