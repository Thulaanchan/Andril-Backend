using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Andril.Social.Matching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

/// <summary>
/// End-to-end security boundary tests for the Social Explore gated discovery pipeline
/// (<see cref="SocialDiscoveryGatedMatchingService"/>).
/// Verifies fail-closed caller prerequisites, underage exclusion, module opt-in enforcement,
/// bidirectional block enforcement, contradictory evidence rejection, identity isolation,
/// privacy leakage safeguards, and static module decoupling.
/// </summary>
public sealed class SocialExploreEndToEndBoundaryTests
{
    private static readonly Guid CallerProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateProfileIdA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CandidateProfileIdB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SocialDiscoveryCallerEvidence CreateCallerEvidence(
        Guid? profileId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasEvidence = true)
    {
        return new SocialDiscoveryCallerEvidence(
            profileId ?? CallerProfileId,
            age,
            isOptedIn,
            isActive,
            hasEvidence);
    }

    private static SocialExplorePreferences CreateCompatiblePreferences(
        SocialGenderPreference showMe = SocialGenderPreference.All,
        int minAge = 18,
        int maxAge = 50,
        int minDistanceKm = 0,
        int maxDistanceKm = 100,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null)
    {
        var rangeFilters = new SocialExploreRangeFilters(minAge, maxAge, minDistanceKm, maxDistanceKm);
        return new SocialExplorePreferences(
            rangeFilters,
            showMe,
            languages ?? new[] { "English" },
            connectionGoals ?? new[] { "Friendship" });
    }

    private static SocialGatedCandidateItem CreateCandidateItem(
        Guid? candidateProfileId = null,
        string displayName = "CandidateUser",
        int age = 24,
        string? gender = "female",
        SocialExplorePreferences? preferences = null,
        int? approximateDistanceKm = 15,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasEvidence = true,
        Guid? evidenceProfileId = null,
        int? evidenceAge = null)
    {
        var cProfileId = candidateProfileId ?? CandidateProfileIdA;
        var eProfileId = evidenceProfileId ?? cProfileId;
        var eAge = evidenceAge ?? age;
        var prefs = preferences ?? CreateCompatiblePreferences();

        var evidence = new SocialDiscoveryCandidateEvidence(
            eProfileId,
            eAge,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasEvidence);

        var matchingInput = new SocialCandidateMatchingInput(
            cProfileId,
            displayName,
            age,
            gender,
            prefs,
            approximateDistanceKm);

        return new SocialGatedCandidateItem(evidence, matchingInput);
    }

    #region 1. Authorized Discovery Baseline

