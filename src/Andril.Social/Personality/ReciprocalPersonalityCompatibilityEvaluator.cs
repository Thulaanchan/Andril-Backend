using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Andril.Social.Personality;

/// <summary>
/// Encapsulates a user's personality evidence for reciprocal compatibility evaluation.
/// Does not expose raw questionnaire answers through public serialization.
/// </summary>
public sealed class PersonalityEvaluationProfile
{
    [JsonIgnore]
    public IReadOnlyList<QuestionnaireAnswer>? Answers { get; }

    [JsonIgnore]
    public string? Version { get; }

    [JsonIgnore]
    public string? Revision { get; }

    [JsonIgnore]
    public PersonalityArchetypeConfirmation? Confirmation { get; }

    [JsonIgnore]
    public PersonalityPartnerPreferences PartnerPreferences { get; }

    public PersonalityEvaluationProfile(
        IReadOnlyList<QuestionnaireAnswer>? answers,
        string? version,
        string? revision = null,
        PersonalityArchetypeConfirmation? confirmation = null,
        PersonalityPartnerPreferences? partnerPreferences = null)
    {
        Answers = answers is null ? null : answers.ToArray();
        Version = version;
        Revision = revision;
        Confirmation = confirmation;
        PartnerPreferences = partnerPreferences ?? PersonalityPartnerPreferences.Default;
    }

    public override string ToString() => "PersonalityEvaluationProfile [private]";
}

/// <summary>
/// Result of reciprocal personality compatibility evaluation under SOC-02.
/// </summary>
public sealed class ReciprocalPersonalityCompatibilityResult
{
    [JsonIgnore]
    public bool IsAvailable { get; }

    [JsonIgnore]
    public double? Score { get; }

    [JsonIgnore]
    public double? DetailedQuestionnaireSimilarity { get; }

    [JsonIgnore]
    public double? ReciprocalPreferenceAlignment { get; }

    [JsonIgnore]
    public int EvaluablePreferenceCount { get; }

    [JsonIgnore]
    public string? ReasonUnavailable { get; }

    private ReciprocalPersonalityCompatibilityResult(
        bool isAvailable,
        double? score,
        double? detailedSimilarity,
        double? preferenceAlignment,
        int evaluablePreferenceCount,
        string? reasonUnavailable)
    {
        IsAvailable = isAvailable;
        Score = score;
        DetailedQuestionnaireSimilarity = detailedSimilarity;
        ReciprocalPreferenceAlignment = preferenceAlignment;
        EvaluablePreferenceCount = evaluablePreferenceCount;
        ReasonUnavailable = reasonUnavailable;
    }

    public static ReciprocalPersonalityCompatibilityResult Available(
        double score,
        double detailedSimilarity,
        double? preferenceAlignment,
        int evaluablePreferenceCount) =>
        new(true, score, detailedSimilarity, preferenceAlignment, evaluablePreferenceCount, null);

    public static ReciprocalPersonalityCompatibilityResult Unavailable(string reason) =>
        new(false, null, null, null, 0, reason);

    public override string ToString() =>
        IsAvailable ? $"ReciprocalPersonalityCompatibilityResult: Available (Score={Score:F4})"
                    : "ReciprocalPersonalityCompatibilityResult: Unavailable [private]";
}

/// <summary>
/// Deterministic reciprocal personality compatibility evaluator implementing SOC-02 / P-04 rules.
/// Computes detailed 12-question similarity across 5 approved dimensions and reciprocal partner tendency alignment.
/// Guarantees exact score symmetry: Evaluate(A, B) == Evaluate(B, A).
/// </summary>
public static class ReciprocalPersonalityCompatibilityEvaluator
{
    private static readonly HashSet<string> ValidQuestionIds = new(StringComparer.Ordinal)
    {
        "q01", "q02", "q03", "q04", "q05", "q06", "q07", "q08", "q09", "q10", "q11", "q12"
    };

    // 5 Approved Dimension Groups
    private static readonly string[] CommunicationQuestions = ["q03", "q05", "q06"];
    private static readonly string[] SocialQuestions = ["q01", "q07", "q09"];
    private static readonly string[] ConnectionQuestions = ["q02", "q04"];
    private static readonly string[] LifestyleQuestions = ["q10", "q11"];
    private static readonly string[] ValuesQuestions = ["q08", "q12"];

