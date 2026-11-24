using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class ReciprocalPersonalityCompatibilityEvaluatorTests
{
    private static List<QuestionnaireAnswer> CreateAnswersForArchetype(PersonalityArchetype archetype)
    {
        var socialVal = archetype.Social == SocialEngagementTendency.Outgoing ? "5" : "1";
        var commVal = archetype.Communication == CommunicationTendency.Expressive ? "5" : "1";
        var planVal = archetype.Planning == PlanningTendency.Structured ? "5" : "1";

        return
        [
            new("q01", socialVal),
            new("q02", "3"),
            new("q03", commVal),
            new("q04", "3"),
            new("q05", commVal),
            new("q06", commVal),
            new("q07", socialVal),
            new("q08", "3"),
            new("q09", socialVal),
            new("q10", planVal),
            new("q11", "3"),
            new("q12", "3")
        ];
    }

    private static List<QuestionnaireAnswer> CreateCustomAnswers(int val)
    {
        var s = val.ToString();
        return
        [
            new("q01", s), new("q02", s), new("q03", s), new("q04", s),
            new("q05", s), new("q06", s), new("q07", s), new("q08", s),
            new("q09", s), new("q10", s), new("q11", s), new("q12", s)
        ];
    }

    [Fact]
    public void Evaluate_All64OrderedArchetypeCombinations_ProduceValidSymmetricalScores()
    {
        var allArchetypes = PersonalityArchetype.All;
        Assert.Equal(8, allArchetypes.Count);

        var count = 0;
        foreach (var archA in allArchetypes)
        {
            foreach (var archB in allArchetypes)
            {
                var profileA = new PersonalityEvaluationProfile(CreateAnswersForArchetype(archA), "v1");
                var profileB = new PersonalityEvaluationProfile(CreateAnswersForArchetype(archB), "v1");

                var resAB = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(profileA, profileB);
                var resBA = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(profileB, profileA);

                Assert.True(resAB.IsAvailable);
                Assert.True(resBA.IsAvailable);
                Assert.NotNull(resAB.Score);
                Assert.NotNull(resBA.Score);

                // Exact symmetry
                Assert.Equal(resAB.Score!.Value, resBA.Score!.Value, precision: 6);
                Assert.InRange(resAB.Score.Value, 0.0, 1.0);

                count++;
            }
        }

        Assert.Equal(64, count);
    }

    [Fact]
    public void Evaluate_IdenticalAnswers_WithoutPreferences_YieldsPerfectSimilarity()
    {
        var answers = CreateCustomAnswers(4);
        var pA = new PersonalityEvaluationProfile(answers, "v1");
        var pB = new PersonalityEvaluationProfile(answers, "v1");

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Equal(1.0, result.DetailedQuestionnaireSimilarity!.Value, precision: 6);
        Assert.Equal(1.0, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_CompletelyOppositeAnswers_WithoutPreferences_YieldsZeroSimilarity()
    {
        var answersA = CreateCustomAnswers(1);
        var answersB = CreateCustomAnswers(5);

        var pA = new PersonalityEvaluationProfile(answersA, "v1");
        var pB = new PersonalityEvaluationProfile(answersB, "v1");

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Equal(0.0, result.DetailedQuestionnaireSimilarity!.Value, precision: 6);
        Assert.Equal(0.0, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_WithReciprocalPreferences_BlendsSimilarityAndAlignmentCorrectly()
    {
        // User A: Outgoing (AP08)
        var pA = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP08),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(
                Social: SocialEngagementPreference.Reserved, // wants Reserved
                Communication: CommunicationPreference.LessExpressive)); // wants LessExpressive

        // User B: Reserved & LessExpressive (AP01)
        var pB = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP01),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(
                Social: SocialEngagementPreference.Outgoing, // wants Outgoing
                Communication: CommunicationPreference.Expressive)); // wants Expressive

        // A satisfies B's preferences (A is Outgoing & Expressive -> 2 matches: 1.0, 1.0)
        // B satisfies A's preferences (B is Reserved & LessExpressive -> 2 matches: 1.0, 1.0)
        // Total 4 evaluable terms, all 1.0 -> R = 1.0
        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Equal(1.0, result.ReciprocalPreferenceAlignment!.Value, precision: 6);
        Assert.Equal(4, result.EvaluablePreferenceCount);

        // P = 0.40 * S + 0.60 * R
        var expectedP = (0.40 * result.DetailedQuestionnaireSimilarity!.Value) + (0.60 * 1.0);
        Assert.Equal(expectedP, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_UnsatisfiedReciprocalPreferences_ReducesScore()
    {
        // User A: Outgoing (AP08), wants Reserved
        var pA = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP08),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(Social: SocialEngagementPreference.Reserved));

        // User B: Outgoing (AP08), wants Reserved
        var pB = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP08),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(Social: SocialEngagementPreference.Reserved));

        // Both want Reserved, but both are Outgoing -> 2 terms, both 0.0 -> R = 0.0
        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Equal(0.0, result.ReciprocalPreferenceAlignment!.Value, precision: 6);
        Assert.Equal(2, result.EvaluablePreferenceCount);

        // P = 0.40 * S + 0.60 * 0 = 0.40 * S
        var expectedP = 0.40 * result.DetailedQuestionnaireSimilarity!.Value;
        Assert.Equal(expectedP, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_EitherPreferences_ExcludedFromEvaluableTerms()
    {
        var pA = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP01),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(
                Social: SocialEngagementPreference.Either,
                Communication: CommunicationPreference.Either,
                Planning: PlanningPreference.Either));

        var pB = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP02),
            "v1",
            partnerPreferences: PersonalityPartnerPreferences.Default);

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Null(result.ReciprocalPreferenceAlignment);
        Assert.Equal(0, result.EvaluablePreferenceCount);
        // When R is unavailable, P = S
        Assert.Equal(result.DetailedQuestionnaireSimilarity!.Value, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_UnresolvedTargetTendency_ExcludedFromAlignmentTerms()
    {
        // A has all 3s without confirmation -> all axes unresolved
        var neutralAnswers = CreateCustomAnswers(3);
        var pA = new PersonalityEvaluationProfile(neutralAnswers, "v1");

        // B has preferences for Social
        var pB = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP01),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(Social: SocialEngagementPreference.Outgoing));

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        // Target A's social is unresolved, so B's preference is not evaluable
        Assert.Null(result.ReciprocalPreferenceAlignment);
        Assert.Equal(0, result.EvaluablePreferenceCount);
        Assert.Equal(result.DetailedQuestionnaireSimilarity!.Value, result.Score!.Value, precision: 6);
    }

    [Fact]
    public void Evaluate_IncompleteOrNullQuestionnaire_ReturnsUnavailable()
    {
        var validAnswers = CreateCustomAnswers(4);
        var pA = new PersonalityEvaluationProfile(validAnswers, "v1");
        var pB = new PersonalityEvaluationProfile(null, "v1");

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Score);
        Assert.Contains("incomplete", result.ReasonUnavailable);
    }

    [Fact]
    public void Evaluate_UnsupportedVersion_ReturnsUnavailable()
    {
        var validAnswers = CreateCustomAnswers(4);
        var pA = new PersonalityEvaluationProfile(validAnswers, "v1");
        var pB = new PersonalityEvaluationProfile(validAnswers, "v2");

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Profile_Answers_IsDefensivelyCopied()
    {
        var answers = CreateCustomAnswers(3);
        var profile = new PersonalityEvaluationProfile(answers, "v1");

        answers[0] = new QuestionnaireAnswer("q01", "5");

        Assert.Equal("3", profile.Answers![0].OptionId);
    }

    [Theory]
    [InlineData(" 1 ")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("6")]
    [InlineData("invalid")]
    public void Evaluate_NonCanonicalOptionValue_ReturnsUnavailable(string nonCanonicalOption)
    {
        var answersA = CreateCustomAnswers(4);
        answersA[0] = new QuestionnaireAnswer("q01", nonCanonicalOption);
        var answersB = CreateCustomAnswers(4);

        var pA = new PersonalityEvaluationProfile(answersA, "v1");
        var pB = new PersonalityEvaluationProfile(answersB, "v1");

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Evaluate_StaleConfirmationRevision_ExcludesUnresolvedAxisFromPreferences()
    {
        var neutralAnswers = CreateCustomAnswers(3);
        var staleConfirmation = new PersonalityArchetypeConfirmation(
            "old-rev",
            SocialConfirmation: SocialEngagementTendency.Outgoing);

        var pA = new PersonalityEvaluationProfile(
            neutralAnswers, "v1", revision: "new-rev", confirmation: staleConfirmation);

        var pB = new PersonalityEvaluationProfile(
            CreateAnswersForArchetype(PersonalityArchetype.AP01),
            "v1",
            partnerPreferences: new PersonalityPartnerPreferences(Social: SocialEngagementPreference.Outgoing));

        var result = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);

        Assert.True(result.IsAvailable);
        Assert.Null(result.ReciprocalPreferenceAlignment);
        Assert.Equal(0, result.EvaluablePreferenceCount);
    }

    [Fact]
    public void Result_And_Profile_ToString_ProtectsInternalPrivacy()
    {
        var validAnswers = CreateCustomAnswers(4);
        var pA = new PersonalityEvaluationProfile(validAnswers, "v1");
        var pB = new PersonalityEvaluationProfile(null, "v1");

        Assert.Equal("PersonalityEvaluationProfile [private]", pA.ToString());

        var unavailableResult = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pB);
        Assert.Equal("ReciprocalPersonalityCompatibilityResult: Unavailable [private]", unavailableResult.ToString());

        var availableResult = ReciprocalPersonalityCompatibilityEvaluator.Evaluate(pA, pA);
        Assert.Contains("ReciprocalPersonalityCompatibilityResult: Available", availableResult.ToString());
        Assert.DoesNotContain("DetailedQuestionnaireSimilarity", availableResult.ToString());
    }
}