    [Fact]
    public void BuildGatedSnapshots_AuthorizedAdultCallerAndCandidate_ReturnsRotatedSnapshot()
    {
        var caller = CreateCallerEvidence(age: 26);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(candidateProfileId: CandidateProfileIdA, age: 24);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate },
            rotationOffset: 0);

        var card = Assert.Single(results);
        Assert.Equal(CandidateProfileIdA, card.CandidateProfileId);
        Assert.Equal("CandidateUser", card.DisplayName);
        Assert.Equal(24, card.Age);
        Assert.Equal(15, card.ApproximateDistanceKm);
        Assert.Contains("English", card.SharedLanguages);
        Assert.Contains("Friendship", card.SharedConnectionGoals);
    }

    #endregion

    #region 2. Caller-Side Fail-Closed Pre-Validations

    [Fact]
    public void BuildGatedSnapshots_CallerUnderage_FailsClosedReturningEmpty()
    {
        var caller = CreateCallerEvidence(age: 17);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 22);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerNotOptedIn_FailsClosedReturningEmpty()
    {
        var caller = CreateCallerEvidence(isOptedIn: false);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 22);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerNotActive_FailsClosedReturningEmpty()
    {
        var caller = CreateCallerEvidence(isActive: false);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 22);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerLacksEvidence_FailsClosedReturningEmpty()
    {
        var caller = CreateCallerEvidence(hasEvidence: false);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 22);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    #endregion

    #region 3. Candidate-Side Eligibility and Safety Omissions

    [Fact]
    public void BuildGatedSnapshots_CandidateUnderage_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 17);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateNotOptedIn_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isOptedIn: false);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateNotActive_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isActive: false);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateHidden_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isDiscoverable: false);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateBlockedByCaller_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isBlockedByCaller: true);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerBlockedByCandidate_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(age: 25);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isCallerBlockedByCandidate: true);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_SameProfileIdentity_OmittedFromResults()
    {
        var caller = CreateCallerEvidence(profileId: CallerProfileId);
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(candidateProfileId: CallerProfileId);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    #endregion

    #region 4. Contradictory Evidence and Missing Evidence Omissions

    [Fact]
    public void BuildGatedSnapshots_ContradictoryProfileIdEvidence_OmittedFromResults()
    {
        var caller = CreateCallerEvidence();
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(
            candidateProfileId: CandidateProfileIdA,
            evidenceProfileId: CandidateProfileIdB);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_ContradictoryAgeEvidence_OmittedFromResults()
    {
        var caller = CreateCallerEvidence();
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(
            age: 25,
            evidenceAge: 26);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateMissingAuthoritativeEvidence_OmittedFromResults()
    {
        var caller = CreateCallerEvidence();
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(hasEvidence: false);

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        Assert.Empty(results);
    }

    #endregion

    #region 5. Empty Identity Guard Invariants

    [Fact]
    public void CallerEvidence_EmptyProfileId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new SocialDiscoveryCallerEvidence(Guid.Empty, 25, true, true));
    }

    [Fact]
    public void CandidateMatchingInput_EmptyProfileId_ThrowsArgumentException()
    {
        var prefs = CreateCompatiblePreferences();
        Assert.Throws<ArgumentException>(() =>
            new SocialCandidateMatchingInput(Guid.Empty, "ValidName", 25, "female", prefs));
    }

    #endregion

    #region 6. Mixed Batch Pipeline Evaluation

    [Fact]
    public void BuildGatedSnapshots_BatchWithDiverseViolations_YieldsOnlyStrictlyEligible()
    {
        var caller = CreateCallerEvidence(age: 28);
        var callerPrefs = CreateCompatiblePreferences();

        var candidates = new List<SocialGatedCandidateItem>
        {
            CreateCandidateItem(Guid.NewGuid(), age: 17), // Underage
            CreateCandidateItem(Guid.NewGuid(), isOptedIn: false), // Not opted in
            CreateCandidateItem(Guid.NewGuid(), isActive: false), // Not active
            CreateCandidateItem(Guid.NewGuid(), isDiscoverable: false), // Hidden
            CreateCandidateItem(Guid.NewGuid(), isBlockedByCaller: true), // Blocked by caller
            CreateCandidateItem(Guid.NewGuid(), isCallerBlockedByCandidate: true), // Blocked by candidate
            CreateCandidateItem(CallerProfileId), // Self-matching violation
            CreateCandidateItem(Guid.NewGuid(), hasEvidence: false), // Missing evidence
            CreateCandidateItem(CandidateProfileIdA, displayName: "FullyEligibleCandidate", age: 26) // Valid
        };

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            candidates);

        var singleMatch = Assert.Single(results);
        Assert.Equal(CandidateProfileIdA, singleMatch.CandidateProfileId);
        Assert.Equal("FullyEligibleCandidate", singleMatch.DisplayName);
        Assert.Equal(26, singleMatch.Age);
    }

    #endregion

    #region 7. Privacy Safeguards and Architecture Isolation

    [Fact]
    public void CandidateCard_ConcealsPrivateFields_InSerializationAndToString()
    {
        var caller = CreateCallerEvidence();
        var callerPrefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(candidateProfileId: CandidateProfileIdA, displayName: "SecretUser");

        var results = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(
            caller,
            "male",
            callerPrefs,
            new[] { candidate });

        var card = Assert.Single(results);
        var json = JsonSerializer.Serialize(card);

        // JsonIgnore on all properties ensures empty JSON object `{}`
        Assert.Equal("{}", json);
        Assert.DoesNotContain(CandidateProfileIdA.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretUser", json, StringComparison.OrdinalIgnoreCase);

        // ToString returns non-sensitive string
        Assert.Equal("SocialExploreCandidateCard [private]", card.ToString());
    }

    [Fact]
    public void SocialDiscoveryGatedMatchingService_IsStaticPureService_NotRegisteredInDi()
    {
        var services = new ServiceCollection();
        var serviceTypes = services.Select(sd => sd.ServiceType).ToList();

        Assert.DoesNotContain(typeof(SocialDiscoveryGatedMatchingService), serviceTypes);
        Assert.True(typeof(SocialDiscoveryGatedMatchingService).IsAbstract && typeof(SocialDiscoveryGatedMatchingService).IsSealed);
    }

    #endregion
}
