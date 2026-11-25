using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialScoringProfileEvidenceTests
{
    private static SocialExplorePreferences DefaultPreferences =>
        SocialExplorePreferences.Default;

    [Fact]
    public void Constructor_ValidParameters_InstantiatesCorrectly()
    {
        var id = Guid.NewGuid();
        var evidence = new SocialScoringProfileEvidence(
            id,
            25,
            "demographic-unspecified",
            DefaultPreferences);

        Assert.Equal(id, evidence.ProfileId);
        Assert.Equal(25, evidence.Age);
        Assert.Equal("demographic-unspecified", evidence.DemographicGender);
        Assert.True(evidence.IsOptedIn);
        Assert.True(evidence.IsActive);
        Assert.True(evidence.IsDiscoverable);
        Assert.True(evidence.HasAuthoritativeEvidence);
    }

    [Fact]
    public void Constructor_EmptyProfileId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SocialScoringProfileEvidence(Guid.Empty, 25, null, DefaultPreferences));
    }

    [Fact]
    public void Constructor_NegativeAge_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialScoringProfileEvidence(Guid.NewGuid(), -1, null, DefaultPreferences));
    }

    [Fact]
    public void Constructor_NullPreferences_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SocialScoringProfileEvidence(Guid.NewGuid(), 25, null, null!));
    }

    [Fact]
    public void Constructor_DefensivelySnapshotsCollections()
    {
        var langList = new List<string> { "en", "fr" };
        var goalList = new List<string> { "friendship" };
        var interestList = new List<Guid> { Guid.NewGuid() };
        var answerList = new List<QuestionnaireAnswer> { new("q01", "3") };
        var blockedList = new List<Guid> { Guid.NewGuid() };

        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            28,
            "other",
            DefaultPreferences,
            confirmedLanguages: langList,
            confirmedConnectionGoals: goalList,
            confirmedSocialInterestIds: interestList,
            questionnaireAnswers: answerList,
            blockedProfileIds: blockedList);

        langList.Add("es");
        goalList.Add("dating");
        interestList.Add(Guid.NewGuid());
        answerList.Add(new("q02", "4"));
        blockedList.Add(Guid.NewGuid());

        Assert.Equal(2, evidence.ConfirmedLanguages!.Count);
        Assert.DoesNotContain("es", evidence.ConfirmedLanguages);

        Assert.NotNull(evidence.ConfirmedConnectionGoals);
        Assert.Single(evidence.ConfirmedConnectionGoals!);
        Assert.DoesNotContain("dating", evidence.ConfirmedConnectionGoals!);

        Assert.Single(evidence.ConfirmedSocialInterestIds!);

        Assert.Single(evidence.QuestionnaireAnswers!);

        Assert.Single(evidence.BlockedProfileIds);
    }

    [Fact]
    public void Constructor_FiltersOutEmptyOrWhitespaceEntries()
    {
        var langList = new List<string> { "en", "", "   ", "es" };
        var goalList = new List<string> { "friendship", "  " };
        var interestList = new List<Guid> { Guid.NewGuid(), Guid.Empty };
        var blockedList = new List<Guid> { Guid.NewGuid(), Guid.Empty };

        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            30,
            "female",
            DefaultPreferences,
            confirmedLanguages: langList,
            confirmedConnectionGoals: goalList,
            confirmedSocialInterestIds: interestList,
            blockedProfileIds: blockedList);

        Assert.Equal(2, evidence.ConfirmedLanguages!.Count);
        Assert.Single(evidence.ConfirmedConnectionGoals!);
        Assert.Single(evidence.ConfirmedSocialInterestIds!);
        Assert.Single(evidence.BlockedProfileIds);
    }

    [Fact]
    public void CreateAuthoritativeDiscovery_InstantiatesWithExplicitFlags()
    {
        var id = Guid.NewGuid();
        var evidence = SocialScoringProfileEvidence.CreateAuthoritativeDiscovery(
            id,
            24,
            "male",
            DefaultPreferences,
            isOptedIn: false,
            isActive: false,
            isDiscoverable: false);

        Assert.Equal(id, evidence.ProfileId);
        Assert.False(evidence.IsOptedIn);
        Assert.False(evidence.IsActive);
        Assert.False(evidence.IsDiscoverable);
        Assert.True(evidence.HasAuthoritativeEvidence);
    }

    [Fact]
    public void ToPersonalityEvaluationProfile_TransfersCanonicalAnswersAccurately()
    {
        var responseId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var definition = QuestionnaireV1Definition.Create();
        var answerEntity = new QuestionnaireAnswerEntity(
            Guid.NewGuid(),
            responseId,
            "q01",
            "3",
            DateTimeOffset.UtcNow);

        var response = QuestionnaireResponseEntity.Create(
            responseId,
            profileId,
            definition,
            new[] { answerEntity },
            skip: false,
            DateTimeOffset.UtcNow);

        var evidence = new SocialScoringProfileEvidence(
            profileId,
            22,
            "other",
            DefaultPreferences,
            questionnaireResponse: response);

        var profile = evidence.ToPersonalityEvaluationProfile();

        Assert.NotNull(profile);
        Assert.NotNull(profile!.Answers);
        Assert.Single(profile.Answers!);
        Assert.Equal("3", profile.Answers[0].OptionId);
        Assert.Equal("q01", profile.Answers[0].QuestionId);
    }

    [Fact]
    public void ToString_ReturnsSafeConstantRepresentation()
    {
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(), 26, "female", DefaultPreferences);

        Assert.Equal("SocialScoringProfileEvidence [private]", evidence.ToString());
    }

    [Fact]
    public void Serialization_HidesAllProperties()
    {
        var id = Guid.NewGuid();
        var evidence = new SocialScoringProfileEvidence(
            id,
            27,
            "secret-gender",
            DefaultPreferences,
            confirmedLanguages: new[] { "secret-lang" });

        var json = JsonSerializer.Serialize(evidence);

        Assert.DoesNotContain(id.ToString(), json);
        Assert.DoesNotContain("secret-gender", json);
        Assert.DoesNotContain("secret-lang", json);
        Assert.DoesNotContain("DemographicGender", json);
        Assert.DoesNotContain("ConfirmedLanguages", json);
    }

    [Fact]
    public void Collections_ThrowInvalidCastException_WhenCastToMutableTypes()
    {
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "other",
            DefaultPreferences,
            confirmedLanguages: new[] { "en" },
            confirmedConnectionGoals: new[] { "friendship" },
            confirmedSocialInterestIds: new[] { Guid.NewGuid() },
            questionnaireAnswers: new[] { new QuestionnaireAnswer("q01", "3") },
            blockedProfileIds: new[] { Guid.NewGuid() });

        Assert.Throws<InvalidCastException>(() => (HashSet<Guid>)evidence.BlockedProfileIds);
        Assert.Throws<InvalidCastException>(() => (HashSet<string>)evidence.ConfirmedLanguages!);
        Assert.Throws<InvalidCastException>(() => (HashSet<string>)evidence.ConfirmedConnectionGoals!);
        Assert.Throws<InvalidCastException>(() => (HashSet<Guid>)evidence.ConfirmedSocialInterestIds!);
        Assert.Throws<InvalidCastException>(() => (QuestionnaireAnswer[])evidence.QuestionnaireAnswers!);
        Assert.Throws<InvalidCastException>(() => (List<QuestionnaireAnswer>)evidence.QuestionnaireAnswers!);
    }

    [Fact]
    public void Collections_ThrowNotSupportedException_WhenMutatedViaICollection()
    {
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "other",
            DefaultPreferences,
            confirmedLanguages: new[] { "en" },
            confirmedConnectionGoals: new[] { "friendship" },
            confirmedSocialInterestIds: new[] { Guid.NewGuid() },
            questionnaireAnswers: new[] { new QuestionnaireAnswer("q01", "3") },
            blockedProfileIds: new[] { Guid.NewGuid() });

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<Guid>)evidence.BlockedProfileIds).Add(Guid.NewGuid()));

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<string>)evidence.ConfirmedLanguages!).Add("de"));

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<string>)evidence.ConfirmedConnectionGoals!).Add("networking"));

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<Guid>)evidence.ConfirmedSocialInterestIds!).Add(Guid.NewGuid()));

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<QuestionnaireAnswer>)evidence.QuestionnaireAnswers!).Add(new("q09", "1")));
    }

    [Fact]
    public void BlockedProfileIds_WhenNullOrEmpty_IsNonNullImmutableEmptySet()
    {
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "other",
            DefaultPreferences,
            blockedProfileIds: null);

        Assert.NotNull(evidence.BlockedProfileIds);
        Assert.Empty(evidence.BlockedProfileIds);
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<Guid>)evidence.BlockedProfileIds).Add(Guid.NewGuid()));
    }

    [Fact]
    public void BlockedProfileIds_DeduplicatesEntriesAndFiltersEmpty()
    {
        var duplicateId = Guid.NewGuid();
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "other",
            DefaultPreferences,
            blockedProfileIds: new[] { duplicateId, Guid.Empty, duplicateId });

        Assert.Single(evidence.BlockedProfileIds);
        Assert.Contains(duplicateId, evidence.BlockedProfileIds);
        Assert.DoesNotContain(Guid.Empty, evidence.BlockedProfileIds);
    }

    [Fact]
    public void StringCollections_PreserveCaseInsensitiveMatching()
    {
        var evidence = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "other",
            DefaultPreferences,
            confirmedLanguages: new[] { "en", "FR" },
            confirmedConnectionGoals: new[] { "friendship", "DATING" });

        Assert.NotNull(evidence.ConfirmedLanguages);
        Assert.True(evidence.ConfirmedLanguages!.Contains("EN"));
        Assert.True(evidence.ConfirmedLanguages!.Contains("fr"));

        Assert.NotNull(evidence.ConfirmedConnectionGoals);
        Assert.True(evidence.ConfirmedConnectionGoals!.Contains("FRIENDSHIP"));
        Assert.True(evidence.ConfirmedConnectionGoals!.Contains("dating"));
    }
}
