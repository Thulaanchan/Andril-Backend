using System;
using System.Text.Json;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Xunit;

namespace Andril.UnitTests.Social;

public sealed class SocialMatchEntityTests
{
    private static readonly Guid MatchId1 = Guid.Parse("018d9f4e-1111-7000-8000-000000000001");
    private static readonly Guid MatchId2 = Guid.Parse("018d9f4e-2222-7000-8000-000000000002");
    private static readonly Guid ProfileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ProfileC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTime ValidUtcTime = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);

        Assert.Equal(MatchId1, match.MatchId);
        Assert.Equal(pairKey, match.PairKey);
        Assert.Equal(ValidUtcTime, match.CreatedAtUtc);
    }

    [Fact]
    public void Constructor_WithEmptyMatchId_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialMatchEntity(Guid.Empty, pairKey, ValidUtcTime));

        Assert.Equal("matchId", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNullPairKey_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SocialMatchEntity(MatchId1, null!, ValidUtcTime));

        Assert.Equal("pairKey", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNonUtcTimestamp_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var localTime = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialMatchEntity(MatchId1, pairKey, localTime));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Theory]
    [InlineData(0)] // Default / MinValue
    public void Constructor_WithDefaultTimestamp_ThrowsArgumentException(long ticks)
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var invalidTime = new DateTime(ticks, DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialMatchEntity(MatchId1, pairKey, invalidTime));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithMaxValueTimestamp_ThrowsArgumentException()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var maxTime = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialMatchEntity(MatchId1, pairKey, maxTime));

        Assert.Equal("createdAtUtc", ex.ParamName);
    }

    [Fact]
    public void PairKey_PreservesCanonicalOrdering_RegardlessOfInputOrder()
    {
        var pairKeyDirect = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKeyReversed = new SocialProfilePairKey(ProfileB, ProfileA);

        var matchDirect = new SocialMatchEntity(MatchId1, pairKeyDirect, ValidUtcTime);
        var matchReversed = new SocialMatchEntity(MatchId1, pairKeyReversed, ValidUtcTime);

        Assert.Equal(matchDirect.PairKey, matchReversed.PairKey);
        Assert.Equal(matchDirect, matchReversed);
    }

    [Fact]
    public void Create_WithExplicitTimestamp_CreatesExpectedInstance()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = SocialMatchEntity.Create(MatchId1, pairKey, ValidUtcTime);

        Assert.Equal(MatchId1, match.MatchId);
        Assert.Equal(pairKey, match.PairKey);
        Assert.Equal(ValidUtcTime, match.CreatedAtUtc);
    }

    [Fact]
    public void Create_WithCurrentTimestamp_SetsUtcKind()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var before = DateTime.UtcNow;
        var match = SocialMatchEntity.Create(MatchId1, pairKey);
        var after = DateTime.UtcNow;

        Assert.Equal(DateTimeKind.Utc, match.CreatedAtUtc.Kind);
        Assert.InRange(match.CreatedAtUtc, before, after);
    }

    [Fact]
    public void Equals_WithSameValues_ReturnsTrue()
    {
        var pairKey1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKey2 = new SocialProfilePairKey(ProfileA, ProfileB);

        var match1 = new SocialMatchEntity(MatchId1, pairKey1, ValidUtcTime);
        var match2 = new SocialMatchEntity(MatchId1, pairKey2, ValidUtcTime);

        Assert.True(match1.Equals(match2));
        Assert.True(match1.Equals((object)match2));
        Assert.True(match1 == match2);
        Assert.False(match1 != match2);
        Assert.Equal(match1.GetHashCode(), match2.GetHashCode());
    }

    [Fact]
    public void Equals_WithDifferentMatchId_ReturnsFalse()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match1 = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);
        var match2 = new SocialMatchEntity(MatchId2, pairKey, ValidUtcTime);

        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
        Assert.True(match1 != match2);
    }

    [Fact]
    public void Equals_WithDifferentPairKey_ReturnsFalse()
    {
        var pairKey1 = new SocialProfilePairKey(ProfileA, ProfileB);
        var pairKey2 = new SocialProfilePairKey(ProfileA, ProfileC);

        var match1 = new SocialMatchEntity(MatchId1, pairKey1, ValidUtcTime);
        var match2 = new SocialMatchEntity(MatchId1, pairKey2, ValidUtcTime);

        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
    }

    [Fact]
    public void Equals_WithDifferentTimestamp_ReturnsFalse()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match1 = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);
        var match2 = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime.AddMinutes(1));

        Assert.False(match1.Equals(match2));
        Assert.False(match1 == match2);
    }

    [Fact]
    public void Equals_WithNullOrOtherType_ReturnsFalse()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);

        Assert.False(match.Equals(null));
        Assert.False(match.Equals("some_string"));
        Assert.False(match == null);
        Assert.False(null == match);
        Assert.True(match != null);
    }

    [Fact]
    public void ToString_ConcealsConfidentialIdentities()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);

        var representation = match.ToString();

        Assert.Equal("SocialMatchEntity [private]", representation);
        Assert.DoesNotContain(MatchId1.ToString(), representation);
        Assert.DoesNotContain(ProfileA.ToString(), representation);
        Assert.DoesNotContain(ProfileB.ToString(), representation);
    }

    [Fact]
    public void Serialization_ConcealsConfidentialProperties()
    {
        var pairKey = new SocialProfilePairKey(ProfileA, ProfileB);
        var match = new SocialMatchEntity(MatchId1, pairKey, ValidUtcTime);

        var json = JsonSerializer.Serialize(match);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(MatchId1.ToString(), json);
        Assert.DoesNotContain(ProfileA.ToString(), json);
        Assert.DoesNotContain(ProfileB.ToString(), json);
    }

    [Fact]
    public void StructuralBoundary_DoesNotExposeLifecycleOrCommunicationPermissions()
    {
        var type = typeof(SocialMatchEntity);

        // Verify no lifecycle status property exists
        Assert.Null(type.GetProperty("Status"));
        Assert.Null(type.GetProperty("IsActive"));
        Assert.Null(type.GetProperty("IsUnmatched"));

        // Verify no communication authorization method exists
        Assert.Null(type.GetMethod("AuthorizeChat"));
        Assert.Null(type.GetMethod("IssueToken"));
        Assert.Null(type.GetMethod("GrantCommunicationEvidence"));
    }
}
