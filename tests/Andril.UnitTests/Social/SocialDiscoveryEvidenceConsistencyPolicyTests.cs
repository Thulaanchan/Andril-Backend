using System;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialDiscoveryEvidenceConsistencyPolicyTests
{
    private static readonly Guid CandidateId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CandidateId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static SocialExplorePreferences CreatePreferences()
    {
        var rangeFilters = new SocialExploreRangeFilters(18, 35, 0, 50);
        return new SocialExplorePreferences(
            rangeFilters,
            SocialGenderPreference.All,
            new[] { "English" },
            new[] { "Dating" });
    }

    private static SocialDiscoveryCandidateEvidence CreateEvidence(
        Guid? candidateId = null,
        int age = 24,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool isBlockedByCaller = false,
        bool isCallerBlockedByCandidate = false,
        bool hasEvidence = true)
    {
        return new SocialDiscoveryCandidateEvidence(
            candidateId ?? CandidateId1,
            age,
            isOptedIn,
            isActive,
            isDiscoverable,
            isBlockedByCaller,
            isCallerBlockedByCandidate,
            hasEvidence);
    }

    private static SocialCandidateMatchingInput CreateMatchingInput(
        Guid? candidateId = null,
        string displayName = "Alex",
        int age = 24,
        string? gender = "female",
        SocialExplorePreferences? preferences = null,
        int? distanceKm = 10)
    {
        return new SocialCandidateMatchingInput(
            candidateId ?? CandidateId1,
            displayName,
            age,
            gender,
            preferences ?? CreatePreferences(),
            distanceKm);
    }

    #region 1. Argument & Construction Validation

    [Fact]
    public void Validate_NullEvidence_ThrowsArgumentNullException()
    {
        var candidate = CreateMatchingInput();
        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryEvidenceConsistencyPolicy.Validate(null!, candidate));
    }

    [Fact]
    public void Validate_NullCandidate_ThrowsArgumentNullException()
    {
        var evidence = CreateEvidence();
        Assert.Throws<ArgumentNullException>(() =>
            SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, null!));
    }

    #endregion

    #region 2. Positive Consistency

    [Fact]
    public void Validate_MatchingIdentityAndAgeWithEvidence_ReturnsValid()
    {
        var evidence = CreateEvidence(CandidateId1, age: 26);
        var candidate = CreateMatchingInput(CandidateId1, age: 26);

        var result = SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, candidate);

        Assert.True(result.IsValid);
        Assert.Equal(SocialDiscoveryEvidenceConsistencyStatus.Valid, result.Status);
        Assert.Equal(SocialDiscoveryEvidenceInconsistencyReason.None, result.Reasons);
    }

    #endregion

    #region 3. Profile Identity Mismatches

    [Fact]
    public void Validate_DifferentProfileIdentities_ReturnsMismatchWithProfileIdMismatch()
    {
        var evidence = CreateEvidence(CandidateId1);
        var candidate = CreateMatchingInput(CandidateId2);

        var result = SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, candidate);

        Assert.False(result.IsValid);
        Assert.Equal(SocialDiscoveryEvidenceConsistencyStatus.Mismatch, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryEvidenceInconsistencyReason.ProfileIdMismatch));
    }

    #endregion

    #region 4. Demographic Age Mismatches

    [Fact]
    public void Validate_AgeMismatch_ReturnsMismatchWithAgeMismatchReason()
    {
        var evidence = CreateEvidence(CandidateId1, age: 25);
        var candidate = CreateMatchingInput(CandidateId1, age: 30);

        var result = SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, candidate);

        Assert.False(result.IsValid);
        Assert.Equal(SocialDiscoveryEvidenceConsistencyStatus.Mismatch, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryEvidenceInconsistencyReason.AgeMismatch));
    }

    [Fact]
    public void Validate_BothIdentityAndAgeMismatch_ReturnsMismatchWithBothReasons()
    {
        var evidence = CreateEvidence(CandidateId1, age: 25);
        var candidate = CreateMatchingInput(CandidateId2, age: 30);

        var result = SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, candidate);

        Assert.False(result.IsValid);
        Assert.Equal(SocialDiscoveryEvidenceConsistencyStatus.Mismatch, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryEvidenceInconsistencyReason.ProfileIdMismatch));
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryEvidenceInconsistencyReason.AgeMismatch));
    }

    #endregion

    #region 5. Missing Authoritative Evidence (Fail-Closed)

    [Fact]
    public void Validate_MissingAuthoritativeEvidence_ReturnsMissingEvidenceStatus()
    {
        var evidence = CreateEvidence(CandidateId1, hasEvidence: false);
        var candidate = CreateMatchingInput(CandidateId1);

        var result = SocialDiscoveryEvidenceConsistencyPolicy.Validate(evidence, candidate);

        Assert.False(result.IsValid);
        Assert.Equal(SocialDiscoveryEvidenceConsistencyStatus.MissingEvidence, result.Status);
        Assert.True(result.Reasons.HasFlag(SocialDiscoveryEvidenceInconsistencyReason.MissingAuthoritativeEvidence));
    }

    #endregion

    #region 6. Diagnostics and Equality

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveData()
    {
        var result = SocialDiscoveryEvidenceConsistencyResult.Success();
        Assert.Equal("SocialDiscoveryEvidenceConsistencyResult [private]", result.ToString());
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var res1 = SocialDiscoveryEvidenceConsistencyResult.Success();
        var res2 = SocialDiscoveryEvidenceConsistencyResult.Success();

        Assert.Equal(res1, res2);
        Assert.Equal(res1.GetHashCode(), res2.GetHashCode());

        var mis1 = SocialDiscoveryEvidenceConsistencyResult.Mismatch(SocialDiscoveryEvidenceInconsistencyReason.ProfileIdMismatch);
        var mis2 = SocialDiscoveryEvidenceConsistencyResult.Mismatch(SocialDiscoveryEvidenceInconsistencyReason.ProfileIdMismatch);

        Assert.Equal(mis1, mis2);
        Assert.Equal(mis1.GetHashCode(), mis2.GetHashCode());
        Assert.NotEqual(res1, mis1);
    }

    #endregion
}
