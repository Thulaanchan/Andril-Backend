using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Social.Matching;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// Security regression tests enforcing strict privacy safeguards for <see cref="SocialExploreCandidateCard"/>.
/// Verifies defensive collection encapsulation, zero account ID or raw questionnaire response leakage,
/// strict property surface limits, serialization quarantine, and diagnostic string redaction.
/// </summary>
public sealed class SocialExploreResultPrivacyRegressionTests
{
    private static readonly Guid CandidateProfileId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    private static SocialCandidateEvaluationResult CreateEligibleEvaluation(
        List<string>? languages = null,
        List<string>? connectionGoals = null)
    {
        return new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Eligible,
            exclusionReasons: null,
            indeterminateReasons: null,
            sharedLanguages: languages ?? new List<string> { "English", "French" },
            sharedConnectionGoals: connectionGoals ?? new List<string> { "Friendship", "Mentorship" });
    }

    private static SocialExploreCandidateCard CreateValidCard(
        Guid? profileId = null,
        string displayName = "TargetCandidate",
        int age = 27,
        int? distanceKm = 12,
        SocialCandidateEvaluationResult? evaluationResult = null)
    {
        return SocialExploreCandidateCard.Create(
            profileId ?? CandidateProfileId,
            displayName,
            age,
            distanceKm,
            evaluationResult ?? CreateEligibleEvaluation());
    }

    #region 1. Defensive Collections and Immutability

    [Fact]
    public void CandidateCard_CollectionsAreDefensiveSnapshots_UnaffectedBySourceMutations()
    {
        var sourceLanguages = new List<string> { "English", "Spanish" };
        var sourceGoals = new List<string> { "Networking" };
        var evaluation = CreateEligibleEvaluation(sourceLanguages, sourceGoals);

        var card = CreateValidCard(evaluationResult: evaluation);

        // Mutate source collections
        sourceLanguages.Add("German");
        sourceGoals.Add("Dating");

        Assert.Equal(2, card.SharedLanguages.Count);
        Assert.DoesNotContain("German", card.SharedLanguages);
        Assert.Single(card.SharedConnectionGoals);
        Assert.DoesNotContain("Dating", card.SharedConnectionGoals);
    }

    [Fact]
    public void CandidateCard_CollectionsCannotBeCastToMutableList()
    {
        var card = CreateValidCard();

        Assert.IsNotType<List<string>>(card.SharedLanguages);
        Assert.IsNotType<List<string>>(card.SharedConnectionGoals);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)card.SharedLanguages).Add("MaliciousEntry"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)card.SharedConnectionGoals).Add("MaliciousEntry"));
    }

    #endregion

    #region 2. Strict Surface Inspection — Zero Account ID or Questionnaire Leakage

    [Fact]
    public void CandidateCard_PublicProperties_ConformToStrictApprovedAllowlist()
    {
        var approvedPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(SocialExploreCandidateCard.CandidateProfileId),
            nameof(SocialExploreCandidateCard.DisplayName),
            nameof(SocialExploreCandidateCard.Age),
            nameof(SocialExploreCandidateCard.ApproximateDistanceKm),
            nameof(SocialExploreCandidateCard.SharedLanguages),
            nameof(SocialExploreCandidateCard.SharedConnectionGoals)
        };

        var publicProperties = typeof(SocialExploreCandidateCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var actualPropertyNames = publicProperties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(approvedPropertyNames, actualPropertyNames);
    }

    [Fact]
    public void CandidateCard_DoesNotExposeSensitiveAccountOrQuestionnaireFields()
    {
        var allProperties = typeof(SocialExploreCandidateCard)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var prohibitedSubstrings = new[]
        {
            "Account",
            "Subject",
            "Email",
            "Phone",
            "Password",
            "Credential",
            "Token",
            "Questionnaire",
            "Answer",
            "Score",
            "Percentage",
            "Compatibility",
            "Rank",
            "Weight",
            "Gps",
            "Latitude",
            "Longitude",
            "BirthDate"
        };

        foreach (var property in allProperties)
        {
            foreach (var prohibited in prohibitedSubstrings)
            {
                Assert.DoesNotContain(prohibited, property.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region 3. Transport Serialization Quarantine

    [Fact]
    public void CandidateCard_AllPublicProperties_HaveJsonIgnoreAttribute()
    {
        var publicProperties = typeof(SocialExploreCandidateCard)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in publicProperties)
        {
            var hasJsonIgnore = prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null;
            Assert.True(hasJsonIgnore, $"Property '{prop.Name}' lacks [JsonIgnore] attribute.");
        }
    }

    [Fact]
    public void CandidateCard_JsonSerialization_ProducesEmptyObjectWithoutSensitiveLeakage()
    {
        var card = CreateValidCard(
            profileId: CandidateProfileId,
            displayName: "PrivateAlice",
            age: 29,
            distanceKm: 5);

        var serializedJson = JsonSerializer.Serialize(card);

        // Must serialize strictly to an empty JSON object `{}`
        Assert.Equal("{}", serializedJson);

        // Explicit non-leakage verification
        Assert.DoesNotContain(CandidateProfileId.ToString(), serializedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateAlice", serializedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("29", serializedJson);
        Assert.DoesNotContain("English", serializedJson, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region 4. Diagnostic Redaction Safeguards

    [Fact]
    public void CandidateCard_ToString_ReturnsConstantNonDisclosingString()
    {
        var card = CreateValidCard(
            profileId: CandidateProfileId,
            displayName: "ClassifiedName",
            age: 33,
            distanceKm: 18);

        var diagnosticString = card.ToString();

        Assert.Equal("SocialExploreCandidateCard [private]", diagnosticString);
        Assert.DoesNotContain(CandidateProfileId.ToString(), diagnosticString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClassifiedName", diagnosticString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("33", diagnosticString);
        Assert.DoesNotContain("18", diagnosticString);
    }

    #endregion

    #region 5. Fail-Closed Creation Invariants

    [Fact]
    public void CandidateCard_ExcludedOrIndeterminateEvaluation_ThrowsArgumentException()
    {
        var excludedEvaluation = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Excluded,
            new[] { SocialCandidateExclusionReason.CandidateUnderage },
            null);

        var indeterminateEvaluation = new SocialCandidateEvaluationResult(
            SocialCandidateMatchStatus.Indeterminate,
            null,
            new[] { SocialCandidateIndeterminateReason.DistanceUnavailable });

        Assert.Throws<ArgumentException>(() =>
            SocialExploreCandidateCard.Create(CandidateProfileId, "Name", 25, 10, excludedEvaluation));

        Assert.Throws<ArgumentException>(() =>
            SocialExploreCandidateCard.Create(CandidateProfileId, "Name", 25, 10, indeterminateEvaluation));
    }

    [Fact]
    public void CandidateCard_UnderageCandidate_ThrowsArgumentOutOfRangeException()
    {
        var evaluation = CreateEligibleEvaluation();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialExploreCandidateCard.Create(CandidateProfileId, "UnderageName", 17, 10, evaluation));
    }

    [Fact]
    public void CandidateCard_EmptyCandidateProfileId_ThrowsArgumentException()
    {
        var evaluation = CreateEligibleEvaluation();

        Assert.Throws<ArgumentException>(() =>
            SocialExploreCandidateCard.Create(Guid.Empty, "ValidName", 25, 10, evaluation));
    }

    #endregion
}
