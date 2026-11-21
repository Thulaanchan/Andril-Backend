using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Unit tests for the authoritative production QuestionnaireV1Definition provider.
/// Validates definition structure, exact approved prompts and answer options, collection immutability,
/// deterministic instance isolation, domain validator compliance, catalog integration, and DTO projection.
/// </summary>
public sealed class QuestionnaireV1DefinitionTests
{
    private static readonly (string Id, string Prompt)[] ExpectedQuestions =
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

    private static readonly (string Id, string Label)[] ExpectedOptions =
    [
        ("1", "Strongly Disagree"),
        ("2", "Disagree"),
        ("3", "Neutral"),
        ("4", "Agree"),
        ("5", "Strongly Agree")
    ];

    #region A. Definition Structure

    [Fact]
    public void Create_ReturnsExpectedVersionAndStructure()
    {
        var definition = QuestionnaireV1Definition.Create();

        Assert.NotNull(definition);
        Assert.Equal("v1", definition.Version);
        Assert.Equal(12, definition.Questions.Count);
        Assert.All(definition.Questions, question =>
        {
            Assert.True(question.IsRequired);
            Assert.Equal(5, question.Options.Count);
        });

        var questionIds = definition.Questions.Select(q => q.Id).ToArray();
        var expectedIds = Enumerable.Range(1, 12).Select(i => $"q{i:00}").ToArray();
        Assert.Equal(expectedIds, questionIds);
        Assert.Equal(12, questionIds.Distinct(StringComparer.Ordinal).Count());
    }

    #endregion

    #region B. Exact Approved Question Wording

