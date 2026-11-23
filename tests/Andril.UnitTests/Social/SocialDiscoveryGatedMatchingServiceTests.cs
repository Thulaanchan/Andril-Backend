using System;
using System.Collections.Generic;
using System.Linq;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialDiscoveryGatedMatchingServiceTests
{
    private static readonly Guid CallerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CandidateB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SocialDiscoveryCallerEvidence CreateCallerEvidence(
        Guid? callerId = null,
        int age = 25,
        bool isOptedIn = true,
        bool isActive = true,
        bool hasEvidence = true)
    {
        return new SocialDiscoveryCallerEvidence(
            callerId ?? CallerId,
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
            connectionGoals ?? new[] { "Dating" });
    }

    private static SocialGatedCandidateItem CreateCandidateItem(
        Guid? candidateId = null,
        string displayName = "Alex",
        int age = 25,
        string? gender = "female",
        SocialExplorePreferences? preferences = null,
        int? distanceKm = 10,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasEvidence = true,
        Guid? evidenceId = null,
        int? evidenceAge = null)
    {
        var cid = candidateId ?? CandidateA;
        var eid = evidenceId ?? cid;
        var eAge = evidenceAge ?? age;
        var prefs = preferences ?? CreateCompatiblePreferences();

        var evidence = new SocialDiscoveryCandidateEvidence(
            eid,
            eAge,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasEvidence);

        var matchingInput = new SocialCandidateMatchingInput(
            cid,
            displayName,
            age,
            gender,
            prefs,
            distanceKm);

        return new SocialGatedCandidateItem(evidence, matchingInput);
    }

    #region 1. Argument Validation

    [Fact]
    public void BuildGatedSnapshots_NullCallerEvidence_ThrowsArgumentNullException()
    {
        var prefs = CreateCompatiblePreferences();
        var candidates = new List<SocialGatedCandidateItem>();

        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(null!, "male", prefs, candidates));
    }

    [Fact]
    public void BuildGatedSnapshots_NullPreferences_ThrowsArgumentNullException()
    {
        var caller = CreateCallerEvidence();
        var candidates = new List<SocialGatedCandidateItem>();

        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", null!, candidates));
    }

    [Fact]
    public void BuildGatedSnapshots_NullCandidates_ThrowsArgumentNullException()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();

        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, null!));
    }

    [Fact]
    public void BuildGatedSnapshots_NegativeRotationOffset_ThrowsArgumentOutOfRangeException()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidates = new List<SocialGatedCandidateItem>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, candidates, -1));
    }

    [Fact]
    public void BuildGatedSnapshots_CollectionContainsNull_ThrowsArgumentException()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidates = new SocialGatedCandidateItem[] { null! };

        Assert.Throws<ArgumentException>(() =>
            SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, candidates));
    }

    [Fact]
    public void BuildGatedSnapshots_EmptyCollection_ReturnsEmpty()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidates = Array.Empty<SocialGatedCandidateItem>();

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, candidates);

        Assert.Empty(result);
    }

    #endregion

    #region 2. Positive Gated Discovery

    [Fact]
    public void BuildGatedSnapshots_EligibleCallerAndCandidate_ReturnsSnapshotCard()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(candidateId: CandidateA, displayName: "Alex", age: 24);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Single(result);
        var card = result[0];
        Assert.Equal(CandidateA, card.CandidateProfileId);
        Assert.Equal("Alex", card.DisplayName);
        Assert.Equal(24, card.Age);
        Assert.Equal(10, card.ApproximateDistanceKm);
    }

    #endregion

    #region 3. Caller-Level Fail-Closed Gating

    [Fact]
    public void BuildGatedSnapshots_CallerMissingEvidence_ReturnsEmpty()
    {
        var caller = CreateCallerEvidence(hasEvidence: false);
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem();

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerUnderage_ReturnsEmpty()
    {
        var caller = CreateCallerEvidence(age: 17);
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem();

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerNotOptedIn_ReturnsEmpty()
    {
        var caller = CreateCallerEvidence(isOptedIn: false);
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem();

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerNotActive_ReturnsEmpty()
    {
        var caller = CreateCallerEvidence(isActive: false);
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem();

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    #endregion

    #region 4. Stage 1 (Candidate Evidence & Privacy) Gating

    [Fact]
    public void BuildGatedSnapshots_CandidateMissingAuthoritativeEvidence_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(hasEvidence: false);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateUnderage_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 17, evidenceAge: 17);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateNotOptedIn_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isOptedIn: false);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateInactive_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isActive: false);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateHidden_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isDiscoverable: false);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateBlockedByCaller_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isBlockedByCaller: true);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CallerBlockedByCandidate_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_IdentityMismatchBetweenEvidenceAndInput_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(candidateId: CandidateA, evidenceId: Guid.NewGuid());

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_AgeMismatchBetweenEvidenceAndInput_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(age: 25, evidenceAge: 30);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    #endregion

    #region 5. Stage 2 (Hard Preference Filters) Gating

    [Fact]
    public void BuildGatedSnapshots_IncompatibleAgePreference_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        // Caller only wants candidates between 18 and 22
        var prefs = CreateCompatiblePreferences(minAge: 18, maxAge: 22);
        var candidate = CreateCandidateItem(age: 35);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_IncompatibleDistancePreference_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences(maxDistanceKm: 25);
        // Candidate is 50km away
        var candidate = CreateCandidateItem(distanceKm: 50);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_IncompatibleGenderPreference_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        // Caller only wants female candidates
        var prefs = CreateCompatiblePreferences(showMe: SocialGenderPreference.Women);
        // Candidate is male
        var candidate = CreateCandidateItem(gender: "male");

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    #endregion

    #region 6. Mixed Batch & Rotation Ordering

    [Fact]
    public void BuildGatedSnapshots_MixedBatch_ReturnsOnlyEligibleAndRotates()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();

        var eligible1 = CreateCandidateItem(candidateId: CandidateA, displayName: "Alex");
        var blocked = CreateCandidateItem(candidateId: Guid.NewGuid(), isBlockedByCaller: true);
        var eligible2 = CreateCandidateItem(candidateId: CandidateB, displayName: "Jordan");
        var hidden = CreateCandidateItem(candidateId: Guid.NewGuid(), isDiscoverable: false);

        var batch = new[] { eligible1, blocked, eligible2, hidden };

        // Rotation offset 1: [eligible1, eligible2] rotated by 1 -> [eligible2, eligible1]
        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, batch, rotationOffset: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(CandidateB, result[0].CandidateProfileId);
        Assert.Equal(CandidateA, result[1].CandidateProfileId);
    }

    [Fact]
    public void BuildGatedSnapshots_IdentityAndAgeBothMismatched_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var candidate = CreateCandidateItem(
            candidateId: CandidateA,
            age: 24,
            evidenceId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            evidenceAge: 31);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { candidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_CandidateWithSameIdAsCaller_IsOmitted()
    {
        var caller = CreateCallerEvidence(callerId: CallerId);
        var prefs = CreateCompatiblePreferences();
        var selfCandidate = CreateCandidateItem(candidateId: CallerId, evidenceId: CallerId);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { selfCandidate });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_MutuallyBlockedCandidate_IsOmitted()
    {
        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();
        var mutuallyBlocked = CreateCandidateItem(isBlockedByCaller: true, isCallerBlockedByCandidate: true);

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, new[] { mutuallyBlocked });

        Assert.Empty(result);
    }

    [Fact]
    public void BuildGatedSnapshots_ComprehensiveMixedBatch_ExcludesInconsistentBlockedAndIneligibleAndPreservesRotation()
    {
        var candidateC = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var idMismatchId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var ageMismatchId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var missingEvidenceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var blockedByCallerId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var blockedByCandidateId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var hiddenId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var inactiveId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var underageId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var caller = CreateCallerEvidence();
        var prefs = CreateCompatiblePreferences();

        var batch = new[]
        {
            CreateCandidateItem(candidateId: CandidateA, displayName: "Alex"),
            CreateCandidateItem(candidateId: idMismatchId, evidenceId: Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
            CreateCandidateItem(candidateId: ageMismatchId, age: 25, evidenceAge: 29),
            CreateCandidateItem(candidateId: missingEvidenceId, hasEvidence: false),
            CreateCandidateItem(candidateId: blockedByCallerId, isBlockedByCaller: true),
            CreateCandidateItem(candidateId: blockedByCandidateId, isCallerBlockedByCandidate: true),
            CreateCandidateItem(candidateId: hiddenId, isDiscoverable: false),
            CreateCandidateItem(candidateId: inactiveId, isActive: false),
            CreateCandidateItem(candidateId: underageId, age: 17, evidenceAge: 17),
            CreateCandidateItem(candidateId: CandidateB, displayName: "Jordan"),
            CreateCandidateItem(candidateId: candidateC, displayName: "Sam")
        };

        var rotated0 = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, batch, rotationOffset: 0);
        var rotated1 = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, batch, rotationOffset: 1);
        var rotated2 = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(caller, "male", prefs, batch, rotationOffset: 2);

        Assert.Equal(new[] { CandidateA, CandidateB, candidateC }, rotated0.Select(c => c.CandidateProfileId).ToArray());
        Assert.Equal(new[] { CandidateB, candidateC, CandidateA }, rotated1.Select(c => c.CandidateProfileId).ToArray());
        Assert.Equal(new[] { candidateC, CandidateA, CandidateB }, rotated2.Select(c => c.CandidateProfileId).ToArray());
    }

    [Fact]
    public void BuildGatedSnapshots_UnderageCallerWithMultipleEligibleCandidates_ReturnsEmpty()
    {
        var underageCaller = CreateCallerEvidence(age: 17);
        var prefs = CreateCompatiblePreferences();
        var batch = new[]
        {
            CreateCandidateItem(candidateId: CandidateA, displayName: "Alex"),
            CreateCandidateItem(candidateId: CandidateB, displayName: "Jordan")
        };

        var result = SocialDiscoveryGatedMatchingService.BuildGatedSnapshots(underageCaller, "male", prefs, batch, rotationOffset: 1);

        Assert.Empty(result);
    }

    [Fact]
    public void SocialGatedCandidateItem_ToStringAndEquality_SafeguardsPrivacy()
    {
        var item1 = CreateCandidateItem();
        var item2 = CreateCandidateItem();

        Assert.Equal("SocialGatedCandidateItem [private]", item1.ToString());
        Assert.Equal(item1, item2);
        Assert.Equal(item1.GetHashCode(), item2.GetHashCode());
    }

    #endregion
}
