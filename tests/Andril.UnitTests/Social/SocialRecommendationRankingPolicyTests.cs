using System;
using System.Collections.Generic;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

public class SocialRecommendationRankingPolicyTests
{
    private static SocialCompatibilityScoreResult CreateScoredResult(
        double score,
        double availableWeight = 100.0,
        double evidenceCoverage = 100.0,
        double applicableCoverage = 100.0)
    {
        var factors = new Dictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>();
        return SocialCompatibilityScoreResult.Scored(
            score,
            availableWeight,
            evidenceCoverage,
            applicableCoverage,
            factors);
    }

    private static SocialScoringProfileEvidence CreateSimpleEvidence(
        Guid? profileId = null,
        int age = 25,
        string gender = "female",
        string showMe = "Men",
        IEnumerable<Guid>? confirmedSocialInterests = null)
    {
        return new SocialScoringProfileEvidence(
            profileId ?? Guid.NewGuid(),
            age,
            gender,
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(18, 40, 0, 50),
                showMe,
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: confirmedSocialInterests ?? new[] { Guid.Parse("11111111-1111-1111-1111-111111111111") });
    }

    [Theory]
    [InlineData(75.0, 70.0, SocialRecommendationTier.Strong)]
    [InlineData(85.0, 80.0, SocialRecommendationTier.Strong)]
    [InlineData(74.9, 70.0, SocialRecommendationTier.Standard)]
    [InlineData(75.0, 69.9, SocialRecommendationTier.Standard)]
    [InlineData(50.0, 50.0, SocialRecommendationTier.Standard)]
    [InlineData(49.9, 80.0, SocialRecommendationTier.OtherEligible)]
    [InlineData(0.0, 100.0, SocialRecommendationTier.OtherEligible)]
    public void DetermineTier_ScoredResult_ReturnsExpectedTier(
        double score,
        double coverage,
        SocialRecommendationTier expectedTier)
    {
        var result = CreateScoredResult(score, applicableCoverage: coverage);
        var tier = SocialRecommendationRankingPolicy.DetermineTier(result);
        Assert.Equal(expectedTier, tier);
    }

    [Fact]
    public void DetermineTier_PreferenceBasedSkippedQuestionnaire_QualifiesForStrong()
    {
        // When personality is skipped, applicable coverage is (65 / 65) * 100 = 100%.
        // Score >= 75 and applicable coverage >= 70% qualifies for Strong!
        var result = CreateScoredResult(
            score: 78.5,
            availableWeight: 65.0,
            evidenceCoverage: 65.0,
            applicableCoverage: 100.0);

        var tier = SocialRecommendationRankingPolicy.DetermineTier(result);
        Assert.Equal(SocialRecommendationTier.Strong, tier);
    }

    [Fact]
    public void DetermineTier_UnscoredResult_ReturnsUnscoredTier()
    {
        var unscored = SocialCompatibilityScoreResult.Unscored(
            new Dictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>());

        var tier = SocialRecommendationRankingPolicy.DetermineTier(unscored);
        Assert.Equal(SocialRecommendationTier.Unscored, tier);
    }

    [Fact]
    public void DetermineTier_ExcludedOrIneligibleResult_ThrowsArgumentException()
    {
        var excluded = SocialCompatibilityScoreResult.Excluded(
            SocialCandidateExclusionReason.CallerAgePreferenceMismatch);

        Assert.Throws<ArgumentException>(() => SocialRecommendationRankingPolicy.DetermineTier(excluded));
    }

