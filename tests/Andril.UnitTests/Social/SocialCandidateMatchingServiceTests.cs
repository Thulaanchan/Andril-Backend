using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Unit, privacy, reciprocal filtering, snapshot projection, and sequence rotation tests
/// for <see cref="SocialCandidateMatchingService"/> and <see cref="SocialCandidateMatchingInput"/>.
/// </summary>
public sealed class SocialCandidateMatchingServiceTests
{
    private static readonly Guid SyntheticCallerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid CandidateId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CandidateId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CandidateId3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CandidateId4 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const int DefaultCallerAge = 25;
    private const string DefaultCallerGender = "male";

    #region Fixture Helpers

    private static SocialExplorePreferences CreatePreferences(
        int minAge = 20,
        int maxAge = 30,
        int minDistanceKm = 0,
        int maxDistanceKm = 50,
        SocialGenderPreference showMe = SocialGenderPreference.Women,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? connectionGoals = null)
    {
        var rangeFilters = new SocialExploreRangeFilters(minAge, maxAge, minDistanceKm, maxDistanceKm);
        return new SocialExplorePreferences(
            rangeFilters,
            showMe,
            languages ?? new[] { "English", "Tamil" },
            connectionGoals ?? new[] { "Dating", "Friendship" });
    }

    private static SocialCandidateMatchingInput CreateInput(
        Guid? profileId = null,
        string? displayName = null,
        int age = 24,
        string? gender = "female",
        SocialExplorePreferences? preferences = null,
        int? distanceKm = 15)
    {
        return new SocialCandidateMatchingInput(
            profileId ?? CandidateId1,
            displayName ?? "Candidate One",
            age,
            gender,
            preferences ?? CreatePreferences(
                minAge: 20,
                maxAge: 30,
                minDistanceKm: 0,
                maxDistanceKm: 50,
                showMe: SocialGenderPreference.Men,
                languages: new[] { "Tamil", "French" },
                connectionGoals: new[] { "Dating" }),
            distanceKm);
    }

    private static SocialCandidateMatchingInput CreateEligibleInput(
        Guid profileId,
        string displayName = "Eligible Candidate",
        int age = 24,
        int? distanceKm = 15) =>
        CreateInput(
            profileId: profileId,
            displayName: displayName,
            age: age,
            gender: "female",
            preferences: CreatePreferences(
                minAge: 20,
                maxAge: 30,
                minDistanceKm: 0,
                maxDistanceKm: 50,
                showMe: SocialGenderPreference.Men,
                languages: new[] { "Tamil", "French" },
                connectionGoals: new[] { "Dating" }),
            distanceKm: distanceKm);

    private static SocialCandidateMatchingInput CreateExcludedInput(
        Guid profileId,
        string displayName = "Excluded Candidate") =>
        CreateInput(
            profileId: profileId,
            displayName: displayName,
            age: 45, // Outside caller's max age of 30
            gender: "female",
            preferences: CreatePreferences(
                minAge: 20,
                maxAge: 30,
                minDistanceKm: 0,
                maxDistanceKm: 50,
                showMe: SocialGenderPreference.Men),
            distanceKm: 15);

    private static SocialCandidateMatchingInput CreateIndeterminateInput(
        Guid profileId,
        string displayName = "Indeterminate Candidate") =>
        CreateInput(
            profileId: profileId,
            displayName: displayName,
            age: 24,
            gender: "female",
            preferences: CreatePreferences(
                minAge: 20,
                maxAge: 30,
                minDistanceKm: 0,
                maxDistanceKm: 50,
                showMe: SocialGenderPreference.Men),
            distanceKm: null); // Null distance produces Indeterminate

    #endregion

    #region SECTION A — Input Contract and Validation

    [Fact]
    public void BuildSnapshots_NullCallerPreferences_ThrowsArgumentNullException_WithParamName()
    {
        var candidates = new[] { CreateEligibleInput(CandidateId1) };

        var ex = Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateMatchingService.BuildSnapshots(
                DefaultCallerAge,
                DefaultCallerGender,
                null!,
                candidates,
                0));

