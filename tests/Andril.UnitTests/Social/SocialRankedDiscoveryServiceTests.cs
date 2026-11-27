using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
        IEnumerable<QuestionnaireAnswer>? answers = null,
        QuestionnaireResponseEntity? response = null,
        PersonalityPartnerPreferences? partnerPreferences = null,
        PersonalityArchetypeConfirmation? confirmation = null,
        string? questionnaireVersion = null,
        string? questionnaireRevision = null)
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
            questionnaireResponse: response,
            partnerPreferences: partnerPreferences,
            archetypeConfirmation: confirmation,
            questionnaireVersion: questionnaireVersion,
            questionnaireRevision: questionnaireRevision,
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

    private static SocialEvidenceRevisionSnapshot CreateSnapshot(
        Guid profileId,
        string policyVersion = SocialCompatibilityScoringEngine.PolicyVersion,
        long questionnaireRevision = 1,
        long partnerPreferencesRevision = 1,
        long demographicsRevision = 1,
        bool isProfileActive = true,
        bool isAccountRestricted = false)
    {
        return new SocialEvidenceRevisionSnapshot(
            profileId,
            policyVersion: policyVersion,
            questionnaireRevision: questionnaireRevision,
            partnerPreferencesRevision: partnerPreferencesRevision,
            demographicsRevision: demographicsRevision,
            isProfileActive: isProfileActive,
            isAccountRestricted: isAccountRestricted);
    }

    private static SocialAuthoritativeCandidateItem CreateCandidateItem(
        SocialScoringProfileEvidence evidence,
        SocialEvidenceRevisionSnapshot? cached = null,
        SocialEvidenceRevisionSnapshot? committed = null,
        string displayName = "CandidateUser",
        int? distanceKm = 10)
    {
        var c = cached ?? CreateSnapshot(evidence.ProfileId);
        var com = committed ?? CreateSnapshot(evidence.ProfileId);
        return new SocialAuthoritativeCandidateItem(evidence, c, com, displayName, distanceKm);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_MissingAuthoritativeEvidence_FailsClosed()
    {
        var callerWithoutEvidence = CreateProfile(hasAuthoritativeEvidence: false);
        var callerSnap = CreateSnapshot(callerWithoutEvidence.ProfileId);
        var cand = CreateProfile();
        var candItem = CreateCandidateItem(cand);

        // Caller lacks authoritative evidence
        var resultCaller = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            callerWithoutEvidence, callerSnap, callerSnap, new[] { candItem });
        Assert.Empty(resultCaller);

        // Candidate lacks authoritative evidence
        var caller = CreateProfile();
        var callerValidSnap = CreateSnapshot(caller.ProfileId);
        var candWithoutEvidence = CreateProfile(hasAuthoritativeEvidence: false);
        var candWithoutEvidenceItem = CreateCandidateItem(candWithoutEvidence);

        var resultCandidate = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerValidSnap, callerValidSnap, new[] { candWithoutEvidenceItem });
        Assert.Empty(resultCandidate);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_StaleCallerRevisions_ReturnsEmptyListImmediately()
    {
        var caller = CreateProfile();
        var cached = CreateSnapshot(caller.ProfileId, questionnaireRevision: 1);
        var committed = CreateSnapshot(caller.ProfileId, questionnaireRevision: 2); // stale revision!

        var cand = CreateProfile();
        var candItem = CreateCandidateItem(cand);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, cached, committed, new[] { candItem });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_StaleCandidateRevisions_ExcludesStaleCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var staleCand = CreateProfile();
        var staleItem = CreateCandidateItem(
            staleCand,
            cached: CreateSnapshot(staleCand.ProfileId, questionnaireRevision: 1),
            committed: CreateSnapshot(staleCand.ProfileId, questionnaireRevision: 2));

        var freshCand = CreateProfile();
        var freshItem = CreateCandidateItem(freshCand);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { staleItem, freshItem });

        Assert.Single(result);
        Assert.Equal(freshCand.ProfileId, result[0].CandidateProfileId);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_PolicyVersionMismatch_ExcludesCandidateOrReturnsEmptyForCaller()
    {
        var caller = CreateProfile();
        var callerMismatchedSnap = CreateSnapshot(caller.ProfileId, policyVersion: "v2.0-unsupported");
        var cand = CreateProfile();
        var candItem = CreateCandidateItem(cand);

        // Caller policy mismatch -> returns empty
        var resultCaller = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerMismatchedSnap, callerMismatchedSnap, new[] { candItem });
        Assert.Empty(resultCaller);

        // Candidate policy mismatch -> candidate excluded
        var callerValidSnap = CreateSnapshot(caller.ProfileId);
        var candMismatchedItem = CreateCandidateItem(
            cand,
            cached: CreateSnapshot(cand.ProfileId, policyVersion: "v0.9-legacy"),
            committed: CreateSnapshot(cand.ProfileId, policyVersion: "v0.9-legacy"));

        var resultCandidate = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerValidSnap, callerValidSnap, new[] { candMismatchedItem });
        Assert.Empty(resultCandidate);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_DuplicateCandidateIdentities_DeduplicatesIdenticalEvidence()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var cand = CreateProfile();
        var candItem1 = CreateCandidateItem(cand, displayName: "CandidateDuplicate");
        var candItem2 = CreateCandidateItem(cand, displayName: "CandidateDuplicate");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { candItem1, candItem2 });

        Assert.Single(result);
        Assert.Equal(cand.ProfileId, result[0].CandidateProfileId);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ContradictoryIdentityEvidence_ExcludesContradictoryCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var cand = CreateProfile();
        // Contradictory: Committed snapshot has a different profileId from Evidence!
        var contradictoryItem = new SocialAuthoritativeCandidateItem(
            cand,
            CreateSnapshot(cand.ProfileId),
            CreateSnapshot(Guid.NewGuid()),
            "ContradictoryUser");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { contradictoryItem });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_BoundedCandidateProcessing_RespectsBatchSizeLimit()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var items = Enumerable.Range(1, 5)
            .Select(i => CreateCandidateItem(CreateProfile(), displayName: $"User_{i}"))
            .ToList();

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, items, maxCandidateBatchSize: 2);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_IndeterminateHardFilters_ExcludesCandidate()
    {
        // Caller requires Women
        var caller = CreateProfile(
            demographicGender: "male",
            genderPreference: SocialGenderPreference.Women);
        var callerSnap = CreateSnapshot(caller.ProfileId);

        // Candidate has unresolved/null gender demographic -> Hard filter returns Indeterminate
        var candidateIndeterminate = CreateProfile(
            demographicGender: null,
            genderPreference: SocialGenderPreference.All);
        var candItem = CreateCandidateItem(candidateIndeterminate);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { candItem });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_BidirectionalBlocking_ExcludesBlockedCandidates()
    {
        var callerId = Guid.NewGuid();
        var candAId = Guid.NewGuid();
        var candBId = Guid.NewGuid();
        var candCId = Guid.NewGuid();

        // Caller blocks A
        var caller = CreateProfile(profileId: callerId, blockedProfileIds: new[] { candAId });
        var callerSnap = CreateSnapshot(callerId);

        var candA = CreateProfile(profileId: candAId);
        // B blocks Caller
        var candB = CreateProfile(profileId: candBId, blockedProfileIds: new[] { callerId });
        // C has no blocks
        var candC = CreateProfile(profileId: candCId);

        var items = new[]
        {
            CreateCandidateItem(candA, displayName: "BlockedByCaller"),
            CreateCandidateItem(candB, displayName: "BlocksCaller"),
            CreateCandidateItem(candC, displayName: "UnblockedCandidate")
        };

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, items);

        Assert.Single(result);
        Assert.Equal(candCId, result[0].CandidateProfileId);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_PreferenceOnlyStrongRecommendations_PreservesRankingPriority()
    {
        var caller = CreateProfile(
            languages: new[] { "English", "French" },
            goals: new[] { "Friendship", "Dating" });
        var callerSnap = CreateSnapshot(caller.ProfileId);

        // Cand 1: High overlap (shares both languages and goals, close distance)
        var cand1 = CreateProfile(
            languages: new[] { "English", "French" },
            goals: new[] { "Friendship", "Dating" });
        var item1 = CreateCandidateItem(cand1, displayName: "StrongMatch", distanceKm: 5);

        // Cand 2: Low overlap (shares English only, different goals, further)
        var cand2 = CreateProfile(
            languages: new[] { "English" },
            goals: new[] { "Companionship" });
        var item2 = CreateCandidateItem(cand2, displayName: "WeakMatch", distanceKm: 80);

        // Pass cand2 first
        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item2, item1 });

        Assert.Equal(2, result.Count);
        Assert.Equal(cand1.ProfileId, result[0].CandidateProfileId);
        Assert.Equal(cand2.ProfileId, result[1].CandidateProfileId);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_PrivacySafeCandidateProjection_ConcealsPrivateDataAndJsonIgnores()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var candId = Guid.NewGuid();
        var cand = CreateProfile(
            profileId: candId,
            age: 28,
            languages: new[] { "English" },
            goals: new[] { "Friendship" });
        var candItem = CreateCandidateItem(cand, displayName: "Alice", distanceKm: 12);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { candItem });

        Assert.Single(result);
        var card = result[0];

        // 1. Approved properties are populated
        Assert.Equal(candId, card.CandidateProfileId);
        Assert.Equal("Alice", card.DisplayName);
        Assert.Equal(28, card.Age);
        Assert.Equal(12, card.ApproximateDistanceKm);
        Assert.Contains("English", card.SharedLanguages);
        Assert.Contains("Friendship", card.SharedConnectionGoals);

        // 2. Diagnostic ToString() is non-disclosing
        Assert.Equal("SocialExploreCandidateCard [private]", card.ToString());

        // 3. JSON serialization produces empty JSON object due to [JsonIgnore] attributes
        var json = JsonSerializer.Serialize(card);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_NullOrInvalidArguments_ThrowsExpectedExceptions()
    {
        var validProfile = CreateProfile();
        var validSnap = CreateSnapshot(validProfile.ProfileId);

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                null!, validSnap, validSnap, Array.Empty<SocialAuthoritativeCandidateItem>()));

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                validProfile, null!, validSnap, Array.Empty<SocialAuthoritativeCandidateItem>()));

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                validProfile, validSnap, null!, Array.Empty<SocialAuthoritativeCandidateItem>()));

        Assert.Throws<ArgumentNullException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                validProfile, validSnap, validSnap, null!));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                validProfile, validSnap, validSnap, Array.Empty<SocialAuthoritativeCandidateItem>(), rotationOffset: -1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
                validProfile, validSnap, validSnap, Array.Empty<SocialAuthoritativeCandidateItem>(), maxCandidateBatchSize: 0));
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_FirstDuplicateUnblocked_SecondDuplicateBlocked_ExcludesCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var candId = Guid.NewGuid();

        var candUnblocked = CreateProfile(profileId: candId, blockedProfileIds: Array.Empty<Guid>());
        var candBlocked = CreateProfile(profileId: candId, blockedProfileIds: new[] { caller.ProfileId });

        var item1 = CreateCandidateItem(candUnblocked, displayName: "CandidateA");
        var item2 = CreateCandidateItem(candBlocked, displayName: "CandidateA");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_FirstDuplicateBlocked_SecondDuplicateUnblocked_ExcludesCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var candId = Guid.NewGuid();

        var candBlocked = CreateProfile(profileId: candId, blockedProfileIds: new[] { caller.ProfileId });
        var candUnblocked = CreateProfile(profileId: candId, blockedProfileIds: Array.Empty<Guid>());

        var item1 = CreateCandidateItem(candBlocked, displayName: "CandidateA");
        var item2 = CreateCandidateItem(candUnblocked, displayName: "CandidateA");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ConflictingCandidateDisplayInformation_ExcludesCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var cand = CreateProfile();

        var item1 = CreateCandidateItem(cand, displayName: "Alice");
        var item2 = CreateCandidateItem(cand, displayName: "Bob");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ConflictingApproximateDistance_ExcludesCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var cand = CreateProfile();

        var item1 = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 10);
        var item2 = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 50);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ConflictingMatchingEvidence_LanguagesOrGoals_ExcludesCandidate()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var candId = Guid.NewGuid();

        var candLangEnglish = CreateProfile(profileId: candId, languages: new[] { "English" });
        var candLangFrench = CreateProfile(profileId: candId, languages: new[] { "French" });

        var item1 = CreateCandidateItem(candLangEnglish, displayName: "CandidateA");
        var item2 = CreateCandidateItem(candLangFrench, displayName: "CandidateA");

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_IdenticalValidDuplicateEvidence_DeduplicatesAndPreservesSingleCard()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var cand = CreateProfile();

        var item1 = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 15);
        var item2 = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 15);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2 });

        Assert.Single(result);
        Assert.Equal(cand.ProfileId, result[0].CandidateProfileId);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_LaterDuplicateAttemptsToRestoreEligibility_RemainsExcluded()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var cand = CreateProfile();

        var item1 = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 10);
        var item2Contradictory = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 80);
        var item3AttemptRestore = CreateCandidateItem(cand, displayName: "CandidateA", distanceKm: 10);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item1, item2Contradictory, item3AttemptRestore });

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ContradictoryCandidateOrdering_IsOrderIndependent()
    {
        var caller = CreateProfile();
        var callerSnap = CreateSnapshot(caller.ProfileId);
        var candId = Guid.NewGuid();

        var candNormal = CreateProfile(profileId: candId, blockedProfileIds: Array.Empty<Guid>());
        var candBlocked = CreateProfile(profileId: candId, blockedProfileIds: new[] { caller.ProfileId });

        var itemNormal = CreateCandidateItem(candNormal, displayName: "UserX");
        var itemBlocked = CreateCandidateItem(candBlocked, displayName: "UserX");

        var resultForward = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { itemNormal, itemBlocked });
        var resultReverse = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { itemBlocked, itemNormal });

        Assert.Empty(resultForward);
        Assert.Empty(resultReverse);
    }

    [Theory]
    [InlineData("display-suffix")]
    [InlineData("display-case")]
    [InlineData("answers")]
    [InlineData("response-answers")]
    [InlineData("response-missing")]
    [InlineData("response-identity")]
    [InlineData("partner-social")]
    [InlineData("partner-communication")]
    [InlineData("partner-planning")]
    [InlineData("confirmation")]
    [InlineData("confirmation-revision")]
    [InlineData("questionnaire-version")]
    [InlineData("questionnaire-revision")]
    [InlineData("blocked")]
    public void ContradictoryDuplicates_RejectInEitherOrderAndCannotBeRestored(string difference)
    {
        var caller = CreateProfile();
        var snapshot = CreateSnapshot(caller.ProfileId);
        var candidateId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var original = CreateProfile(profileId: candidateId,
            answers: [new("q01", "3")], response: CreateResponse(candidateId, responseId),
            partnerPreferences: new(), confirmation: new("r1"),
            questionnaireVersion: "v1", questionnaireRevision: "r1");
        var changed = CreateProfile(profileId: candidateId,
            answers: [new("q01", difference == "answers" ? "4" : "3")],
            response: difference == "response-missing" ? null : CreateResponse(candidateId,
                difference == "response-identity" ? Guid.NewGuid() : responseId,
                difference == "response-answers" ? "4" : "3"),
            partnerPreferences: new(
                difference == "partner-social" ? SocialEngagementPreference.Outgoing : SocialEngagementPreference.Either,
                difference == "partner-communication" ? CommunicationPreference.Expressive : CommunicationPreference.Either,
                difference == "partner-planning" ? PlanningPreference.Structured : PlanningPreference.Either),
            confirmation: new(difference == "confirmation-revision" ? "r2" : "r1",
                difference == "confirmation" ? SocialEngagementTendency.Outgoing : null),
            questionnaireVersion: difference == "questionnaire-version" ? "v2" : "v1",
            questionnaireRevision: difference == "questionnaire-revision" ? "r2" : "r1",
            blockedProfileIds: difference == "blocked" ? [caller.ProfileId] : []);
        var first = CreateCandidateItem(original, displayName: "Candidate1");
        var conflicting = CreateCandidateItem(changed, displayName: difference switch
        {
            "display-suffix" => "Candidate2",
            "display-case" => "candidate1",
            _ => "Candidate1"
        });

        Assert.Single(SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, snapshot, snapshot, [first]));
        foreach (var items in new[] { new[] { first, conflicting, first }, new[] { conflicting, first, conflicting } })
            Assert.Empty(SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(caller, snapshot, snapshot, items));
        if (difference is not "display-suffix" and not "display-case")
            Assert.Empty(SocialRankedDiscoveryService.DiscoverRankedCandidates(caller,
                [(original, 10), (changed, 10), (original, 10)]));
    }

    private static QuestionnaireResponseEntity CreateResponse(Guid profileId, Guid responseId, string option = "3", bool reverse = false)
    {
        var timestamp = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        var definition = QuestionnaireV1Definition.Create();
        var answers = definition.Questions.Select((question, index) => new QuestionnaireAnswerEntity(
            new Guid(index + 1, 0, 0, new byte[8]), responseId, question.Id, option, timestamp));
        return QuestionnaireResponseEntity.Create(responseId, profileId, definition,
            reverse ? answers.Reverse() : answers, false, timestamp);
    }

    [Fact]
    public void IndependentlyConstructedEquivalentEvidence_DeduplicatesWithoutChangingRankingRotationOrPrivacy()
    {
        var caller = CreateProfile();
        var snapshot = CreateSnapshot(caller.ProfileId);
        var candidateId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var interestId = Guid.NewGuid();
        var first = CreateProfile(profileId: candidateId, languages: ["English", "French"],
            goals: ["Friendship", "Dating"], interestIds: [interestId], blockedProfileIds: [blockId],
            answers: [new("q01", "3"), new("q02", "3")], response: CreateResponse(candidateId, responseId),
            partnerPreferences: new(), confirmation: new("r1"), questionnaireVersion: "v1", questionnaireRevision: "r1");
        var second = CreateProfile(profileId: candidateId, languages: ["English", "French"],
            goals: ["Friendship", "Dating"], interestIds: [interestId], blockedProfileIds: [blockId],
            answers: [new("q02", "3"), new("q01", "3")], response: CreateResponse(candidateId, responseId, reverse: true),
            partnerPreferences: new(), confirmation: new("r1"), questionnaireVersion: "v1", questionnaireRevision: "r1");
        Assert.NotSame(first.QuestionnaireResponse, second.QuestionnaireResponse);
        var item1 = CreateCandidateItem(first, displayName: "Candidate1");
        var item2 = CreateCandidateItem(second, displayName: "Candidate1");
        var other = CreateCandidateItem(CreateProfile(), displayName: "Other");
        foreach (var offset in new[] { 0, 1, 4 })
        {
            var expected = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(caller, snapshot, snapshot,
                [item1, other], rotationOffset: offset);
            var actual = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(caller, snapshot, snapshot,
                [item2, other, item1], rotationOffset: offset);
            Assert.Equal(2, actual.Count);
            Assert.Equal(expected.Select(card => card.CandidateProfileId), actual.Select(card => card.CandidateProfileId));
            Assert.All(actual, card => Assert.Equal("{}", JsonSerializer.Serialize(card)));
        }
        Assert.Equal("{}", JsonSerializer.Serialize(item1));
        var bounded = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(caller, snapshot, snapshot,
            [item1, other, CreateCandidateItem(first, displayName: "Contradiction")], maxCandidateBatchSize: 2);
        Assert.Equal(2, bounded.Count);
    }

    [Fact]
    public void DiscoverAuthoritativeRankedCards_ValidDuplicatesWithOtherCandidates_PreservesRankingAndRotation()
    {
        var caller = CreateProfile(languages: new[] { "English", "French" }, goals: new[] { "Friendship" });
        var callerSnap = CreateSnapshot(caller.ProfileId);

        var cand1 = CreateProfile(languages: new[] { "English", "French" }, goals: new[] { "Friendship" });
        var cand2 = CreateProfile(languages: new[] { "English" }, goals: new[] { "Companionship" });

        var item1A = CreateCandidateItem(cand1, displayName: "StrongMatch", distanceKm: 5);
        var item1B = CreateCandidateItem(cand1, displayName: "StrongMatch", distanceKm: 5); // duplicate of cand1
        var item2 = CreateCandidateItem(cand2, displayName: "WeakMatch", distanceKm: 50);

        var result = SocialRankedDiscoveryService.DiscoverAuthoritativeRankedCards(
            caller, callerSnap, callerSnap, new[] { item2, item1A, item1B }, rotationOffset: 0);

        Assert.Equal(2, result.Count);
        Assert.Equal(cand1.ProfileId, result[0].CandidateProfileId);
        Assert.Equal(cand2.ProfileId, result[1].CandidateProfileId);
    }
}
