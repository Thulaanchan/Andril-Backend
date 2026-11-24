using System;
using System.Collections.Generic;
using System.Linq;

namespace Andril.Social.Personality;

/// <summary>
/// Deterministic Social personality archetype classifier implementing SOC-01 / SOC-02 rules.
/// Evaluates completed V1 personality questionnaire responses across three orthogonal interaction axes:
/// Social Engagement (q01, q07, q09), Communication (q03, q05, q06), and Planning (q10).
/// </summary>
public static class PersonalityArchetypeClassifier
{
    private static readonly HashSet<string> ValidQuestionIds = new(StringComparer.Ordinal)
    {
        "q01", "q02", "q03", "q04", "q05", "q06", "q07", "q08", "q09", "q10", "q11", "q12"
    };

    private static readonly string[] SocialQuestions = ["q01", "q07", "q09"];
    private static readonly string[] CommunicationQuestions = ["q03", "q05", "q06"];
    private const string PlanningQuestion = "q10";

    /// <summary>
    /// Classifies a completed V1 questionnaire submission into an interaction archetype.
    /// Fails closed if the submission is incomplete, invalid, duplicate, or contains unauthorized overrides.
    /// </summary>
    /// <param name="answers">The raw questionnaire answers.</param>
    /// <param name="version">The questionnaire definition version (must be 'v1').</param>
    /// <param name="currentRevision">The authoritative revision identifier of these answers.</param>
    /// <param name="confirmation">Optional voluntary confirmation for neutral unresolved axes.</param>
    public static PersonalityArchetypeClassificationResult Classify(
        IReadOnlyList<QuestionnaireAnswer>? answers,
        string? version,
        string? currentRevision,
        PersonalityArchetypeConfirmation? confirmation = null)
    {
        if (answers is null || answers.Count == 0)
        {
            return PersonalityArchetypeClassificationResult.Failed("Answers collection cannot be null or empty.");
        }

        if (!string.Equals(version, QuestionnaireV1Definition.Version, StringComparison.Ordinal))
        {
            return PersonalityArchetypeClassificationResult.Failed($"Unsupported questionnaire definition version '{version}'. Expected '{QuestionnaireV1Definition.Version}'.");
        }

        if (answers.Count != 12)
        {
            return PersonalityArchetypeClassificationResult.Failed($"Incomplete answers collection. Expected exactly 12 answers, found {answers.Count}.");
        }

        var parsedScores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (answer is null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Null answer entity encountered.");
            }

            if (string.IsNullOrWhiteSpace(answer.QuestionId) || !ValidQuestionIds.Contains(answer.QuestionId))
            {
                return PersonalityArchetypeClassificationResult.Failed($"Unknown or invalid question identifier '{answer.QuestionId}'.");
            }

            if (parsedScores.ContainsKey(answer.QuestionId))
            {
                return PersonalityArchetypeClassificationResult.Failed($"Duplicate answer for question '{answer.QuestionId}'.");
            }

            if (string.IsNullOrWhiteSpace(answer.OptionId) ||
                !int.TryParse(answer.OptionId, out var score) ||
                score < 1 || score > 5)
            {
                return PersonalityArchetypeClassificationResult.Failed($"Invalid option value '{answer.OptionId}' for question '{answer.QuestionId}'. Must be integer 1 to 5.");
            }

            parsedScores[answer.QuestionId] = score;
        }

        if (parsedScores.Count != 12)
        {
            return PersonalityArchetypeClassificationResult.Failed("One or more required questions are missing answers.");
        }

        // Validate confirmation revision freshness if confirmation provided
        PersonalityArchetypeConfirmation? validConfirmation = null;
        if (confirmation is not null)
        {
            if (!string.IsNullOrWhiteSpace(currentRevision) &&
                !string.Equals(confirmation.QuestionnaireRevision, currentRevision, StringComparison.Ordinal))
            {
                // Confirmation is stale: bound to an older revision
                validConfirmation = null;
            }
            else
            {
                validConfirmation = confirmation;
            }
        }

        // 1. Social Engagement Axis (q01, q07, q09)
        var socialSum = SocialQuestions.Sum(q => parsedScores[q]);
        var socialMean = socialSum / 3.0;
        SocialEngagementTendency? socialTendency = null;

        if (socialMean < 3.0)
        {
            if (validConfirmation?.SocialConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'Reserved' social engagement axis.");
            }
            socialTendency = SocialEngagementTendency.Reserved;
        }
        else if (socialMean > 3.0)
        {
            if (validConfirmation?.SocialConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'Outgoing' social engagement axis.");
            }
            socialTendency = SocialEngagementTendency.Outgoing;
        }
        else
        {
            // Neutral (exactly 3.0) -> check voluntary confirmation
            if (validConfirmation?.SocialConfirmation is not null)
            {
                socialTendency = validConfirmation.SocialConfirmation.Value;
            }
        }

        // 2. Communication Axis (q03, q05, q06)
        var commSum = CommunicationQuestions.Sum(q => parsedScores[q]);
        var commMean = commSum / 3.0;
        CommunicationTendency? commTendency = null;

        if (commMean < 3.0)
        {
            if (validConfirmation?.CommunicationConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'LessExpressive' communication axis.");
            }
            commTendency = CommunicationTendency.LessExpressive;
        }
        else if (commMean > 3.0)
        {
            if (validConfirmation?.CommunicationConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'Expressive' communication axis.");
            }
            commTendency = CommunicationTendency.Expressive;
        }
        else
        {
            // Neutral (exactly 3.0) -> check voluntary confirmation
            if (validConfirmation?.CommunicationConfirmation is not null)
            {
                commTendency = validConfirmation.CommunicationConfirmation.Value;
            }
        }

        // 3. Planning Axis (q10)
        var planningScore = parsedScores[PlanningQuestion];
        PlanningTendency? planningTendency = null;

        if (planningScore < 3)
        {
            if (validConfirmation?.PlanningConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'Spontaneous' planning axis.");
            }
            planningTendency = PlanningTendency.Spontaneous;
        }
        else if (planningScore > 3)
        {
            if (validConfirmation?.PlanningConfirmation is not null)
            {
                return PersonalityArchetypeClassificationResult.Failed("Invalid confirmation override: cannot override computed 'Structured' planning axis.");
            }
            planningTendency = PlanningTendency.Structured;
        }
        else
        {
            // Neutral (exactly 3) -> check voluntary confirmation
            if (validConfirmation?.PlanningConfirmation is not null)
            {
                planningTendency = validConfirmation.PlanningConfirmation.Value;
            }
        }

        // Check for any unresolved axes
        var unresolved = new List<string>(3);
        if (socialTendency is null) unresolved.Add("SocialEngagement");
        if (commTendency is null) unresolved.Add("Communication");
        if (planningTendency is null) unresolved.Add("Planning");

        if (unresolved.Count > 0)
        {
            return PersonalityArchetypeClassificationResult.Unresolved(
                socialTendency, commTendency, planningTendency, unresolved);
        }

        var archetype = PersonalityArchetype.FromAxes(
            socialTendency!.Value,
            commTendency!.Value,
            planningTendency!.Value);

        return PersonalityArchetypeClassificationResult.Success(
            archetype,
            socialTendency.Value,
            commTendency.Value,
            planningTendency.Value);
    }
}
