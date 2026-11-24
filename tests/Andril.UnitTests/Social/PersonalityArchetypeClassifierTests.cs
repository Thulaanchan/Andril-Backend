using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class PersonalityArchetypeClassifierTests
{
    private static List<QuestionnaireAnswer> CreateAnswers(
        int q01 = 3, int q02 = 3, int q03 = 3, int q04 = 3, int q05 = 3, int q06 = 3,
        int q07 = 3, int q08 = 3, int q09 = 3, int q10 = 3, int q11 = 3, int q12 = 3)
    {
        return
        [
            new("q01", q01.ToString()),
            new("q02", q02.ToString()),
            new("q03", q03.ToString()),
            new("q04", q04.ToString()),
            new("q05", q05.ToString()),
            new("q06", q06.ToString()),
            new("q07", q07.ToString()),
            new("q08", q08.ToString()),
            new("q09", q09.ToString()),
            new("q10", q10.ToString()),
            new("q11", q11.ToString()),
            new("q12", q12.ToString())
        ];
    }

    [Theory]
    [InlineData(1, 1, 5, PersonalityArchetypeId.AP01, "The Quiet Planner")]
    [InlineData(1, 1, 1, PersonalityArchetypeId.AP02, "The Quiet Explorer")]
    [InlineData(1, 5, 5, PersonalityArchetypeId.AP03, "The Thoughtful Communicator")]
    [InlineData(1, 5, 1, PersonalityArchetypeId.AP04, "The Gentle Adventurer")]
    [InlineData(5, 1, 5, PersonalityArchetypeId.AP05, "The Social Organizer")]
    [InlineData(5, 1, 1, PersonalityArchetypeId.AP06, "The Free Spirit")]
    [InlineData(5, 5, 5, PersonalityArchetypeId.AP07, "The Social Connector")]
    [InlineData(5, 5, 1, PersonalityArchetypeId.AP08, "The Enthusiast")]
    public void Classify_AllEightArchetypes_ClassifiesCorrectly(
        int socialVal, int commVal, int planVal, PersonalityArchetypeId expectedId, string expectedName)
    {
        // Social: q01, q07, q09; Communication: q03, q05, q06; Planning: q10
        var answers = CreateAnswers(
            q01: socialVal, q07: socialVal, q09: socialVal,
            q03: commVal, q05: commVal, q06: commVal,
            q10: planVal);

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Archetype);
        Assert.Equal(expectedId, result.Archetype!.Id);
        Assert.Equal(expectedName, result.Archetype.Name);
        Assert.False(result.HasUnresolvedAxes);
    }

    [Fact]
    public void Classify_NeutralTendencies_WithoutConfirmation_ReturnsUnresolved()
    {
        // All 3s (mean = 3.0 for all axes)
        var answers = CreateAnswers();

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Archetype);
        Assert.True(result.HasUnresolvedAxes);
        Assert.Equal(3, result.UnresolvedAxes.Count);
        Assert.Contains("SocialEngagement", result.UnresolvedAxes);
        Assert.Contains("Communication", result.UnresolvedAxes);
        Assert.Contains("Planning", result.UnresolvedAxes);
    }

    [Fact]
    public void Classify_NeutralTendencies_WithValidConfirmation_ResolvesArchetype()
    {
        var answers = CreateAnswers(); // all 3s
        var confirmation = new PersonalityArchetypeConfirmation(
            "rev-1",
            SocialConfirmation: SocialEngagementTendency.Outgoing,
            CommunicationConfirmation: CommunicationTendency.Expressive,
            PlanningConfirmation: PlanningTendency.Structured);

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1", confirmation);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Archetype);
        Assert.Equal(PersonalityArchetypeId.AP07, result.Archetype!.Id);
        Assert.Equal("The Social Connector", result.Archetype.Name);
    }

    [Fact]
    public void Classify_StaleConfirmationRevision_IgnoresConfirmation()
    {
        var answers = CreateAnswers(); // all 3s
        var confirmation = new PersonalityArchetypeConfirmation(
            "old-rev",
            SocialConfirmation: SocialEngagementTendency.Outgoing,
            CommunicationConfirmation: CommunicationTendency.Expressive,
            PlanningConfirmation: PlanningTendency.Structured);

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "new-rev", confirmation);

        Assert.False(result.IsSuccess);
        Assert.True(result.HasUnresolvedAxes);
    }

    [Fact]
    public void Classify_InvalidConfirmationOverride_RejectsWhenAxisIsAlreadyResolved()
    {
        // Social is clearly Outgoing (all 5s -> mean = 5.0)
        var answers = CreateAnswers(q01: 5, q07: 5, q09: 5, q03: 5, q05: 5, q06: 5, q10: 5);
        // Confirmation tries to override Social to Reserved
        var confirmation = new PersonalityArchetypeConfirmation(
            "rev-1",
            SocialConfirmation: SocialEngagementTendency.Reserved);

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1", confirmation);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid confirmation override", result.ErrorMessage);
    }

    [Fact]
    public void Classify_IncompleteAnswers_FailsClosed()
    {
        var answers = CreateAnswers().Take(11).ToList();

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("Incomplete answers", result.ErrorMessage);
    }

    [Fact]
    public void Classify_DuplicateQuestionId_FailsClosed()
    {
        var answers = CreateAnswers();
        answers[11] = new QuestionnaireAnswer("q01", "4"); // duplicate q01

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("Duplicate answer", result.ErrorMessage);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("invalid")]
    [InlineData("")]
    public void Classify_InvalidOptionValue_FailsClosed(string invalidOption)
    {
        var answers = CreateAnswers();
        answers[0] = new QuestionnaireAnswer("q01", invalidOption);

        var result = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid option value", result.ErrorMessage);
    }

    [Fact]
    public void Classify_UnsupportedVersion_FailsClosed()
    {
        var answers = CreateAnswers();

        var result = PersonalityArchetypeClassifier.Classify(answers, "v2", "rev-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported questionnaire definition version", result.ErrorMessage);
    }

    [Fact]
    public void Classify_IsDeterministic()
    {
        var answers = CreateAnswers(q01: 4, q07: 4, q09: 4, q03: 2, q05: 2, q06: 2, q10: 5);

        var r1 = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");
        var r2 = PersonalityArchetypeClassifier.Classify(answers, "v1", "rev-1");

        Assert.Equal(r1.Archetype?.Id, r2.Archetype?.Id);
        Assert.Equal(r1.IsSuccess, r2.IsSuccess);
    }
}
