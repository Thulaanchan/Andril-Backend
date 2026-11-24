using System;
using System.Collections.Generic;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public class SocialCompatibilityScoringEngineTests
{
    private static List<QuestionnaireAnswer> CreateQuestionnaireAnswers(int scoreAll = 4)
    {
        var answers = new List<QuestionnaireAnswer>();
        for (int i = 1; i <= 12; i++)
        {
            var qId = $"q{i:D2}";
            answers.Add(new QuestionnaireAnswer(qId, scoreAll.ToString()));
        }

        return answers;
    }

    private static SocialScoringProfileEvidence CreateDefaultEvidence(
        Guid? profileId = null,
        int age = 25,
        string gender = "male",
        string showMe = "Women",
        int minAge = 20,
        int maxAge = 35,
        int minDistance = 0,
        int maxDistance = 50,
        SocialIdealAgeBand? idealAgeBand = null,
        IEnumerable<string>? confirmedLanguages = null,
        IEnumerable<string>? confirmedGoals = null,
        IEnumerable<Guid>? confirmedSocialInterests = null,
        IEnumerable<QuestionnaireAnswer>? questionnaireAnswers = null,
        PersonalityPartnerPreferences? partnerPrefs = null,
        PersonalityArchetypeConfirmation? archetypeConf = null,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool hasAuthoritativeEvidence = true,
        IEnumerable<Guid>? blockedIds = null)
    {
        var prefs = new SocialExplorePreferences(
            new SocialExploreRangeFilters(minAge, maxAge, minDistance, maxDistance),
            showMe,
            new[] { "English" },
            new[] { "Dating" });

        return new SocialScoringProfileEvidence(
            profileId ?? Guid.NewGuid(),
            age,
            gender,
            prefs,
            idealAgeBand,
            confirmedLanguages,
            confirmedGoals,
            confirmedSocialInterests,
            questionnaireResponse: null,
            questionnaireAnswers: questionnaireAnswers,
            questionnaireVersion: QuestionnaireV1Definition.Version,
            partnerPreferences: partnerPrefs,
            archetypeConfirmation: archetypeConf,
            isOptedIn: isOptedIn,
            isActive: isActive,
            isDiscoverable: isDiscoverable,
            hasAuthoritativeEvidence: hasAuthoritativeEvidence,
            blockedProfileIds: blockedIds);
    }

    [Fact]
    public void Evaluate_CompleteEvidence_CalculatesCorrectWeightedScore()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var interest1 = Guid.NewGuid();
        var interest2 = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 26,
            gender: "male",
            showMe: "Women",
            minAge: 20,
            maxAge: 30,
            minDistance: 0,
            maxDistance: 40,
            idealAgeBand: new SocialIdealAgeBand(24, 28),
            confirmedLanguages: new[] { "English", "French" },
            confirmedGoals: new[] { "Dating", "Friendship" },
            confirmedSocialInterests: new[] { interest1, interest2 },
            questionnaireAnswers: CreateQuestionnaireAnswers(scoreAll: 4));

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 25,
            gender: "female",
            showMe: "Men",
            minAge: 22,
            maxAge: 32,
            minDistance: 0,
            maxDistance: 50,
            idealAgeBand: new SocialIdealAgeBand(25, 29),
            confirmedLanguages: new[] { "English", "Spanish" },
            confirmedGoals: new[] { "Dating", "Life Partner" },
            confirmedSocialInterests: new[] { interest1 },
            questionnaireAnswers: CreateQuestionnaireAnswers(scoreAll: 4));

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);

        Assert.Equal(SocialCompatibilityEvaluationStatus.Scored, result.Status);
        Assert.True(result.IsScored);
        Assert.NotNull(result.CompatibilityScore);
        Assert.InRange(result.CompatibilityScore.Value, 0.0, 100.0);
        Assert.Equal(100.0, result.TotalAvailableWeight);
        Assert.Equal(100.0, result.EvidenceCoveragePercentage);
        Assert.Equal(100.0, result.ApplicableCoveragePercentage);

        // Verify factor breakdowns
        Assert.Equal(6, result.Factors.Count);
        Assert.All(result.Factors.Values, f => Assert.True(f.IsAvailable));
        Assert.Equal(1.0, result.Factors[SocialCompatibilityFactor.Personality].NormalizedScore);
        Assert.Equal(1.0, result.Factors[SocialCompatibilityFactor.CommonLanguages].NormalizedScore); // Shared "English"
        Assert.Equal(1.0 / 3.0, result.Factors[SocialCompatibilityFactor.ConnectionGoals].NormalizedScore!.Value, precision: 4); // Dating / (Dating, Friendship, Life Partner)
        Assert.Equal(0.5, result.Factors[SocialCompatibilityFactor.SharedInterests].NormalizedScore); // 1 / 2
        Assert.Equal(1.0, result.Factors[SocialCompatibilityFactor.AgePreference].NormalizedScore); // Both in ideal age bands
    }

    [Fact]
    public void Evaluate_PairDirectionSymmetry_GuaranteedIdenticalScore()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var interest = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 28,
            gender: "male",
            showMe: "Women",
            minAge: 20,
            maxAge: 30,
            minDistance: 0,
            maxDistance: 60,
            idealAgeBand: new SocialIdealAgeBand(22, 26),
            confirmedLanguages: new[] { "English", "German" },
            confirmedGoals: new[] { "Dating" },
            confirmedSocialInterests: new[] { interest },
            questionnaireAnswers: CreateQuestionnaireAnswers(scoreAll: 5));

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            minAge: 25,
            maxAge: 35,
            minDistance: 10,
            maxDistance: 80,
            idealAgeBand: new SocialIdealAgeBand(26, 30),
            confirmedLanguages: new[] { "English", "French" },
            confirmedGoals: new[] { "Dating", "Friendship" },
            confirmedSocialInterests: new[] { interest },
            questionnaireAnswers: CreateQuestionnaireAnswers(scoreAll: 3));

        var resultAB = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 20);
        var resultBA = SocialCompatibilityScoringEngine.Evaluate(profB, profA, approximateDistanceKm: 20);

        Assert.Equal(resultAB.Status, resultBA.Status);
        Assert.Equal(resultAB.CompatibilityScore, resultBA.CompatibilityScore);
        Assert.Equal(resultAB.TotalAvailableWeight, resultBA.TotalAvailableWeight);
        Assert.Equal(resultAB.EvidenceCoveragePercentage, resultBA.EvidenceCoveragePercentage);
        Assert.Equal(resultAB.ApplicableCoveragePercentage, resultBA.ApplicableCoveragePercentage);

        foreach (var factor in resultAB.Factors.Keys)
        {
            Assert.Equal(resultAB.Factors[factor].IsAvailable, resultBA.Factors[factor].IsAvailable);
            Assert.Equal(resultAB.Factors[factor].NormalizedScore, resultBA.Factors[factor].NormalizedScore);
            Assert.Equal(resultAB.Factors[factor].EffectiveWeight, resultBA.Factors[factor].EffectiveWeight);
        }
    }

    [Fact]
    public void Evaluate_OneSkippedQuestionnaire_RedistributesWeightsTo65NormalizedTo100()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            idealAgeBand: new SocialIdealAgeBand(22, 28),
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" },
            confirmedSocialInterests: new[] { Guid.NewGuid() },
            questionnaireAnswers: CreateQuestionnaireAnswers(4));

        // Profile B skipped questionnaire (null)
        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            idealAgeBand: new SocialIdealAgeBand(23, 29),
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" },
            confirmedSocialInterests: new[] { Guid.NewGuid() },
            questionnaireAnswers: null);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 15);

        Assert.Equal(SocialCompatibilityEvaluationStatus.Scored, result.Status);
        Assert.False(result.Factors[SocialCompatibilityFactor.Personality].IsAvailable);
        Assert.Null(result.Factors[SocialCompatibilityFactor.Personality].NormalizedScore);

        // Available weight is 65 (20 goals + 15 lang + 10 dist + 10 age + 10 interests)
        Assert.Equal(65.0, result.TotalAvailableWeight);
        Assert.Equal(65.0, result.EvidenceCoveragePercentage);
        Assert.Equal(100.0, result.ApplicableCoveragePercentage);

        // Effective weights: 20/65, 15/65, 10/65, 10/65, 10/65
        Assert.Equal(20.0 / 65.0, result.Factors[SocialCompatibilityFactor.ConnectionGoals].EffectiveWeight, precision: 5);
        Assert.Equal(15.0 / 65.0, result.Factors[SocialCompatibilityFactor.CommonLanguages].EffectiveWeight, precision: 5);
        Assert.Equal(10.0 / 65.0, result.Factors[SocialCompatibilityFactor.Distance].EffectiveWeight, precision: 5);
        Assert.Equal(10.0 / 65.0, result.Factors[SocialCompatibilityFactor.AgePreference].EffectiveWeight, precision: 5);
        Assert.Equal(10.0 / 65.0, result.Factors[SocialCompatibilityFactor.SharedInterests].EffectiveWeight, precision: 5);
    }

    [Fact]
    public void Evaluate_BothSkippedQuestionnaires_RedistributesWeightsTo65()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            idealAgeBand: new SocialIdealAgeBand(22, 28),
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" },
            confirmedSocialInterests: new[] { Guid.NewGuid() },
            questionnaireAnswers: null);

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            idealAgeBand: new SocialIdealAgeBand(23, 29),
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" },
            confirmedSocialInterests: new[] { Guid.NewGuid() },
            questionnaireAnswers: null);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 15);

        Assert.Equal(SocialCompatibilityEvaluationStatus.Scored, result.Status);
        Assert.False(result.Factors[SocialCompatibilityFactor.Personality].IsAvailable);
        Assert.Equal(65.0, result.TotalAvailableWeight);
        Assert.Equal(100.0, result.ApplicableCoveragePercentage);
    }

    [Fact]
    public void Evaluate_MissingIdealAgeBands_EvaluatesAvailableOrExcludes()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        // A has ideal band, B has none
        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            minAge: 20,
            maxAge: 30,
            idealAgeBand: new SocialIdealAgeBand(23, 27),
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" });

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 25,
            gender: "female",
            showMe: "Men",
            minAge: 20,
            maxAge: 30,
            idealAgeBand: null,
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" });

        var resultOneSided = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);
        Assert.True(resultOneSided.Factors[SocialCompatibilityFactor.AgePreference].IsAvailable);
        Assert.Equal(1.0, resultOneSided.Factors[SocialCompatibilityFactor.AgePreference].NormalizedScore);

        // Neither has ideal band -> AgePreference factor is unavailable
        var profANoBand = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            idealAgeBand: null,
            confirmedLanguages: new[] { "English" },
            confirmedGoals: new[] { "Dating" });

        var resultNeither = SocialCompatibilityScoringEngine.Evaluate(profANoBand, profB, approximateDistanceKm: 10);
        Assert.False(resultNeither.Factors[SocialCompatibilityFactor.AgePreference].IsAvailable);
        Assert.Null(resultNeither.Factors[SocialCompatibilityFactor.AgePreference].NormalizedScore);
    }

    [Fact]
    public void Evaluate_IdealAgeBandPiecewise_InterpolatesCorrectly()
    {
        // Candidate age 22, accepted [20, 30], ideal [24, 28] -> Below band: (22 - 20) / (24 - 20) = 2/4 = 0.5
        var idealBand = new SocialIdealAgeBand(24, 28);
        double scoreBelow = idealBand.EvaluateCandidateAge(candidateAge: 22, acceptedMinAge: 20, acceptedMaxAge: 30);
        Assert.Equal(0.5, scoreBelow);

        // Candidate age 29, accepted [20, 30], ideal [24, 28] -> Above band: (30 - 29) / (30 - 28) = 1/2 = 0.5
        double scoreAbove = idealBand.EvaluateCandidateAge(candidateAge: 29, acceptedMinAge: 20, acceptedMaxAge: 30);
        Assert.Equal(0.5, scoreAbove);

        // Inside band
        double scoreInside = idealBand.EvaluateCandidateAge(candidateAge: 26, acceptedMinAge: 20, acceptedMaxAge: 30);
        Assert.Equal(1.0, scoreInside);

        // Exact endpoints
        double scoreLo = idealBand.EvaluateCandidateAge(candidateAge: 20, acceptedMinAge: 20, acceptedMaxAge: 30);
        Assert.Equal(0.0, scoreLo);

        double scoreHi = idealBand.EvaluateCandidateAge(candidateAge: 30, acceptedMinAge: 20, acceptedMaxAge: 30);
        Assert.Equal(0.0, scoreHi);
    }

    [Fact]
    public void Evaluate_ConfirmedDisjointLanguagesAndGoals_AvailableWithZeroScore()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            confirmedLanguages: new[] { "Tamil", "Hindi" },
            confirmedGoals: new[] { "Friendship" });

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            confirmedLanguages: new[] { "French", "German" }, // Disjoint
            confirmedGoals: new[] { "Serious Relationship" }); // Disjoint

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);

        Assert.True(result.Factors[SocialCompatibilityFactor.CommonLanguages].IsAvailable);
        Assert.Equal(0.0, result.Factors[SocialCompatibilityFactor.CommonLanguages].NormalizedScore);

        Assert.True(result.Factors[SocialCompatibilityFactor.ConnectionGoals].IsAvailable);
        Assert.Equal(0.0, result.Factors[SocialCompatibilityFactor.ConnectionGoals].NormalizedScore);
    }

    [Fact]
    public void Evaluate_UnconfirmedEvidence_MarksFactorUnavailable()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        // Null confirmed collections (unconfirmed evidence)
        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null);

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);

        Assert.False(result.Factors[SocialCompatibilityFactor.CommonLanguages].IsAvailable);
        Assert.False(result.Factors[SocialCompatibilityFactor.ConnectionGoals].IsAvailable);
        Assert.False(result.Factors[SocialCompatibilityFactor.SharedInterests].IsAvailable);
    }

    [Fact]
    public void Evaluate_ZeroWidthDistanceRange_ReturnsOneWhenSatisfied()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            minDistance: 5,
            maxDistance: 5);

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            minDistance: 5,
            maxDistance: 5);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 5);
        Assert.True(result.Factors[SocialCompatibilityFactor.Distance].IsAvailable);
        Assert.Equal(1.0, result.Factors[SocialCompatibilityFactor.Distance].NormalizedScore);
    }

    [Fact]
    public void Evaluate_AllFactorsUnavailable_ReturnsUnscoredResult()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        // No questionnaire, no languages, no goals, no interests, no ideal age band, no distance
        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            idealAgeBand: null,
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null,
            questionnaireAnswers: null);

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            idealAgeBand: null,
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null,
            questionnaireAnswers: null);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: null);

        // Distance unavailable results in Indeterminate by precheck when null, but let's test with no distance evidence
        Assert.Equal(SocialCompatibilityEvaluationStatus.Indeterminate, result.Status);
    }

    [Fact]
    public void Evaluate_ZeroWeightWithDistanceOnly_ReturnsScored()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        // Only distance is available (weight 10)
        var profA = CreateDefaultEvidence(
            profileId: idA,
            age: 25,
            gender: "male",
            showMe: "Women",
            minDistance: 0,
            maxDistance: 20,
            idealAgeBand: null,
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null,
            questionnaireAnswers: null);

        var profB = CreateDefaultEvidence(
            profileId: idB,
            age: 24,
            gender: "female",
            showMe: "Men",
            minDistance: 0,
            maxDistance: 20,
            idealAgeBand: null,
            confirmedLanguages: null,
            confirmedGoals: null,
            confirmedSocialInterests: null,
            questionnaireAnswers: null);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 0);

        Assert.Equal(SocialCompatibilityEvaluationStatus.Scored, result.Status);
        Assert.Equal(10.0, result.TotalAvailableWeight);
        Assert.Equal(100.0, result.CompatibilityScore); // 1.0 distance score scaled to 100%
    }

    [Fact]
    public void Evaluate_EligibilityPreChecks_UnderageFailsIneligible()
    {
        var profUnderage = CreateDefaultEvidence(age: 17);
        var profAdult = CreateDefaultEvidence(age: 25);

        var result = SocialCompatibilityScoringEngine.Evaluate(profUnderage, profAdult, approximateDistanceKm: 10);
        Assert.Equal(SocialCompatibilityEvaluationStatus.Ineligible, result.Status);
    }

    [Fact]
    public void Evaluate_EligibilityPreChecks_SelfMatchingFailsIneligible()
    {
        var id = Guid.NewGuid();
        var profA = CreateDefaultEvidence(profileId: id);
        var profB = CreateDefaultEvidence(profileId: id);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);
        Assert.Equal(SocialCompatibilityEvaluationStatus.Ineligible, result.Status);
    }

    [Fact]
    public void Evaluate_EligibilityPreChecks_BlockedFailsIneligible()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var profA = CreateDefaultEvidence(profileId: idA, blockedIds: new[] { idB });
        var profB = CreateDefaultEvidence(profileId: idB);

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);
        Assert.Equal(SocialCompatibilityEvaluationStatus.Ineligible, result.Status);
    }

    [Fact]
    public void Evaluate_EligibilityPreChecks_HardFilterAgeMismatch_FailsExcluded()
    {
        var profA = CreateDefaultEvidence(age: 25, minAge: 20, maxAge: 24);
        var profB = CreateDefaultEvidence(age: 28, minAge: 20, maxAge: 30); // A wants <= 24, B is 28

        var result = SocialCompatibilityScoringEngine.Evaluate(profA, profB, approximateDistanceKm: 10);
        Assert.Equal(SocialCompatibilityEvaluationStatus.Excluded, result.Status);
        Assert.Equal(SocialCandidateExclusionReason.CallerAgePreferenceMismatch, result.ExclusionReason);
    }
}