    public static ReciprocalPersonalityCompatibilityResult Evaluate(
        PersonalityEvaluationProfile? profileA,
        PersonalityEvaluationProfile? profileB)
    {
        if (profileA is null || profileB is null)
        {
            return ReciprocalPersonalityCompatibilityResult.Unavailable("One or both profiles are null.");
        }

        var scoresA = ExtractValidScores(profileA.Answers, profileA.Version);
        if (scoresA is null)
        {
            return ReciprocalPersonalityCompatibilityResult.Unavailable("Profile A has an incomplete, unsupported, or invalid questionnaire.");
        }

        var scoresB = ExtractValidScores(profileB.Answers, profileB.Version);
        if (scoresB is null)
        {
            return ReciprocalPersonalityCompatibilityResult.Unavailable("Profile B has an incomplete, unsupported, or invalid questionnaire.");
        }

        // 1. Detailed 12-question similarity: s = 1.0 - abs(answerA - answerB) / 4.0
        var sComm = CalculateDimensionSimilarity(scoresA, scoresB, CommunicationQuestions);
        var sSocial = CalculateDimensionSimilarity(scoresA, scoresB, SocialQuestions);
        var sConn = CalculateDimensionSimilarity(scoresA, scoresB, ConnectionQuestions);
        var sLife = CalculateDimensionSimilarity(scoresA, scoresB, LifestyleQuestions);
        var sValues = CalculateDimensionSimilarity(scoresA, scoresB, ValuesQuestions);

        // Equal-weight mean across all 5 dimensions
        var detailedSimilarity = (sComm + sSocial + sConn + sLife + sValues) / 5.0;

        // 2. Classify both profiles to obtain resolved interaction tendencies
        var classificationA = PersonalityArchetypeClassifier.Classify(
            profileA.Answers, profileA.Version, profileA.Revision, profileA.Confirmation);
        var classificationB = PersonalityArchetypeClassifier.Classify(
            profileB.Answers, profileB.Version, profileB.Revision, profileB.Confirmation);

        // 3. Evaluate reciprocal partner preference alignment in both directions
        var preferenceTerms = new List<double>(6);

        // A -> B directed terms
        AddPreferenceTerms(profileA.PartnerPreferences, classificationB, preferenceTerms);

        // B -> A directed terms
        AddPreferenceTerms(profileB.PartnerPreferences, classificationA, preferenceTerms);

        double finalScore;
        double? preferenceAlignment = null;

        if (preferenceTerms.Count > 0)
        {
            preferenceAlignment = preferenceTerms.Average();
            // P = 0.40 * S + 0.60 * R
            finalScore = (0.40 * detailedSimilarity) + (0.60 * preferenceAlignment.Value);
        }
        else
        {
            // R is unavailable -> P = S
            finalScore = detailedSimilarity;
        }

        // Clamp between 0.0 and 1.0 to guard against floating-point anomalies
        finalScore = Math.Clamp(finalScore, 0.0, 1.0);

        return ReciprocalPersonalityCompatibilityResult.Available(
            finalScore,
            detailedSimilarity,
            preferenceAlignment,
            preferenceTerms.Count);
    }

    private static void AddPreferenceTerms(
        PersonalityPartnerPreferences preferences,
        PersonalityArchetypeClassificationResult targetClassification,
        List<double> terms)
    {
        // Social
        if (preferences.Social != SocialEngagementPreference.Either)
        {
            if (targetClassification.SocialTendency is not null)
            {
                var matches = (preferences.Social == SocialEngagementPreference.Reserved && targetClassification.SocialTendency == SocialEngagementTendency.Reserved) ||
                              (preferences.Social == SocialEngagementPreference.Outgoing && targetClassification.SocialTendency == SocialEngagementTendency.Outgoing);
                terms.Add(matches ? 1.0 : 0.0);
            }
        }

        // Communication
        if (preferences.Communication != CommunicationPreference.Either)
        {
            if (targetClassification.CommunicationTendency is not null)
            {
                var matches = (preferences.Communication == CommunicationPreference.LessExpressive && targetClassification.CommunicationTendency == CommunicationTendency.LessExpressive) ||
                              (preferences.Communication == CommunicationPreference.Expressive && targetClassification.CommunicationTendency == CommunicationTendency.Expressive);
                terms.Add(matches ? 1.0 : 0.0);
            }
        }

        // Planning
        if (preferences.Planning != PlanningPreference.Either)
        {
            if (targetClassification.PlanningTendency is not null)
            {
                var matches = (preferences.Planning == PlanningPreference.Spontaneous && targetClassification.PlanningTendency == PlanningTendency.Spontaneous) ||
                              (preferences.Planning == PlanningPreference.Structured && targetClassification.PlanningTendency == PlanningTendency.Structured);
                terms.Add(matches ? 1.0 : 0.0);
            }
        }
    }

    private static double CalculateDimensionSimilarity(
        IReadOnlyDictionary<string, int> scoresA,
        IReadOnlyDictionary<string, int> scoresB,
        string[] questionIds)
    {
        double sum = 0.0;
        foreach (var q in questionIds)
        {
            var diff = Math.Abs(scoresA[q] - scoresB[q]);
            var s = 1.0 - (diff / 4.0);
            sum += s;
        }
        return sum / questionIds.Length;
    }

    private static Dictionary<string, int>? ExtractValidScores(
        IReadOnlyList<QuestionnaireAnswer>? answers,
        string? version)
    {
        if (answers is null || answers.Count != 12) return null;
        if (!string.Equals(version, QuestionnaireV1Definition.Version, StringComparison.Ordinal)) return null;

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in answers)
        {
            if (a is null) return null;
            if (string.IsNullOrWhiteSpace(a.QuestionId) || !ValidQuestionIds.Contains(a.QuestionId)) return null;
            if (map.ContainsKey(a.QuestionId)) return null;

            int val;
            switch (a.OptionId)
            {
                case "1": val = 1; break;
                case "2": val = 2; break;
                case "3": val = 3; break;
                case "4": val = 4; break;
                case "5": val = 5; break;
                default: return null;
            }

            map[a.QuestionId] = val;
        }

        return map.Count == 12 ? map : null;
    }
}