    [Fact]
    public void RankCandidates_SortsByTierThenScoreThenCoverageThenGuid()
    {
        var interestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 30, 0, 50),
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // Strong candidate (shared goals, shared language, shared interest, near distance) -> score ~96, coverage 84.6% -> Strong
        var candStrong = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        // Standard candidate (shared goals, but disjoint language, dist 40km) -> score ~52, coverage 84.6% -> Standard
        var candStandard = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "female",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(18, 40, 0, 50),
                "Men",
                new[] { "French" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "French" }, // Disjoint language -> score 0
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // Other eligible candidate (disjoint language, disjoint goals, dist 48km) -> score < 50 -> OtherEligible
        var candOther = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "female",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(18, 40, 0, 50),
                "Men",
                new[] { "French" },
                new[] { "Friendship" }),
            confirmedLanguages: new[] { "French" },
            confirmedConnectionGoals: new[] { "Friendship" },
            confirmedSocialInterestIds: new[] { Guid.NewGuid() }); // Disjoint interest

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (candOther, 48),
            (candStandard, 40),
            (candStrong, 5)
        };

        var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(SocialRecommendationTier.Strong, ranked[0].Tier);
        Assert.Equal(SocialRecommendationTier.Standard, ranked[1].Tier);
        Assert.Equal(SocialRecommendationTier.OtherEligible, ranked[2].Tier);
    }

    [Fact]
    public void RankCandidates_OmitsExcludedCandidates()
    {
        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 24, 0, 50), // Max age 24
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" });

        var candEligible = CreateSimpleEvidence(age: 22);
        var candUnderage = CreateSimpleEvidence(age: 17); // Underage
        var candAgeMismatch = CreateSimpleEvidence(age: 28); // Excluded by age

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (candEligible, 10),
            (candUnderage, 10),
            (candAgeMismatch, 10)
        };

        var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates);

        Assert.Single(ranked);
        Assert.Equal(candEligible.ProfileId, ranked[0].CandidateProfileId);
    }

    [Fact]
    public void RankCandidates_RotationOffset_RotatesCandidatesDeterministically()
    {
        var caller = CreateSimpleEvidence(gender: "male", showMe: "Women");

        var cand1 = CreateSimpleEvidence();
        var cand2 = CreateSimpleEvidence();
        var cand3 = CreateSimpleEvidence();

        var list = new[]
        {
            (cand1, (int?)10),
            (cand2, (int?)10),
            (cand3, (int?)10)
        };

        var unrotated = SocialRecommendationRankingPolicy.RankCandidates(caller, list, rotationOffset: 0);
        var rotated1 = SocialRecommendationRankingPolicy.RankCandidates(caller, list, rotationOffset: 1);

        Assert.Equal(unrotated.Count, rotated1.Count);
        Assert.Equal(unrotated[1].CandidateProfileId, rotated1[0].CandidateProfileId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(1000000)]
    public void RankCandidates_RotationDoesNotMoveStandardAheadOfStrong(int rotationOffset)
    {
        var interestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 30, 0, 50),
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        var candStrong1 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });
        var candStrong2 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        var candStandard1 = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "female",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(18, 40, 0, 50),
                "Men",
                new[] { "French" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "French" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        var candStandard2 = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "female",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(18, 40, 0, 50),
                "Men",
                new[] { "French" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "French" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (candStandard1, 40),
            (candStrong1, 5),
            (candStandard2, 40),
            (candStrong2, 5)
        };

        var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates, rotationOffset: rotationOffset);

        Assert.Equal(4, ranked.Count);

        // Strong recommendations MUST ALWAYS be at index 0 and 1
        Assert.Equal(SocialRecommendationTier.Strong, ranked[0].Tier);
        Assert.Equal(SocialRecommendationTier.Strong, ranked[1].Tier);

        // Standard recommendations MUST ALWAYS be at index 2 and 3
        Assert.Equal(SocialRecommendationTier.Standard, ranked[2].Tier);
        Assert.Equal(SocialRecommendationTier.Standard, ranked[3].Tier);
    }

    [Fact]
    public void RankCandidates_RotationDoesNotMoveLowerScoresAheadOfHigherScores()
    {
        var interestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 30, 0, 50),
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // Higher score candidate (distance 0 km)
        var candHigherScore = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        // Two equal lower-score candidates (distance 15 km)
        var candLowerScore1 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });
        var candLowerScore2 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (candLowerScore1, 15),
            (candHigherScore, 0),
            (candLowerScore2, 15)
        };

        // Regardless of rotation offset, highest score remains at index 0
        for (int offset = 0; offset <= 5; offset++)
        {
            var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates, rotationOffset: offset);
            Assert.Equal(3, ranked.Count);
            Assert.Equal(candHigherScore.ProfileId, ranked[0].CandidateProfileId);
        }
    }

    [Fact]
    public void RankCandidates_MultipleIndependentTieGroupsRemainCorrectlyOrdered()
    {
        var interestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 30, 0, 50),
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // Strong tie group (2 candidates)
        var strong1 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });
        var strong2 = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        // Standard tie group (2 candidates)
        var standard1 = new SocialScoringProfileEvidence(
            Guid.NewGuid(), 25, "female",
            new SocialExplorePreferences(new SocialExploreRangeFilters(18, 40, 0, 50), "Men", new[] { "French" }, new[] { "Dating" }),
            confirmedLanguages: new[] { "French" }, confirmedConnectionGoals: new[] { "Dating" }, confirmedSocialInterestIds: new[] { interestId });
        var standard2 = new SocialScoringProfileEvidence(
            Guid.NewGuid(), 25, "female",
            new SocialExplorePreferences(new SocialExploreRangeFilters(18, 40, 0, 50), "Men", new[] { "French" }, new[] { "Dating" }),
            confirmedLanguages: new[] { "French" }, confirmedConnectionGoals: new[] { "Dating" }, confirmedSocialInterestIds: new[] { interestId });

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (standard1, 40),
            (strong1, 5),
            (standard2, 40),
            (strong2, 5)
        };

        var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates, rotationOffset: 1);

        Assert.Equal(4, ranked.Count);
        Assert.Equal(SocialRecommendationTier.Strong, ranked[0].Tier);
        Assert.Equal(SocialRecommendationTier.Strong, ranked[1].Tier);
        Assert.Equal(SocialRecommendationTier.Standard, ranked[2].Tier);
        Assert.Equal(SocialRecommendationTier.Standard, ranked[3].Tier);
    }

    [Fact]
    public void DetermineTier_AndTierOrdering_UnscoredRemainsBehindAllScoredTiers()
    {
        var unscored = SocialCompatibilityScoreResult.Unscored(
            new Dictionary<SocialCompatibilityFactor, SocialScoringFactorBreakdown>());

        var tier = SocialRecommendationRankingPolicy.DetermineTier(unscored);
        Assert.Equal(SocialRecommendationTier.Unscored, tier);

        Assert.True((int)tier > (int)SocialRecommendationTier.OtherEligible);
        Assert.True((int)tier > (int)SocialRecommendationTier.Standard);
        Assert.True((int)tier > (int)SocialRecommendationTier.Strong);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(100)]
    public void RankCandidates_LowerTierProfiles_RemainBehindAllHigherTierProfilesUnderRotation(int rotationOffset)
    {
        var interestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var caller = new SocialScoringProfileEvidence(
            Guid.NewGuid(),
            25,
            "male",
            new SocialExplorePreferences(
                new SocialExploreRangeFilters(20, 30, 0, 50),
                "Women",
                new[] { "English" },
                new[] { "Dating" }),
            confirmedLanguages: new[] { "English" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // Strong candidate (score ~96, coverage 84.6% -> Strong)
        var strongCand = CreateSimpleEvidence(confirmedSocialInterests: new[] { interestId });

        // Standard candidate (score ~52, coverage 84.6% -> Standard)
        var standardCand = new SocialScoringProfileEvidence(
            Guid.NewGuid(), 25, "female",
            new SocialExplorePreferences(new SocialExploreRangeFilters(18, 40, 0, 50), "Men", new[] { "French" }, new[] { "Dating" }),
            confirmedLanguages: new[] { "French" },
            confirmedConnectionGoals: new[] { "Dating" },
            confirmedSocialInterestIds: new[] { interestId });

        // OtherEligible candidate (score ~3.6 -> OtherEligible)
        var otherCand = new SocialScoringProfileEvidence(
            Guid.NewGuid(), 25, "female",
            new SocialExplorePreferences(new SocialExploreRangeFilters(18, 40, 0, 50), "Men", new[] { "French" }, new[] { "Friendship" }),
            confirmedLanguages: new[] { "French" },
            confirmedConnectionGoals: new[] { "Friendship" },
            confirmedSocialInterestIds: new[] { Guid.NewGuid() });

        var candidates = new (SocialScoringProfileEvidence Candidate, int? ApproximateDistanceKm)[]
        {
            (otherCand, 48),
            (standardCand, 40),
            (strongCand, 5)
        };

        var ranked = SocialRecommendationRankingPolicy.RankCandidates(caller, candidates, rotationOffset: rotationOffset);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(SocialRecommendationTier.Strong, ranked[0].Tier);
        Assert.Equal(SocialRecommendationTier.Standard, ranked[1].Tier);
        Assert.Equal(SocialRecommendationTier.OtherEligible, ranked[2].Tier);
        Assert.Equal(strongCand.ProfileId, ranked[0].CandidateProfileId);
        Assert.Equal(standardCand.ProfileId, ranked[1].CandidateProfileId);
        Assert.Equal(otherCand.ProfileId, ranked[2].CandidateProfileId);
    }
}