        Assert.Equal("callerPreferences", ex.ParamName);
    }

    [Fact]
    public void BuildSnapshots_NullCandidates_ThrowsArgumentNullException_WithParamName()
    {
        var callerPrefs = CreatePreferences();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            SocialCandidateMatchingService.BuildSnapshots(
                DefaultCallerAge,
                DefaultCallerGender,
                callerPrefs,
                null!,
                0));

        Assert.Equal("candidates", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void BuildSnapshots_NegativeRotationOffset_ThrowsArgumentOutOfRangeException_WithParamName(int negativeOffset)
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SocialCandidateMatchingService.BuildSnapshots(
                DefaultCallerAge,
                DefaultCallerGender,
                callerPrefs,
                candidates,
                negativeOffset));

        Assert.Equal("rotationOffset", ex.ParamName);
        Assert.Equal(negativeOffset, ex.ActualValue);
    }

    [Fact]
    public void BuildSnapshots_CandidateCollectionWithNullEntry_ThrowsArgumentException_WithParamName()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new SocialCandidateMatchingInput[] { CreateEligibleInput(CandidateId1), null! };

        var ex = Assert.Throws<ArgumentException>(() =>
            SocialCandidateMatchingService.BuildSnapshots(
                DefaultCallerAge,
                DefaultCallerGender,
                callerPrefs,
                candidates,
                0));

        Assert.Equal("candidates", ex.ParamName);
    }

    [Fact]
    public void BuildSnapshots_EmptyCandidateBatch_ReturnsEmptyReadOnlyCollection()
    {
        var callerPrefs = CreatePreferences();
        var emptyList = Array.Empty<SocialCandidateMatchingInput>();

        var result = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            emptyList,
            0);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildSnapshots_ZeroRotationOffset_IsAccepted()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1) };

        var result = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Single(result);
        Assert.Equal(CandidateId1, result[0].CandidateProfileId);
    }

    [Fact]
    public void BuildSnapshots_LargeRotationOffset_IsAcceptedWithoutOverflow()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1), CreateEligibleInput(CandidateId2) };

        var result = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            int.MaxValue);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CandidateInput_EmptyProfileId_ThrowsArgumentException()
    {
        var prefs = CreatePreferences();

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateMatchingInput(Guid.Empty, "Alice", 25, "female", prefs, 10));

        Assert.Equal("candidateProfileId", ex.ParamName);
    }

    [Fact]
    public void CandidateInput_NullDisplayName_ThrowsArgumentNullException()
    {
        var prefs = CreatePreferences();

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SocialCandidateMatchingInput(CandidateId1, null!, 25, "female", prefs, 10));

        Assert.Equal("displayName", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void CandidateInput_WhitespaceDisplayName_ThrowsArgumentException(string invalidName)
    {
        var prefs = CreatePreferences();

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialCandidateMatchingInput(CandidateId1, invalidName, 25, "female", prefs, 10));

        Assert.Equal("displayName", ex.ParamName);
    }

    [Fact]
    public void CandidateInput_NullPreferences_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SocialCandidateMatchingInput(CandidateId1, "Alice", 25, "female", null!, 10));

        Assert.Equal("candidatePreferences", ex.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void CandidateInput_NegativeDistance_ThrowsArgumentOutOfRangeException(int negativeDistance)
    {
        var prefs = CreatePreferences();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SocialCandidateMatchingInput(CandidateId1, "Alice", 25, "female", prefs, negativeDistance));

        Assert.Equal("approximateDistanceKm", ex.ParamName);
        Assert.Equal(negativeDistance, ex.ActualValue);
    }

    [Fact]
    public void CandidateInput_UnderageCandidate_AcceptedAtInputConstruction_ExcludedByPipeline()
    {
        var prefs = CreatePreferences();
        var underageInput = new SocialCandidateMatchingInput(CandidateId1, "Young Alice", 17, "female", prefs, 10);

        Assert.Equal(17, underageInput.CandidateAge);

        var callerPrefs = CreatePreferences();
        var results = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { underageInput },
            0);

        Assert.Empty(results);
    }

    [Fact]
    public void CandidateInput_PropertiesPreservedAndNameTrimmed()
    {
        var prefs = CreatePreferences();
        var input = new SocialCandidateMatchingInput(CandidateId1, "  Bob  ", 26, "male", prefs, 25);

        Assert.Equal(CandidateId1, input.CandidateProfileId);
        Assert.Equal("Bob", input.DisplayName);
        Assert.Equal(26, input.CandidateAge);
        Assert.Equal("male", input.CandidateGender);
        Assert.Same(prefs, input.CandidatePreferences);
        Assert.Equal(25, input.ApproximateDistanceKm);
    }

    #endregion

    #region SECTION B — Reciprocal Eligibility

    [Fact]
    public void BuildSnapshots_EligibleCandidate_ProducesOneSnapshot()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1, "Eligible Candidate") };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Single(snapshots);
        Assert.Equal(CandidateId1, snapshots[0].CandidateProfileId);
        Assert.Equal("Eligible Candidate", snapshots[0].DisplayName);
    }

    [Fact]
    public void BuildSnapshots_ExcludedCandidate_ProducesNoSnapshot()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateExcludedInput(CandidateId1) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_IndeterminateCandidate_ProducesNoSnapshot()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateIndeterminateInput(CandidateId1) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_UnderageCaller_ExcludesAllCandidates()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1), CreateEligibleInput(CandidateId2) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            callerAge: 17, // Caller is underage
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_UnderageCandidate_IsExcluded()
    {
        var callerPrefs = CreatePreferences();
        var candidate = CreateInput(profileId: CandidateId1, age: 17);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CandidateOutsideCallerAgeRange_IsExcluded()
    {
        var callerPrefs = CreatePreferences(minAge: 20, maxAge: 30);
        // Candidate is 35 (outside caller's maxAge 30)
        var candidate = CreateInput(profileId: CandidateId1, age: 35);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CallerOutsideCandidateAgeRange_IsExcluded()
    {
        var callerPrefs = CreatePreferences();
        // Candidate prefers caller between 30 and 40, but caller is 25
        var candidatePrefs = CreatePreferences(minAge: 30, maxAge: 40, showMe: SocialGenderPreference.Men);
        var candidate = CreateInput(profileId: CandidateId1, age: 25, preferences: candidatePrefs);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CandidateFailingCallerGenderPreference_IsExcluded()
    {
        // Caller wants Women, candidate is male
        var callerPrefs = CreatePreferences(showMe: SocialGenderPreference.Women);
        var candidate = CreateInput(profileId: CandidateId1, gender: "male");

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CallerFailingCandidateGenderPreference_IsExcluded()
    {
        // Caller is male, but candidate prefers Women
        var callerPrefs = CreatePreferences(showMe: SocialGenderPreference.Women);
        var candidatePrefs = CreatePreferences(showMe: SocialGenderPreference.Women);
        var candidate = CreateInput(profileId: CandidateId1, gender: "female", preferences: candidatePrefs);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            callerAge: 25,
            callerGender: "male",
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CandidateOutsideCallerDistanceRange_IsExcluded()
    {
        // Caller max distance is 50, candidate is at 80 km
        var callerPrefs = CreatePreferences(minDistanceKm: 0, maxDistanceKm: 50);
        var candidate = CreateInput(profileId: CandidateId1, distanceKm: 80);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_CandidateOutsideReciprocalDistanceRange_IsExcluded()
    {
        var callerPrefs = CreatePreferences(minDistanceKm: 0, maxDistanceKm: 100);
        // Candidate only willing to travel up to 10 km, but distance is 25 km
        var candidatePrefs = CreatePreferences(minDistanceKm: 0, maxDistanceKm: 10, showMe: SocialGenderPreference.Men);
        var candidate = CreateInput(profileId: CandidateId1, distanceKm: 25, preferences: candidatePrefs);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_MissingDistance_ProducesIndeterminate_OmittedFromSnapshots()
    {
        var callerPrefs = CreatePreferences();
        var candidate = CreateInput(profileId: CandidateId1, distanceKm: null);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("prefer_not_to_say")]
    [InlineData(null)]
    public void BuildSnapshots_UnresolvedGender_ProducesIndeterminate_OmittedFromSnapshots(string? unresolvedGender)
    {
        var callerPrefs = CreatePreferences();
        var candidate = CreateInput(profileId: CandidateId1, gender: unresolvedGender);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_ExplicitExclusionOverridesIndeterminateEvidence_RemainsExcluded()
    {
        var callerPrefs = CreatePreferences();
        // Candidate is underage (exclusion) AND distance is null (indeterminate)
        var candidate = CreateInput(profileId: CandidateId1, age: 17, distanceKm: null);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        Assert.Empty(snapshots);
    }

    #endregion

    #region SECTION C — Mixed Batch Processing

    [Fact]
    public void BuildSnapshots_MixedBatch_RetainsOnlyEligibleCandidates()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "Eligible 1"),
            CreateExcludedInput(CandidateId2, "Excluded 1"),
            CreateIndeterminateInput(CandidateId3, "Indeterminate 1"),
            CreateEligibleInput(CandidateId4, "Eligible 2")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(CandidateId1, snapshots[0].CandidateProfileId);
        Assert.Equal("Eligible 1", snapshots[0].DisplayName);
        Assert.Equal(CandidateId4, snapshots[1].CandidateProfileId);
        Assert.Equal("Eligible 2", snapshots[1].DisplayName);
    }

    [Fact]
    public void BuildSnapshots_MixedBatch_ExcludedAndIndeterminateNeverAppear()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateExcludedInput(CandidateId1),
            CreateIndeterminateInput(CandidateId2),
            CreateEligibleInput(CandidateId3)
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Single(snapshots);
        Assert.DoesNotContain(snapshots, c => c.CandidateProfileId == CandidateId1);
        Assert.DoesNotContain(snapshots, c => c.CandidateProfileId == CandidateId2);
        Assert.Contains(snapshots, c => c.CandidateProfileId == CandidateId3);
    }

    [Fact]
    public void BuildSnapshots_PreservesRelativeOrderOfEligibleCandidatesBeforeRotation()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateExcludedInput(CandidateId2),
            CreateEligibleInput(CandidateId3, "Second"),
            CreateIndeterminateInput(CandidateId4)
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(CandidateId1, snapshots[0].CandidateProfileId);
        Assert.Equal(CandidateId3, snapshots[1].CandidateProfileId);
    }

    [Fact]
    public void BuildSnapshots_ExclusionOfIntermediateCandidates_DoesNotAffectOrderOfRetained()
    {
        var callerPrefs = CreatePreferences();
        var candidatesWithExclusion = new[]
        {
            CreateEligibleInput(CandidateId1, "A"),
            CreateExcludedInput(CandidateId2),
            CreateEligibleInput(CandidateId3, "B")
        };

        var candidatesWithoutExclusion = new[]
        {
            CreateEligibleInput(CandidateId1, "A"),
            CreateEligibleInput(CandidateId3, "B")
        };

        var snapshots1 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidatesWithExclusion, 0);
        var snapshots2 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidatesWithoutExclusion, 0);

        Assert.Equal(snapshots1.Select(s => s.CandidateProfileId), snapshots2.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_AllCandidatesExcludedOrIndeterminate_ReturnsEmptyCollection()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateExcludedInput(CandidateId1),
            CreateIndeterminateInput(CandidateId2)
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_DoesNotFabricateOrRegenerateCandidateProfileIds()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1), CreateEligibleInput(CandidateId2) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            candidates,
            0);

        Assert.Equal(CandidateId1, snapshots[0].CandidateProfileId);
        Assert.Equal(CandidateId2, snapshots[1].CandidateProfileId);
    }

    #endregion

    #region SECTION D — Snapshot Projection

    [Fact]
    public void BuildSnapshots_EligibleCandidate_PreservesAllRequiredSnapshotFields()
    {
        var callerPrefs = CreatePreferences(
            languages: new[] { "English", "Tamil", "Hindi" },
            connectionGoals: new[] { "Dating", "Friendship" });

        var candidatePrefs = CreatePreferences(
            showMe: SocialGenderPreference.Men,
            languages: new[] { "Tamil", "Spanish" },
            connectionGoals: new[] { "Dating", "Companionship" });

        var candidate = CreateInput(
            profileId: CandidateId1,
            displayName: "  Jessica  ",
            age: 26,
            gender: "female",
            preferences: candidatePrefs,
            distanceKm: 22);

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(
            DefaultCallerAge,
            DefaultCallerGender,
            callerPrefs,
            new[] { candidate },
            0);

        var card = Assert.Single(snapshots);
        Assert.Equal(CandidateId1, card.CandidateProfileId);
        Assert.Equal("Jessica", card.DisplayName); // Trimmed by snapshot factory
        Assert.Equal(26, card.Age);
        Assert.Equal(22, card.ApproximateDistanceKm);
        Assert.Equal(new[] { "Tamil" }, card.SharedLanguages);
        Assert.Equal(new[] { "Dating" }, card.SharedConnectionGoals);
    }

    [Fact]
    public void BuildSnapshots_SnapshotCard_DoesNotExposeQuestionnaireAnswersOrScores()
    {
        var cardProperties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var forbiddenSubstrings = new[]
        {
            "Questionnaire",
            "Answer",
            "Response",
            "Score",
            "Percentage",
            "Weight",
            "Algorithm",
            "Ranking",
            "Badge"
        };

        foreach (var prop in cardProperties)
        {
            foreach (var forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void BuildSnapshots_SnapshotCard_DoesNotExposeInternalAccountOrExactLocationData()
    {
        var cardProperties = typeof(SocialExploreCandidateCard).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var forbiddenIdentityTerms = new[]
        {
            "AccountId",
            "UserId",
            "Sub",
            "Email",
            "Phone",
            "BirthDate",
            "DateOfBirth",
            "Latitude",
            "Longitude",
            "Gps",
            "Coordinate"
        };

        foreach (var prop in cardProperties)
        {
            foreach (var forbidden in forbiddenIdentityTerms)
            {
                Assert.DoesNotContain(forbidden, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region SECTION E — Rotation and Ordering

    [Fact]
    public void BuildSnapshots_OffsetZero_PreservesOriginalEligibleOrder()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateEligibleInput(CandidateId2, "Second"),
            CreateEligibleInput(CandidateId3, "Third")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);

        Assert.Equal(new[] { CandidateId1, CandidateId2, CandidateId3 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_OffsetOne_RotatesEligibleSequenceOnePosition()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateEligibleInput(CandidateId2, "Second"),
            CreateEligibleInput(CandidateId3, "Third")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 1);

        Assert.Equal(new[] { CandidateId2, CandidateId3, CandidateId1 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_OffsetEqualToCount_ReturnsOriginalOrder()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateEligibleInput(CandidateId2, "Second"),
            CreateEligibleInput(CandidateId3, "Third")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 3);

        Assert.Equal(new[] { CandidateId1, CandidateId2, CandidateId3 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_OffsetLargerThanCount_AppliesModuloCorrectly()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateEligibleInput(CandidateId2, "Second"),
            CreateEligibleInput(CandidateId3, "Third")
        };

        // Offset 4 % 3 = 1 -> [Second, Third, First]
        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 4);

        Assert.Equal(new[] { CandidateId2, CandidateId3, CandidateId1 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_IntMaxValueOffset_RotatesWithoutArithmeticOverflow()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "First"),
            CreateEligibleInput(CandidateId2, "Second"),
            CreateEligibleInput(CandidateId3, "Third")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, int.MaxValue);

        Assert.Equal(3, snapshots.Count);
        // int.MaxValue % 3 = 2147483647 % 3 = 1 -> [Second, Third, First]
        Assert.Equal(new[] { CandidateId2, CandidateId3, CandidateId1 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_RotationOccursStrictlyAfterFiltering()
    {
        // 4 total candidates, 3 eligible (A, B, D) and 1 excluded (C at index 2).
        // If rotation happened before filtering with offset = 3:
        //   Rotate candidates [A, B, C, D] by 3 -> [D, A, B, C]
        //   Then filter -> [D, A, B]
        // If rotation happens after filtering with offset = 3:
        //   Filter eligible -> [A, B, D] (count = 3)
        //   Rotate [A, B, D] by offset 3 (3 % 3 = 0) -> [A, B, D]
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1, "A"),
            CreateEligibleInput(CandidateId2, "B"),
            CreateExcludedInput(CandidateId3, "C"),
            CreateEligibleInput(CandidateId4, "D")
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 3);

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(new[] { CandidateId1, CandidateId2, CandidateId4 }, snapshots.Select(s => s.CandidateProfileId));
    }

    [Fact]
    public void BuildSnapshots_EmptyEligibleSet_CanBeRotatedWithoutError()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateExcludedInput(CandidateId1) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 5);

        Assert.Empty(snapshots);
    }

    [Fact]
    public void BuildSnapshots_RotationDoesNotAddOrDropEligibleCandidates()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1),
            CreateEligibleInput(CandidateId2),
            CreateEligibleInput(CandidateId3)
        };

        var snapshots0 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);
        var snapshots1 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 1);
        var snapshots2 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 2);

        var originalIds = new HashSet<Guid> { CandidateId1, CandidateId2, CandidateId3 };

        Assert.True(originalIds.SetEquals(snapshots0.Select(s => s.CandidateProfileId)));
        Assert.True(originalIds.SetEquals(snapshots1.Select(s => s.CandidateProfileId)));
        Assert.True(originalIds.SetEquals(snapshots2.Select(s => s.CandidateProfileId)));
    }

    #endregion

    #region SECTION F — Immutability and Defensive Results

    [Fact]
    public void BuildSnapshots_ReturnsReadOnlyCollection_AttemptedMutationThrows()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);

        Assert.IsAssignableFrom<IReadOnlyList<SocialExploreCandidateCard>>(snapshots);

        var mutableList = snapshots as IList<SocialExploreCandidateCard>;
        Assert.NotNull(mutableList);

        var dummyCard = snapshots[0];
        Assert.Throws<NotSupportedException>(() => mutableList.Add(dummyCard));
        Assert.Throws<NotSupportedException>(() => mutableList.Remove(dummyCard));
        Assert.Throws<NotSupportedException>(() => mutableList.Clear());
    }

    [Fact]
    public void BuildSnapshots_SourceCandidateCollection_RemainsUnchangedAfterExecution()
    {
        var callerPrefs = CreatePreferences();
        var candidate1 = CreateEligibleInput(CandidateId1);
        var candidate2 = CreateExcludedInput(CandidateId2);

        var sourceList = new List<SocialCandidateMatchingInput> { candidate1, candidate2 };

        SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, sourceList, 1);

        Assert.Equal(2, sourceList.Count);
        Assert.Same(candidate1, sourceList[0]);
        Assert.Same(candidate2, sourceList[1]);
    }

    [Fact]
    public void BuildSnapshots_MutatingOriginalListAfterExecution_DoesNotAffectReturnedSnapshots()
    {
        var callerPrefs = CreatePreferences();
        var sourceList = new List<SocialCandidateMatchingInput> { CreateEligibleInput(CandidateId1) };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, sourceList, 0);

        Assert.Single(snapshots);

        sourceList.Add(CreateEligibleInput(CandidateId2));
        sourceList.Clear();

        Assert.Single(snapshots);
        Assert.Equal(CandidateId1, snapshots[0].CandidateProfileId);
    }

    [Fact]
    public void BuildSnapshots_RepeatedCallsWithIdenticalInputs_ProduceEquivalentDeterministicOutput()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1),
            CreateEligibleInput(CandidateId2)
        };

        var run1 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 1);
        var run2 = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 1);

        Assert.Equal(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i], run2[i]);
        }
    }

    #endregion

    #region SECTION G — Privacy and Security Boundaries

    [Fact]
    public void CandidateInput_AllDataProperties_HaveJsonIgnoreAttribute()
    {
        var properties = typeof(SocialCandidateMatchingInput).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        foreach (var prop in properties)
        {
            var jsonIgnore = prop.GetCustomAttribute<JsonIgnoreAttribute>();
            Assert.True(
                jsonIgnore != null,
                $"Property '{prop.Name}' on '{nameof(SocialCandidateMatchingInput)}' must be decorated with [JsonIgnore].");
        }
    }

    [Fact]
    public void CandidateInput_DefaultSerialization_ContainsNoDemographicOrProfileData()
    {
        var prefs = CreatePreferences();
        var input = new SocialCandidateMatchingInput(CandidateId1, "SecretName", 25, "female", prefs, 10);

        var serialized = JsonSerializer.Serialize(input);

        Assert.Equal("{}", serialized);
        Assert.DoesNotContain("SecretName", serialized);
        Assert.DoesNotContain(CandidateId1.ToString(), serialized);
        Assert.DoesNotContain("female", serialized);
    }

    [Fact]
    public void CandidateInput_ToString_ReturnsSafeConstant()
    {
        var prefs = CreatePreferences();
        var input = new SocialCandidateMatchingInput(CandidateId1, "SecretName", 25, "female", prefs, 10);

        Assert.Equal("SocialCandidateMatchingInput [private]", input.ToString());
    }

    [Fact]
    public void SnapshotCard_DefaultSerialization_ContainsNoDemographicOrProfileData()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1, "SecretCandidate") };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);

        var card = Assert.Single(snapshots);
        var serialized = JsonSerializer.Serialize(card);

        Assert.Equal("{}", serialized);
        Assert.DoesNotContain("SecretCandidate", serialized);
    }

    [Fact]
    public void SnapshotCard_ToString_ReturnsSafeConstant()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[] { CreateEligibleInput(CandidateId1, "SecretCandidate") };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);

        var card = Assert.Single(snapshots);
        Assert.Equal("SocialExploreCandidateCard [private]", card.ToString());
    }

    [Fact]
    public void CandidateInput_DoesNotContainQuestionnaireOrSecurityForbiddenTerms()
    {
        var properties = typeof(SocialCandidateMatchingInput).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var forbiddenTerms = new[]
        {
            "Questionnaire",
            "Answer",
            "Response",
            "Score",
            "Percentage",
            "Weight",
            "Password",
            "Secret",
            "Token",
            "AccountId",
            "Email",
            "Phone",
            "Latitude",
            "Longitude",
            "Gps",
            "BirthDate"
        };

        foreach (var prop in properties)
        {
            foreach (var term in forbiddenTerms)
            {
                Assert.DoesNotContain(term, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CollectionSerialization_WarningCheck_RevealsCardinalityEvenThoughItemsAreEmpty()
    {
        var callerPrefs = CreatePreferences();
        var candidates = new[]
        {
            CreateEligibleInput(CandidateId1),
            CreateEligibleInput(CandidateId2)
        };

        var snapshots = SocialCandidateMatchingService.BuildSnapshots(DefaultCallerAge, DefaultCallerGender, callerPrefs, candidates, 0);

        // Even though each item serializes as "{}", serializing the collection produces "[{},{}]",
        // exposing the exact count of candidates. This confirms why candidate collections must
        // NEVER be published directly to public APIs.
        var serializedCollection = JsonSerializer.Serialize(snapshots);

        Assert.Equal("[{},{}]", serializedCollection);
    }

    [Fact]
    public void CandidateInput_StructuralEquality_VerifiesValueSemantics()
    {
        var prefs = CreatePreferences();
        var inputA1 = new SocialCandidateMatchingInput(CandidateId1, "Alex", 25, "female", prefs, 10);
        var inputA2 = new SocialCandidateMatchingInput(CandidateId1, "Alex", 25, "female", prefs, 10);
        var inputB = new SocialCandidateMatchingInput(CandidateId2, "Alex", 25, "female", prefs, 10);

        Assert.Equal(inputA1, inputA2);
        Assert.True(inputA1 == inputA2);
        Assert.False(inputA1 != inputA2);
        Assert.Equal(inputA1.GetHashCode(), inputA2.GetHashCode());

        Assert.NotEqual(inputA1, inputB);
        Assert.True(inputA1 != inputB);
    }

    #endregion
}
