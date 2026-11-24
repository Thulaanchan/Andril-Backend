using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Andril.Social.Services;
using Andril.Social.Validators;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class QuestionnaireDefinitionCatalogTests
{
    private static readonly (string Id, string Prompt)[] ExpectedProductionQuestions =
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

    private static readonly (string Id, string Label)[] ExpectedProductionOptions =
    [
        ("1", "Strongly Disagree"),
        ("2", "Disagree"),
        ("3", "Neutral"),
        ("4", "Agree"),
        ("5", "Strongly Agree")
    ];

    private static QuestionnaireDefinition CreateDefinition(
        string version = "test-v1")
    {
        return new QuestionnaireDefinition(
            version,
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "Dummy personality question",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", "Yes"),
                        new QuestionOption("no", "No")
                    })
            });
    }

    [Fact]
    public void ValidDefinition_CanBeRetrieved()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        var result = catalog.FindByVersion("test-v1");

        Assert.NotNull(result);
        Assert.Equal("test-v1", result.Version);
    }

    [Fact]
    public void UnknownVersion_ReturnsNull()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        Assert.Null(catalog.FindByVersion("unknown"));
    }

    [Fact]
    public void NullOrBlankVersion_ReturnsNull()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        Assert.Null(catalog.FindByVersion(null));
        Assert.Null(catalog.FindByVersion(""));
        Assert.Null(catalog.FindByVersion(" "));
    }

    [Fact]
    public void InvalidDefinition_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new QuestionnaireDefinitionCatalog(
                new[]
                {
                    new QuestionnaireDefinition(
                        "",
                        Array.Empty<QuestionDefinition>())
                }));
    }

    [Fact]
    public void DuplicateVersions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new QuestionnaireDefinitionCatalog(
                new[]
                {
                    CreateDefinition("test-v1"),
                    CreateDefinition("test-v1")
                }));
    }

    [Fact]
    public void NullDefinitionsCollection_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new QuestionnaireDefinitionCatalog(null!));
    }

    [Fact]
    public void ModifyingOriginalDefinition_DoesNotChangeCatalog()
    {
        var options = new List<QuestionOption>
        {
            new("yes", "Yes"),
            new("no", "No")
        };

        var questions = new List<QuestionDefinition>
        {
            new("q1", "Dummy question", true, options)
        };

        var definition = new QuestionnaireDefinition(
            "test-v1", questions);

        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { definition });

        // Modify original collections after registration.
        options.Clear();
        questions.Clear();

        var result = catalog.FindByVersion("test-v1");

        Assert.NotNull(result);
        Assert.Single(result.Questions);
        Assert.Equal(2, result.Questions[0].Options.Count);
    }

    [Fact]
    public void RetrievedDefinition_CannotBeModified()
    {
        var catalog = new QuestionnaireDefinitionCatalog(
            new[] { CreateDefinition() });

        var result = catalog.FindByVersion("test-v1");

        Assert.NotNull(result);

        var questions =
            Assert.IsAssignableFrom<IList<QuestionDefinition>>(
                result.Questions);

        Assert.Throws<NotSupportedException>(() =>
            questions[0] = new QuestionDefinition(
                "changed",
                "Changed question",
                true,
                Array.Empty<QuestionOption>()));
    }

    #region Production Catalog Factory (CreateProductionV1) Tests

    [Fact]
    public void CreateProductionV1_ReturnsNonNullCatalog_WithValidV1Definition()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();

        Assert.NotNull(catalog);

        var definition = catalog.FindByVersion("v1");
        Assert.NotNull(definition);
        Assert.Equal("v1", definition.Version);

        var validationResult = QuestionnaireValidator.ValidateDefinition(definition);
        Assert.True(validationResult.IsValid);
        Assert.Empty(validationResult.Errors);

        Assert.Equal(12, definition.Questions.Count);
        Assert.All(definition.Questions, question =>
        {
            Assert.True(question.IsRequired);
            Assert.Equal(5, question.Options.Count);
        });
    }

    [Fact]
    public void CreateProductionV1_ContainsExactApprovedQuestionsInOrder()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var definition = catalog.FindByVersion("v1");

        Assert.NotNull(definition);
        Assert.Equal(ExpectedProductionQuestions.Length, definition.Questions.Count);

        for (var i = 0; i < ExpectedProductionQuestions.Length; i++)
        {
            var expected = ExpectedProductionQuestions[i];
            var actual = definition.Questions[i];

            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Prompt, actual.Prompt);
            Assert.True(actual.IsRequired);
        }
    }

    [Fact]
    public void CreateProductionV1_EveryQuestionContainsExactApprovedFivePointScaleInOrder()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var definition = catalog.FindByVersion("v1");

        Assert.NotNull(definition);

        foreach (var question in definition.Questions)
        {
            Assert.Equal(ExpectedProductionOptions.Length, question.Options.Count);

            for (var j = 0; j < ExpectedProductionOptions.Length; j++)
            {
                var expected = ExpectedProductionOptions[j];
                var actual = question.Options[j];

                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Label, actual.Label);
            }
        }
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("V1")]
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateProductionV1_VersionResolution_IsExactAndDoesNotFallback(string? version)
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();

        Assert.Null(catalog.FindByVersion(version));
    }

    [Fact]
    public void CreateProductionV1_ExactV1Version_ResolvesSuccessfully()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();

        var definition = catalog.FindByVersion("v1");

        Assert.NotNull(definition);
        Assert.Equal("v1", definition.Version);
    }

    [Fact]
    public void CreateProductionV1_ProducesIndependentCatalogAndDefinitionInstances()
    {
        var catalog1 = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var catalog2 = QuestionnaireDefinitionCatalog.CreateProductionV1();

        Assert.NotSame(catalog1, catalog2);

        var def1 = catalog1.FindByVersion("v1");
        var def2 = catalog2.FindByVersion("v1");

        Assert.NotNull(def1);
        Assert.NotNull(def2);
        Assert.NotSame(def1, def2);
        Assert.NotSame(def1.Questions, def2.Questions);

        for (var i = 0; i < 12; i++)
        {
            Assert.NotSame(def1.Questions[i], def2.Questions[i]);
            Assert.NotSame(def1.Questions[i].Options, def2.Questions[i].Options);

            for (var j = 0; j < 5; j++)
            {
                Assert.NotSame(def1.Questions[i].Options[j], def2.Questions[i].Options[j]);
            }
        }
    }

    [Fact]
    public void CreateProductionV1_MultipleInstances_HaveEquivalentContent()
    {
        var catalog1 = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var catalog2 = QuestionnaireDefinitionCatalog.CreateProductionV1();

        var def1 = catalog1.FindByVersion("v1")!;
        var def2 = catalog2.FindByVersion("v1")!;

        Assert.Equal(def1.Version, def2.Version);
        Assert.Equal(def1.Questions.Count, def2.Questions.Count);

        for (var i = 0; i < def1.Questions.Count; i++)
        {
            Assert.Equal(def1.Questions[i].Id, def2.Questions[i].Id);
            Assert.Equal(def1.Questions[i].Prompt, def2.Questions[i].Prompt);
            Assert.Equal(def1.Questions[i].IsRequired, def2.Questions[i].IsRequired);

            for (var j = 0; j < def1.Questions[i].Options.Count; j++)
            {
                Assert.Equal(def1.Questions[i].Options[j].Id, def2.Questions[i].Options[j].Id);
                Assert.Equal(def1.Questions[i].Options[j].Label, def2.Questions[i].Options[j].Label);
            }
        }
    }

    [Fact]
    public void CreateProductionV1_RetrievedQuestionsAndOptionsAreReadOnly_AndCorruptionsPrevented()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var definition = catalog.FindByVersion("v1");

        Assert.NotNull(definition);

        var questions = Assert.IsAssignableFrom<IList<QuestionDefinition>>(definition.Questions);
        Assert.Throws<NotSupportedException>(() => questions.Clear());
        Assert.Throws<NotSupportedException>(() => questions.Add(new QuestionDefinition(
            "extra", "Extra prompt", true, Array.Empty<QuestionOption>())));
        Assert.Throws<NotSupportedException>(() => questions.RemoveAt(0));

        var options = Assert.IsAssignableFrom<IList<QuestionOption>>(definition.Questions[0].Options);
        Assert.Throws<NotSupportedException>(() => options.Clear());
        Assert.Throws<NotSupportedException>(() => options.Add(new QuestionOption("extra", "Extra option")));
        Assert.Throws<NotSupportedException>(() => options.RemoveAt(0));

        // Verify subsequent lookups are not corrupted
        var reLookup = catalog.FindByVersion("v1");
        Assert.NotNull(reLookup);
        Assert.Equal(12, reLookup.Questions.Count);
        Assert.Equal("q01", reLookup.Questions[0].Id);
        Assert.Equal(5, reLookup.Questions[0].Options.Count);
    }

    [Fact]
    public void CreateProductionV1_CompatibleWithQuestionnaireApplicationService_GetDefinitionReturnsAnswerFreeDto()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var service = new QuestionnaireApplicationService(catalog);

        var dto = service.GetDefinition("v1");

        Assert.NotNull(dto);
        Assert.Equal("v1", dto.Version);
        Assert.Equal(12, dto.Questions.Count);

        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(ExpectedProductionQuestions[i].Id, dto.Questions[i].Id);
            Assert.Equal(ExpectedProductionQuestions[i].Prompt, dto.Questions[i].Prompt);
            Assert.True(dto.Questions[i].IsRequired);
            Assert.Equal(5, dto.Questions[i].Options.Count);

            for (var j = 0; j < 5; j++)
            {
                Assert.Equal(ExpectedProductionOptions[j].Id, dto.Questions[i].Options[j].Id);
                Assert.Equal(ExpectedProductionOptions[j].Label, dto.Questions[i].Options[j].Label);
            }
        }
    }

    [Fact]
    public void CreateProductionV1_CompatibleWithQuestionnaireApplicationService_ValidSubmissionsYieldExpectedStatuses()
    {
        var catalog = QuestionnaireDefinitionCatalog.CreateProductionV1();
        var service = new QuestionnaireApplicationService(catalog);

        // 12 valid answers -> Completed
        var completeAnswers = Enumerable.Range(1, 12)
            .Select(i => new QuestionnaireAnswer($"q{i:00}", "3"))
            .ToArray();
        var completeResult = service.Validate(new QuestionnaireSubmission("v1", completeAnswers));

        Assert.True(completeResult.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, completeResult.Status);
        Assert.Empty(completeResult.Errors);

        // 11 valid answers -> InProgress
        var partialAnswers = Enumerable.Range(1, 11)
            .Select(i => new QuestionnaireAnswer($"q{i:00}", "3"))
            .ToArray();
        var partialResult = service.Validate(new QuestionnaireSubmission("v1", partialAnswers));

        Assert.True(partialResult.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.InProgress, partialResult.Status);
        Assert.Empty(partialResult.Errors);

        // Unknown version -> Rejected
        var unknownResult = service.Validate(new QuestionnaireSubmission("unknown", completeAnswers));

        Assert.False(unknownResult.IsValid);
        Assert.Equal(QuestionnaireParticipationStatus.NotStarted, unknownResult.Status);
        Assert.Equal(new[] { "VERSION_MISMATCH" }, unknownResult.Errors);
    }

    #endregion
}
