using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialRankedDiscoveryServiceTests
{
    private static SocialScoringProfileEvidence CreateProfile(
        Guid? profileId = null,
        int age = 25,
        string? demographicGender = "female",
        SocialGenderPreference genderPreference = SocialGenderPreference.All,
        int minAge = 18,
        int maxAge = 50,
        int maxDistanceKm = 100,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool hasAuthoritativeEvidence = true,
        IEnumerable<Guid>? blockedProfileIds = null,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? goals = null,
        IEnumerable<Guid>? interestIds = null,
        IEnumerable<QuestionnaireAnswer>? answers = null)
    {
        var rangeFilters = new SocialExploreRangeFilters(
            minAge: minAge,
            maxAge: maxAge,
            minDistanceKm: 0,
            maxDistanceKm: maxDistanceKm);

        var prefs = new SocialExplorePreferences(
            rangeFilters,
            genderPreference,
            languages: languages ?? new[] { "English" },
            connectionGoals: goals ?? new[] { "Friendship" });

        return new SocialScoringProfileEvidence(
            profileId ?? Guid.NewGuid(),
            age,
            demographicGender,
            prefs,
            confirmedLanguages: languages ?? new[] { "English" },
            confirmedConnectionGoals: goals ?? new[] { "Friendship" },
            confirmedSocialInterestIds: interestIds,
            questionnaireAnswers: answers,
            isOptedIn: isOptedIn,
            isActive: isActive,
            isDiscoverable: isDiscoverable,
            hasAuthoritativeEvidence: hasAuthoritativeEvidence,
            blockedProfileIds: blockedProfileIds);
    }

    [Fact]
    public void DiscoverRankedCandidates_CallerUnderage_ReturnsEmptyListImmediately()
    {
        var caller = CreateProfile(age: 17);
        var candidate = CreateProfile(age: 25);

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (candidate, (int?)10) });

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void DiscoverRankedCandidates_CallerIneligible_ReturnsEmptyListImmediately(
        bool isOptedIn, bool isActive, bool hasAuthoritativeEvidence)
    {
        var caller = CreateProfile(
            isOptedIn: isOptedIn,
            isActive: isActive,
            hasAuthoritativeEvidence: hasAuthoritativeEvidence);
        var candidate = CreateProfile();

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (candidate, (int?)10) });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverRankedCandidates_SelfCandidate_Excluded()
    {
        var id = Guid.NewGuid();
        var caller = CreateProfile(profileId: id);
        var selfCandidate = CreateProfile(profileId: id);

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (selfCandidate, (int?)5) });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverRankedCandidates_BidirectionalBlocking_ExcludesBlockedCandidates()
    {
        var callerId = Guid.NewGuid();
        var candAId = Guid.NewGuid();
        var candBId = Guid.NewGuid();
        var candCId = Guid.NewGuid();

        // Caller blocks Cand A
        var caller = CreateProfile(profileId: callerId, blockedProfileIds: new[] { candAId });
        var candA = CreateProfile(profileId: candAId);

        // Cand B blocks Caller
        var candB = CreateProfile(profileId: candBId, blockedProfileIds: new[] { callerId });

        // Cand C has no blocks
        var candC = CreateProfile(profileId: candCId);

        var candidates = new[]
        {
            (candA, (int?)10),
            (candB, (int?)10),
            (candC, (int?)10)
        };

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(caller, candidates);

        Assert.Single(result);
        Assert.Equal(candCId, result[0].CandidateProfileId);
    }

    [Theory]
    [InlineData(17, true, true, true, true)]    // underage
    [InlineData(25, false, true, true, true)]   // not opted in
    [InlineData(25, true, false, true, true)]   // not active
    [InlineData(25, true, true, false, true)]   // not discoverable (hidden)
    [InlineData(25, true, true, true, false)]   // missing authoritative evidence
    public void DiscoverRankedCandidates_CandidateIneligible_Excluded(
        int age, bool isOptedIn, bool isActive, bool isDiscoverable, bool hasEvidence)
    {
        var caller = CreateProfile();
        var candidate = CreateProfile(
            age: age,
            isOptedIn: isOptedIn,
            isActive: isActive,
            isDiscoverable: isDiscoverable,
            hasAuthoritativeEvidence: hasEvidence);

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (candidate, (int?)10) });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverRankedCandidates_HardFilterAgeMismatch_Excluded()
    {
        // Caller wants 20..30
        var caller = CreateProfile(age: 25, minAge: 20, maxAge: 30);
        // Candidate is 35 (outside caller's preferred range)
        var candTooOld = CreateProfile(age: 35, minAge: 20, maxAge: 30);

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (candTooOld, (int?)10) });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverRankedCandidates_HardFilterGenderMismatch_Excluded()
    {
        // Caller wants Women only
        var caller = CreateProfile(
            demographicGender: "male",
            genderPreference: SocialGenderPreference.Women);

        // Candidate is Male (wants All)
        var candMale = CreateProfile(
            demographicGender: "male",
            genderPreference: SocialGenderPreference.All);

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(
            caller, new[] { (candMale, (int?)10) });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverRankedCandidates_EligibleCandidates_RankedDeterministically()
    {
        var caller = CreateProfile(
            age: 26,
            demographicGender: "female",
            genderPreference: SocialGenderPreference.All,
            languages: new[] { "English", "Spanish" },
            goals: new[] { "Friendship", "Dating" });

        // Candidate 1: High match (shares languages and goals, close distance)
        var cand1 = CreateProfile(
            age: 27,
            demographicGender: "male",
            genderPreference: SocialGenderPreference.All,
            languages: new[] { "English", "Spanish" },
            goals: new[] { "Friendship", "Dating" });

        // Candidate 2: Moderate match (shares English, different goals, further distance)
        var cand2 = CreateProfile(
            age: 29,
            demographicGender: "male",
            genderPreference: SocialGenderPreference.All,
            languages: new[] { "English" },
            goals: new[] { "Companionship" });

        var candidates = new[]
        {
            (cand2, (int?)50),
            (cand1, (int?)5)
        };

        var result = SocialRankedDiscoveryService.DiscoverRankedCandidates(caller, candidates);

        Assert.Equal(2, result.Count);
        // cand1 has higher compatibility and coverage, should rank first
        Assert.Equal(cand1.ProfileId, result[0].CandidateProfileId);
        Assert.Equal(cand2.ProfileId, result[1].CandidateProfileId);
    }

    [Fact]
    public void DiscoverRankedCandidates_NullArguments_ThrowsException()
    {
        var validProfile = CreateProfile();

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverRankedCandidates(null!, Array.Empty<(SocialScoringProfileEvidence, int?)>()));

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverRankedCandidates(validProfile, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialRankedDiscoveryService.DiscoverRankedCandidates(
                validProfile, Array.Empty<(SocialScoringProfileEvidence, int?)>(), rotationOffset: -1));
    }
}