    [Fact]
    public void Create_QuestionsHaveExactApprovedWordingAndOrder()
    {
        var definition = QuestionnaireV1Definition.Create();

        Assert.Equal(ExpectedQuestions.Length, definition.Questions.Count);

        for (var i = 0; i < ExpectedQuestions.Length; i++)
        {
            var expected = ExpectedQuestions[i];
            var actual = definition.Questions[i];

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Prompt, actual.Prompt);
            Assert.True(actual.IsRequired);
        }
    }

    #endregion

    #region C. Exact Answer Scale

    [Fact]
    public void Create_EveryQuestionUsesExactApprovedFivePointAnswerScale()
    {
        var definition = QuestionnaireV1Definition.Create();

        foreach (var question in definition.Questions)
        {
            Assert.Equal(ExpectedOptions.Length, question.Options.Count);

            for (var i = 0; i < ExpectedOptions.Length; i++)
            {
                var expected = ExpectedOptions[i];
                var actual = question.Options[i];

                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Label, actual.Label);
            }
        }
    }

    #endregion

    #region D. Collection Immutability

    [Fact]
    public void Create_QuestionCollection_IsReadOnlyAndThrowsOnMutation()
    {
        var definition = QuestionnaireV1Definition.Create();
        var questions = Assert.IsAssignableFrom<IList<QuestionDefinition>>(definition.Questions);

        Assert.True(questions.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => questions.Clear());
        Assert.Throws<NotSupportedException>(() => questions.Add(new QuestionDefinition(
            "q13", "Extra prompt", true, Array.Empty<QuestionOption>())));
        Assert.Throws<NotSupportedException>(() => questions.RemoveAt(0));

        Assert.Equal(12, definition.Questions.Count);
        Assert.Equal("q01", definition.Questions[0].Id);
    }

    [Fact]
    public void Create_OptionCollections_AreReadOnlyAndThrowOnMutation()
    {
        var definition = QuestionnaireV1Definition.Create();

        foreach (var question in definition.Questions)
        {
            var options = Assert.IsAssignableFrom<IList<QuestionOption>>(question.Options);

            Assert.True(options.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => options.Clear());
            Assert.Throws<NotSupportedException>(() => options.Add(new QuestionOption("6", "Extra Option")));
            Assert.Throws<NotSupportedException>(() => options.RemoveAt(0));

            Assert.Equal(5, question.Options.Count);
            Assert.Equal("1", question.Options[0].Id);
        }
    }

    #endregion

    #region E. Independent Factory Instances

    [Fact]
    public void Create_ProducesIndependentInstancesWithoutSharedCollections()
    {
        var first = QuestionnaireV1Definition.Create();
        var second = QuestionnaireV1Definition.Create();

        Assert.NotSame(first, second);
        Assert.NotSame(first.Questions, second.Questions);

        for (var i = 0; i < 12; i++)
        {
            Assert.NotSame(first.Questions[i], second.Questions[i]);
            Assert.NotSame(first.Questions[i].Options, second.Questions[i].Options);

            for (var j = 0; j < 5; j++)
            {
                Assert.NotSame(first.Questions[i].Options[j], second.Questions[i].Options[j]);
            }
        }
    }

    [Fact]
    public void Create_IsDeterministicAcrossMultipleInvocations()
    {
        var first = QuestionnaireV1Definition.Create();
        var second = QuestionnaireV1Definition.Create();

        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Questions.Count, second.Questions.Count);

        for (var i = 0; i < first.Questions.Count; i++)
        {
            Assert.Equal(first.Questions[i].Id, second.Questions[i].Id);
            Assert.Equal(first.Questions[i].Prompt, second.Questions[i].Prompt);
            Assert.Equal(first.Questions[i].IsRequired, second.Questions[i].IsRequired);

            for (var j = 0; j < first.Questions[i].Options.Count; j++)
            {
                Assert.Equal(first.Questions[i].Options[j].Id, second.Questions[i].Options[j].Id);
                Assert.Equal(first.Questions[i].Options[j].Label, second.Questions[i].Options[j].Label);
            }
        }
    }

    #endregion

    #region F. Validator Compatibility

    [Fact]
    public void Validator_AcceptsProductionV1Definition()
    {
        var definition = QuestionnaireV1Definition.Create();

        var validationResult = QuestionnaireValidator.ValidateDefinition(definition);

        Assert.True(validationResult.IsValid);
        Assert.Empty(validationResult.Errors);
    }

    [Fact]
    public void Validator_AllTwelveValidAnswers_ReturnsCompleted()
    {
        var definition = QuestionnaireV1Definition.Create();
        var answers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswer($"q{i:00}", "4"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", answers);

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_ElevenValidAnswers_ReturnsInProgress()
    {
        var definition = QuestionnaireV1Definition.Create();
        var answers = Enumerable.Range(1, 11)
            .Select(i => new QuestionnaireAnswer($"q{i:00}", "3"))
            .ToArray();
        var submission = new QuestionnaireSubmission("v1", answers);

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.InProgress, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_ZeroAnswers_ReturnsNotStarted()
    {
        var definition = QuestionnaireV1Definition.Create();
        var submission = new QuestionnaireSubmission("v1", Array.Empty<QuestionnaireAnswer>());

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.True(result.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, result.Status);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validator_UnknownOption_IsRejected()
    {
        var definition = QuestionnaireV1Definition.Create();
        var submission = new QuestionnaireSubmission("v1", new[]
        {
            new QuestionnaireAnswer("q01", "unknown-opt")
        });

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.False(result.IsValid);
        Assert.Contains("INVALID_OPTION", result.Errors);
    }

    [Fact]
    public void Validator_DuplicateAnswers_AreRejected()
    {
        var definition = QuestionnaireV1Definition.Create();
        var submission = new QuestionnaireSubmission("v1", new[]
        {
            new QuestionnaireAnswer("q01", "2"),
            new QuestionnaireAnswer("q01", "3")
        });

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.False(result.IsValid);
        Assert.Contains("DUPLICATE_ANSWER", result.Errors);
    }

    [Fact]
    public void Validator_UnsupportedQuestionId_IsRejected()
    {
        var definition = QuestionnaireV1Definition.Create();
        var submission = new QuestionnaireSubmission("v1", new[]
        {
            new QuestionnaireAnswer("q99", "1")
        });

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.False(result.IsValid);
        Assert.Contains("UNKNOWN_QUESTION", result.Errors);
    }

    [Fact]
    public void Validator_MismatchedVersion_IsRejected()
    {
        var definition = QuestionnaireV1Definition.Create();
        var submission = new QuestionnaireSubmission("v2", new[]
        {
            new QuestionnaireAnswer("q01", "1")
        });

        var result = QuestionnaireValidator.Validate(definition, submission);

        Assert.False(result.IsValid);
        Assert.Contains("VERSION_MISMATCH", result.Errors);
    }

    #endregion

    #region G. Catalog Integration

    [Fact]
    public void Catalog_AcceptsAndRetrievesProductionV1Definition()
    {
        var catalog = new QuestionnaireDefinitionCatalog(new[]
        {
            QuestionnaireV1Definition.Create()
        });

        var retrieved = catalog.FindByVersion("v1");

        Assert.NotNull(retrieved);
        Assert.Equal("v1", retrieved.Version);
        Assert.Equal(12, retrieved.Questions.Count);
    }

    [Fact]
    public void Catalog_UnknownVersion_DoesNotFallbackToV1()
    {
        var catalog = new QuestionnaireDefinitionCatalog(new[]
        {
            QuestionnaireV1Definition.Create()
        });

        Assert.Null(catalog.FindByVersion("v2"));
        Assert.Null(catalog.FindByVersion("unknown"));
        Assert.Null(catalog.FindByVersion(null));
        Assert.Null(catalog.FindByVersion(""));
    }

    [Fact]
    public void Catalog_RetrievedDefinition_PreservesApprovedQuestionContent()
    {
        var catalog = new QuestionnaireDefinitionCatalog(new[]
        {
            QuestionnaireV1Definition.Create()
        });

        var retrieved = catalog.FindByVersion("v1");

        Assert.NotNull(retrieved);
        Assert.Equal("q01", retrieved.Questions[0].Id);
        Assert.Equal("I enjoy meeting new people and starting conversations.", retrieved.Questions[0].Prompt);
        Assert.Equal("1", retrieved.Questions[0].Options[0].Id);
        Assert.Equal("Strongly Disagree", retrieved.Questions[0].Options[0].Label);
    }

    #endregion

    #region H. Privacy and DTO Boundaries

    [Fact]
    public void DtoProjection_PreservesAnswerFreeStructure()
    {
        var definition = QuestionnaireV1Definition.Create();

        var dto = QuestionnaireDefinitionDto.FromDefinition(definition);

        Assert.NotNull(dto);
        Assert.Equal("v1", dto.Version);
        Assert.Equal(12, dto.Questions.Count);

        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(ExpectedQuestions[i].Id, dto.Questions[i].Id);
            Assert.Equal(ExpectedQuestions[i].Prompt, dto.Questions[i].Prompt);
            Assert.True(dto.Questions[i].IsRequired);
            Assert.Equal(5, dto.Questions[i].Options.Count);

            for (var j = 0; j < 5; j++)
            {
                Assert.Equal(ExpectedOptions[j].Id, dto.Questions[i].Options[j].Id);
                Assert.Equal(ExpectedOptions[j].Label, dto.Questions[i].Options[j].Label);
            }
        }
    }

    #endregion
}
