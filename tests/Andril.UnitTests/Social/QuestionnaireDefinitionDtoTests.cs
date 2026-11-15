
using Andril.Social.DTOs;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class QuestionnaireDefinitionDtoTests
{
    private static QuestionnaireDefinition CreateDefinition()
    {
        return new QuestionnaireDefinition(
            "test-v1",
            new[]
            {
                new QuestionDefinition(
                    "q1",
                    "I enjoy meeting new people.",
                    true,
                    new[]
                    {
                        new QuestionOption("yes", "Yes"),
                        new QuestionOption("no", "No")
                    }),

                new QuestionDefinition(
                    "q2",
                    "I enjoy travelling.",
                    false,
                    new[]
                    {
                        new QuestionOption("agree", "Agree"),
                        new QuestionOption("disagree", "Disagree")
                    })
            });
    }

    [Fact]
    public void ValidDefinition_MapsCorrectly()
    {
        var definition = CreateDefinition();

        var dto =
            QuestionnaireDefinitionDto.FromDefinition(definition);

        Assert.Equal("test-v1", dto.Version);
        Assert.Equal(2, dto.Questions.Count);

        Assert.Equal("q1", dto.Questions[0].Id);
        Assert.Equal(
            "I enjoy meeting new people.",
            dto.Questions[0].Prompt);
        Assert.True(dto.Questions[0].IsRequired);

        Assert.Equal("q2", dto.Questions[1].Id);
        Assert.False(dto.Questions[1].IsRequired);
    }

    [Fact]
    public void QuestionOptions_AreMappedCorrectly()
    {
        var dto = QuestionnaireDefinitionDto.FromDefinition(
            CreateDefinition());

        var options = dto.Questions[0].Options;

        Assert.Equal(2, options.Count);
        Assert.Equal("yes", options[0].Id);
        Assert.Equal("Yes", options[0].Label);
        Assert.Equal("no", options[1].Id);
        Assert.Equal("No", options[1].Label);
    }

    [Fact]
    public void NullDefinition_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            QuestionnaireDefinitionDto.FromDefinition(null!));
    }

    [Fact]
    public void OriginalCollectionChanges_DoNotAffectDto()
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
            "test-v1",
            questions);

        var dto =
            QuestionnaireDefinitionDto.FromDefinition(definition);

        options.Clear();
        questions.Clear();

        Assert.Single(dto.Questions);
        Assert.Equal(2, dto.Questions[0].Options.Count);
    }

    [Fact]
    public void ReturnedCollections_AreReadOnly()
    {
        var dto = QuestionnaireDefinitionDto.FromDefinition(
            CreateDefinition());

        var questions =
            Assert.IsAssignableFrom<IList<QuestionnaireQuestionDto>>(
                dto.Questions);

        Assert.Throws<NotSupportedException>(() =>
            questions.Clear());

        var options =
            Assert.IsAssignableFrom<IList<QuestionnaireOptionDto>>(
                dto.Questions[0].Options);

        Assert.Throws<NotSupportedException>(() =>
            options.Clear());
    }

    [Fact]
    public void PublicDtos_DoNotContainPrivateAnswerFields()
    {
        var definitionProperties =
            typeof(QuestionnaireDefinitionDto)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray();

        var questionProperties =
            typeof(QuestionnaireQuestionDto)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray();

        var optionProperties =
            typeof(QuestionnaireOptionDto)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name)
                .ToArray();

        Assert.Equal(
            new[] { "Questions", "Version" },
            definitionProperties);

        Assert.Equal(
            new[] { "Id", "IsRequired", "Options", "Prompt" },
            questionProperties);

        Assert.Equal(
            new[] { "Id", "Label" },
            optionProperties);
    }
}
