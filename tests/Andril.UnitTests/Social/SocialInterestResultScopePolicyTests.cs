using System;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialInterestResultScopePolicyTests
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ForeignProfile = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime RecordedTimestamp = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static SocialProfilePairKey CanonicalPair =>
        SocialProfilePairKey.Create(ProfileA, ProfileB);

    private static SocialDirectionalInterest CreateInterest(
        SocialProfilePairKey pairKey,
        Guid actingProfileId,
        bool isPositive = true)
    {
        return new SocialDirectionalInterest(pairKey, actingProfileId, isPositive, RecordedTimestamp);
    }

    #region 1. Argument & Identity Validation

    [Fact]
    public void Validate_NullRequestedPair_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialInterestResultScopePolicy.Validate(null!, ProfileA, null));
    }

    [Fact]
    public void Validate_EmptyActingProfileId_ReturnsInvalidIdentity()
    {
        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, Guid.Empty, null);

        Assert.False(result.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public void Validate_ActingProfileNotMemberOfPair_ReturnsUnrelatedProfile()
    {
        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, ForeignProfile, null);

        Assert.False(result.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.UnrelatedProfile, result.Status);
    }

    #endregion

    #region 2. Valid In-Scope Results

    [Fact]
    public void Validate_NullRecord_ReturnsValidNullResult()
    {
        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, ProfileA, null);

        Assert.True(result.IsValid);
        Assert.False(result.HasDecision);
        Assert.Equal(SocialInterestResultScopeStatus.NullResult, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_MatchingPairAndActingProfile_ReturnsValid(bool isPositive)
    {
        var record = CreateInterest(CanonicalPair, ProfileA, isPositive);

        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, ProfileA, record);

        Assert.True(result.IsValid);
        Assert.True(result.HasDecision);
        Assert.Equal(SocialInterestResultScopeStatus.Valid, result.Status);
    }

    #endregion

    #region 3. Scope Mismatches

    [Fact]
    public void Validate_DifferentPairKey_ReturnsPairMismatch()
    {
        var otherPair = SocialProfilePairKey.Create(ProfileA, ForeignProfile);
        var record = CreateInterest(otherPair, ProfileA);

        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, ProfileA, record);

        Assert.False(result.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.PairMismatch, result.Status);
    }

    [Fact]
    public void Validate_DifferentActingProfileInSamePair_ReturnsActingProfileMismatch()
    {
        // Record was made by ProfileB, but query requested ProfileA
        var record = CreateInterest(CanonicalPair, ProfileB);

        var result = SocialInterestResultScopePolicy.Validate(CanonicalPair, ProfileA, record);

        Assert.False(result.IsValid);
        Assert.Equal(SocialInterestResultScopeStatus.ActingProfileMismatch, result.Status);
    }

    #endregion

    #region 4. Pair Result Validation

    [Fact]
    public void ValidatePairResult_BothNull_ReturnsTrue()
    {
        var isValid = SocialInterestResultScopePolicy.ValidatePairResult(CanonicalPair, null, null);
        Assert.True(isValid);
    }

    [Fact]
    public void ValidatePairResult_BothValid_ReturnsTrue()
    {
        var first = CreateInterest(CanonicalPair, CanonicalPair.FirstProfileId);
        var second = CreateInterest(CanonicalPair, CanonicalPair.SecondProfileId, isPositive: false);

        var isValid = SocialInterestResultScopePolicy.ValidatePairResult(CanonicalPair, first, second);
        Assert.True(isValid);
    }

    [Fact]
    public void ValidatePairResult_MismatchedRecordInPair_ReturnsFalse()
    {
        var otherPair = SocialProfilePairKey.Create(ProfileA, ForeignProfile);
        var foreignRecord = CreateInterest(otherPair, ProfileA);

        var isValid = SocialInterestResultScopePolicy.ValidatePairResult(CanonicalPair, foreignRecord, null);
        Assert.False(isValid);
    }

    #endregion

    #region 5. Privacy, Non-Disclosure, and Equality

    [Fact]
    public void Diagnostic_ToString_SafeguardsPrivacy()
    {
        var validation = SocialInterestResultScopeValidation.Success();
        Assert.Equal("SocialInterestResultScopeValidation [private]", validation.ToString());
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var val1 = SocialInterestResultScopeValidation.Success();
        var val2 = SocialInterestResultScopeValidation.Success();

        Assert.Equal(val1, val2);
        Assert.Equal(val1.GetHashCode(), val2.GetHashCode());

        var none = SocialInterestResultScopeValidation.None();
        Assert.NotEqual(val1, none);
    }

    #endregion
}
