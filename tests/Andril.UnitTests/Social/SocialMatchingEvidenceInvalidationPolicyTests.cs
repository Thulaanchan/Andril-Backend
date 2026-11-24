using System;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public class SocialMatchingEvidenceInvalidationPolicyTests
{
    private static SocialEvidenceRevisionSnapshot CreateBaselineSnapshot(
        Guid? profileId = null,
        string policyVersion = SocialCompatibilityScoringEngine.PolicyVersion,
        long questionnaireRev = 1,
        bool isQuestionnaireWithdrawn = false,
        long archetypeRev = 1,
        long partnerPrefsRev = 1,
        long demoRev = 1,
        long locationRev = 1,
        long langRev = 1,
        long goalsRev = 1,
        long interestsRev = 1,
        long blockRev = 1,
        long unmatchRev = 1,
        bool isActive = true,
        bool isRestricted = false)
    {
        return new SocialEvidenceRevisionSnapshot(
            profileId ?? Guid.NewGuid(),
            policyVersion,
            questionnaireRev,
            isQuestionnaireWithdrawn,
            archetypeRev,
            partnerPrefsRev,
            demoRev,
            locationRev,
            langRev,
            goalsRev,
            interestsRev,
            blockRev,
            unmatchRev,
            isActive,
            isRestricted);
    }

    [Fact]
    public void EvaluateCurrency_IdenticalSnapshots_ReturnsValidCurrent()
    {
        var id = Guid.NewGuid();
        var cached = CreateBaselineSnapshot(id);
        var latest = CreateBaselineSnapshot(id);

        var result = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, latest);

        Assert.True(result.IsCurrent);
        Assert.Equal(SocialEvidenceCurrencyStatus.Current, result.Status);
        Assert.Equal(SocialEvidenceInvalidationReason.None, result.InvalidationReasons);
    }

    [Theory]
    [InlineData("QuestionnaireRevision", 2, SocialEvidenceInvalidationReason.QuestionnaireRevisionChanged)]
    [InlineData("ArchetypeConfirmationRevision", 2, SocialEvidenceInvalidationReason.ArchetypeConfirmationChanged)]
    [InlineData("PartnerPreferencesRevision", 2, SocialEvidenceInvalidationReason.PartnerPreferencesChanged)]
    [InlineData("DemographicsRevision", 2, SocialEvidenceInvalidationReason.DemographicsChanged)]
    [InlineData("LocationDistanceRevision", 2, SocialEvidenceInvalidationReason.LocationDistanceChanged)]
    [InlineData("LanguagesRevision", 2, SocialEvidenceInvalidationReason.LanguagesChanged)]
    [InlineData("ConnectionGoalsRevision", 2, SocialEvidenceInvalidationReason.ConnectionGoalsChanged)]
    [InlineData("SocialInterestsRevision", 2, SocialEvidenceInvalidationReason.SocialInterestsChanged)]
    [InlineData("BlockingRevision", 2, SocialEvidenceInvalidationReason.BlockingChanged)]
    [InlineData("UnmatchingRevision", 2, SocialEvidenceInvalidationReason.UnmatchingChanged)]
    public void EvaluateCurrency_SingleRevisionBump_ReturnsStaleWithSpecificReason(
        string propertyName,
        long newRevision,
        SocialEvidenceInvalidationReason expectedReason)
    {
        var id = Guid.NewGuid();
        var cached = CreateBaselineSnapshot(id);

        var latest = propertyName switch
        {
            "QuestionnaireRevision" => CreateBaselineSnapshot(id, questionnaireRev: newRevision),
            "ArchetypeConfirmationRevision" => CreateBaselineSnapshot(id, archetypeRev: newRevision),
            "PartnerPreferencesRevision" => CreateBaselineSnapshot(id, partnerPrefsRev: newRevision),
            "DemographicsRevision" => CreateBaselineSnapshot(id, demoRev: newRevision),
            "LocationDistanceRevision" => CreateBaselineSnapshot(id, locationRev: newRevision),
            "LanguagesRevision" => CreateBaselineSnapshot(id, langRev: newRevision),
            "ConnectionGoalsRevision" => CreateBaselineSnapshot(id, goalsRev: newRevision),
            "SocialInterestsRevision" => CreateBaselineSnapshot(id, interestsRev: newRevision),
            "BlockingRevision" => CreateBaselineSnapshot(id, blockRev: newRevision),
            "UnmatchingRevision" => CreateBaselineSnapshot(id, unmatchRev: newRevision),
            _ => throw new ArgumentException(propertyName)
        };

        var result = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, latest);

        Assert.False(result.IsCurrent);
        Assert.Equal(SocialEvidenceCurrencyStatus.StaleRequiresRefresh, result.Status);
        Assert.True(result.InvalidationReasons.HasFlag(expectedReason));
    }

    [Fact]
    public void EvaluateCurrency_QuestionnaireWithdrawn_ReturnsStaleWithWithdrawnReason()
    {
        var id = Guid.NewGuid();
        var cached = CreateBaselineSnapshot(id, isQuestionnaireWithdrawn: false);
        var latest = CreateBaselineSnapshot(id, isQuestionnaireWithdrawn: true);

        var result = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, latest);

        Assert.Equal(SocialEvidenceCurrencyStatus.StaleRequiresRefresh, result.Status);
        Assert.True(result.InvalidationReasons.HasFlag(SocialEvidenceInvalidationReason.QuestionnaireWithdrawn));
    }

    [Fact]
    public void EvaluateCurrency_ProfileDeactivatedOrAccountRestricted_ReturnsRevoked()
    {
        var id = Guid.NewGuid();
        var cached = CreateBaselineSnapshot(id);

        // Deactivated
        var deactivated = CreateBaselineSnapshot(id, isActive: false);
        var resultDeactivated = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, deactivated);
        Assert.Equal(SocialEvidenceCurrencyStatus.RevokedOrDeactivated, resultDeactivated.Status);
        Assert.Equal(SocialEvidenceInvalidationReason.ProfileDeactivated, resultDeactivated.InvalidationReasons);

        // Restricted
        var restricted = CreateBaselineSnapshot(id, isRestricted: true);
        var resultRestricted = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, restricted);
        Assert.Equal(SocialEvidenceCurrencyStatus.RevokedOrDeactivated, resultRestricted.Status);
        Assert.Equal(SocialEvidenceInvalidationReason.AccountRestricted, resultRestricted.InvalidationReasons);
    }

    [Fact]
    public void EvaluateCurrency_PolicyVersionUpdate_ReturnsPolicyChanged()
    {
        var id = Guid.NewGuid();
        var cached = CreateBaselineSnapshot(id, policyVersion: "P-04-1.0");
        var latest = CreateBaselineSnapshot(id, policyVersion: "P-04-2.0");

        var result = SocialMatchingEvidenceInvalidationPolicy.EvaluateCurrency(cached, latest);

        Assert.Equal(SocialEvidenceCurrencyStatus.IncompatiblePolicyVersion, result.Status);
        Assert.Equal(SocialEvidenceInvalidationReason.PolicyVersionChanged, result.InvalidationReasons);
    }

    [Fact]
    public void ShouldInvalidateOnTransactionOutcome_CommittedOnly()
    {
        // Only committed transactions invalidate evidence
        Assert.True(SocialMatchingEvidenceInvalidationPolicy.ShouldInvalidateOnTransactionOutcome(isTransactionCommitted: true));
        Assert.False(SocialMatchingEvidenceInvalidationPolicy.ShouldInvalidateOnTransactionOutcome(isTransactionCommitted: false));
    }

    [Theory]
    [InlineData(SocialScoreChangeProhibitedEffect.CreateLike)]
    [InlineData(SocialScoreChangeProhibitedEffect.RemoveLike)]
    [InlineData(SocialScoreChangeProhibitedEffect.CreateMatch)]
    [InlineData(SocialScoreChangeProhibitedEffect.RemoveMatch)]
    [InlineData(SocialScoreChangeProhibitedEffect.AuthorizeCommunication)]
    public void AssertNoSideEffect_ThrowsInvalidOperationException(SocialScoreChangeProhibitedEffect effect)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SocialMatchingEvidenceInvalidationPolicy.AssertNoSideEffect(effect));

        Assert.Contains(effect.ToString(), ex.Message);
    }
}
