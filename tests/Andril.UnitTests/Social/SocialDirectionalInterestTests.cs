using System;
using System.Text.Json;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialDirectionalInterestTests
{
    private static readonly Guid ProfileA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProfileB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileC = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime ValidUtcTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_WithValidParameters_InitializesCorrectly(bool isPositive)
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, isPositive, ValidUtcTime);

        Assert.Equal(pairKey, interest.PairKey);
        Assert.Equal(ProfileA, interest.ActingProfileId);
        Assert.Equal(ProfileB, interest.TargetProfileId);
        Assert.Equal(isPositive, interest.IsPositive);
        Assert.Equal(ValidUtcTime, interest.RecordedAtUtc);
    }

    [Fact]
    public void TargetProfileId_WhenActingIsSecondProfile_ResolvesFirstProfile()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileB, true, ValidUtcTime);

        Assert.Equal(ProfileB, interest.ActingProfileId);
        Assert.Equal(ProfileA, interest.TargetProfileId);
    }

    [Fact]
    public void Create_WithExplicitTimestamp_CreatesExpectedInstance()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = SocialDirectionalInterest.Create(pairKey, ProfileA, true, ValidUtcTime);

        Assert.Equal(pairKey, interest.PairKey);
        Assert.Equal(ProfileA, interest.ActingProfileId);
        Assert.True(interest.IsPositive);
        Assert.Equal(ValidUtcTime, interest.RecordedAtUtc);
    }

    [Fact]
    public void Create_WithCurrentTimestamp_SetsUtcKind()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var before = DateTime.UtcNow;
        var interest = SocialDirectionalInterest.Create(pairKey, ProfileA, false);
        var after = DateTime.UtcNow;

        Assert.Equal(DateTimeKind.Utc, interest.RecordedAtUtc.Kind);
        Assert.InRange(interest.RecordedAtUtc, before, after);
    }

    [Fact]
    public void Constructor_NullPairKey_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new SocialDirectionalInterest(null!, ProfileA, true, ValidUtcTime));
        Assert.Equal("pairKey", ex.ParamName);
    }

    [Fact]
    public void Constructor_EmptyActingProfileId_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var ex = Assert.Throws<ArgumentException>(
            () => new SocialDirectionalInterest(pairKey, Guid.Empty, true, ValidUtcTime));
        Assert.Equal("actingProfileId", ex.ParamName);
    }

    [Fact]
    public void Constructor_ActingProfileNotInPairKey_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var ex = Assert.Throws<ArgumentException>(
            () => new SocialDirectionalInterest(pairKey, ProfileC, true, ValidUtcTime));
        Assert.Equal("actingProfileId", ex.ParamName);
        Assert.Contains("must belong to the specified profile pair key", ex.Message);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Constructor_NonUtcTimestamp_ThrowsArgumentException(DateTimeKind kind)
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var nonUtc = new DateTime(2026, 11, 23, 12, 0, 0, kind);
        var ex = Assert.Throws<ArgumentException>(
            () => new SocialDirectionalInterest(pairKey, ProfileA, true, nonUtc));
        Assert.Equal("recordedAtUtc", ex.ParamName);
        Assert.Contains("UTC DateTimeKind", ex.Message);
    }

    [Fact]
    public void Constructor_DefaultTimestamp_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var ex = Assert.Throws<ArgumentException>(
            () => new SocialDirectionalInterest(pairKey, ProfileA, true, default));
        Assert.Equal("recordedAtUtc", ex.ParamName);
    }

    [Fact]
    public void Equals_StructuralEquality_VerifiesCorrectly()
    {
        var pair1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pair2 = new SocialProfilePairKey(ProfileB, ProfileA); // Equal to pair1 due to canonical sorting
        var otherPair = new SocialProfilePairKey(ProfileA, ProfileC);

        var a = new SocialDirectionalInterest(pair1, ProfileA, true, ValidUtcTime);
        var b = new SocialDirectionalInterest(pair2, ProfileA, true, ValidUtcTime);
        var cDifferentDecision = new SocialDirectionalInterest(pair1, ProfileA, false, ValidUtcTime);
        var cDifferentActor = new SocialDirectionalInterest(pair1, ProfileB, true, ValidUtcTime);
        var cDifferentTime = new SocialDirectionalInterest(pair1, ProfileA, true, ValidUtcTime.AddSeconds(1));
        var cDifferentPair = new SocialDirectionalInterest(otherPair, ProfileA, true, ValidUtcTime);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.False(a.Equals(cDifferentDecision));
        Assert.False(a.Equals(cDifferentActor));
        Assert.False(a.Equals(cDifferentTime));
        Assert.False(a.Equals(cDifferentPair));
        Assert.False(a.Equals(null));
        Assert.False(a.Equals(new object()));
    }

    [Fact]
    public void ToString_ConcealsConfidentialIdentifiers()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtcTime);

        var diagnostic = interest.ToString();

        Assert.Equal("SocialDirectionalInterest [private]", diagnostic);
        Assert.DoesNotContain(ProfileA.ToString(), diagnostic);
        Assert.DoesNotContain(ProfileB.ToString(), diagnostic);
        Assert.DoesNotContain(ValidUtcTime.ToString(), diagnostic);
    }

    [Fact]
    public void Serialization_SystemTextJson_ConcealsPrivateIdentifiers()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var interest = new SocialDirectionalInterest(pairKey, ProfileA, true, ValidUtcTime);

        var json = JsonSerializer.Serialize(interest);

        Assert.DoesNotContain(ProfileA.ToString(), json);
        Assert.DoesNotContain(ProfileB.ToString(), json);
        Assert.DoesNotContain(ValidUtcTime.ToString("o"), json);
    }
}
