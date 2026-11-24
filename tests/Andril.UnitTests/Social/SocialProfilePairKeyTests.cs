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
/// Unit, structural equality, canonicalization, immutability, and privacy tests for <see cref="SocialProfilePairKey"/>.
/// </summary>
public sealed class SocialProfilePairKeyTests
{
    private static readonly Guid AlphaGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BetaGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GammaGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DeltaGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");

    #region SECTION A — Construction and Parameter Validation

    [Fact]
    public void Constructor_ValidDistinctGuids_InitializesSuccessfully()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.NotNull(key);
        Assert.Equal(AlphaGuid, key.FirstProfileId);
        Assert.Equal(BetaGuid, key.SecondProfileId);
    }

    [Fact]
    public void Create_FactoryMethod_CreatesEquivalentInstance()
    {
        var keyFromCtor = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var keyFromFactory = SocialProfilePairKey.Create(AlphaGuid, BetaGuid);

        Assert.Equal(keyFromCtor, keyFromFactory);
        Assert.Equal(keyFromCtor.FirstProfileId, keyFromFactory.FirstProfileId);
        Assert.Equal(keyFromCtor.SecondProfileId, keyFromFactory.SecondProfileId);
    }

    [Fact]
    public void Constructor_FirstProfileIdEmpty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialProfilePairKey(Guid.Empty, BetaGuid));

        Assert.Equal("profileIdA", ex.ParamName);
        Assert.Contains("cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_SecondProfileIdEmpty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialProfilePairKey(AlphaGuid, Guid.Empty));

        Assert.Equal("profileIdB", ex.ParamName);
        Assert.Contains("cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_BothProfileIdsEmpty_ThrowsArgumentExceptionForFirstParameter()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialProfilePairKey(Guid.Empty, Guid.Empty));

        Assert.Equal("profileIdA", ex.ParamName);
        Assert.Contains("cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_IdenticalGuids_ThrowsArgumentException()
    {
        var duplicateGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var ex = Assert.Throws<ArgumentException>(() =>
            new SocialProfilePairKey(duplicateGuid, duplicateGuid));

        Assert.Equal("profileIdB", ex.ParamName);
        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_EmptyOrIdenticalGuids_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SocialProfilePairKey.Create(Guid.Empty, BetaGuid));
        Assert.Throws<ArgumentException>(() => SocialProfilePairKey.Create(AlphaGuid, Guid.Empty));
        Assert.Throws<ArgumentException>(() => SocialProfilePairKey.Create(AlphaGuid, AlphaGuid));
    }

    #endregion

    #region SECTION B — Canonical Ordering and Symmetry

    [Fact]
    public void CanonicalOrdering_WhenFirstGuidIsLesser_PreservesOrder()
    {
        // AlphaGuid (1111...) < BetaGuid (2222...)
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.Equal(AlphaGuid, key.FirstProfileId);
        Assert.Equal(BetaGuid, key.SecondProfileId);
    }

    [Fact]
    public void CanonicalOrdering_WhenFirstGuidIsGreater_SwapsToLesserFirst()
    {
        // BetaGuid (2222...) > AlphaGuid (1111...)
        var key = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.Equal(AlphaGuid, key.FirstProfileId);
        Assert.Equal(BetaGuid, key.SecondProfileId);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", "ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("99999999-9999-9999-9999-999999999999", "11111111-1111-1111-1111-111111111111")]
    [InlineData("77777777-aaaa-bbbb-cccc-dddddddddddd", "77777777-aaaa-bbbb-cccc-eeeeeeeeeeee")]
    public void CanonicalOrdering_FirstProfileIdIsAlwaysLessThanSecondProfileId(string rawA, string rawB)
    {
        var guidA = Guid.Parse(rawA);
        var guidB = Guid.Parse(rawB);

        var keyForward = new SocialProfilePairKey(guidA, guidB);
        var keyReverse = new SocialProfilePairKey(guidB, guidA);

        Assert.True(keyForward.FirstProfileId.CompareTo(keyForward.SecondProfileId) < 0);
        Assert.True(keyReverse.FirstProfileId.CompareTo(keyReverse.SecondProfileId) < 0);

        Assert.Equal(keyForward.FirstProfileId, keyReverse.FirstProfileId);
        Assert.Equal(keyForward.SecondProfileId, keyReverse.SecondProfileId);
    }

    [Fact]
    public void Symmetry_ForwardAndReverseConstruction_ProduceIdenticalState()
    {
        var forward = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var reverse = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.Equal(forward.FirstProfileId, reverse.FirstProfileId);
        Assert.Equal(forward.SecondProfileId, reverse.SecondProfileId);
        Assert.Equal(forward, reverse);
    }

    #endregion

    #region SECTION C — Contains and GetOtherProfileId Membership Operations

    [Fact]
    public void Contains_MemberGuids_ReturnsTrue()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.True(key.Contains(AlphaGuid));
        Assert.True(key.Contains(BetaGuid));
    }

    [Fact]
    public void Contains_NonMemberGuids_ReturnsFalse()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.False(key.Contains(GammaGuid));
        Assert.False(key.Contains(DeltaGuid));
        Assert.False(key.Contains(Guid.Empty));
    }

    [Fact]
    public void GetOtherProfileId_WhenGivenFirstProfileId_ReturnsSecondProfileId()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var other = key.GetOtherProfileId(AlphaGuid);

        Assert.Equal(BetaGuid, other);
    }

    [Fact]
    public void GetOtherProfileId_WhenGivenSecondProfileId_ReturnsFirstProfileId()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var other = key.GetOtherProfileId(BetaGuid);

        Assert.Equal(AlphaGuid, other);
    }

    [Fact]
    public void GetOtherProfileId_WhenGivenNonMemberGuid_ThrowsArgumentException()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var ex = Assert.Throws<ArgumentException>(() => key.GetOtherProfileId(GammaGuid));

        Assert.Equal("profileId", ex.ParamName);
        Assert.Contains("not a member of this pair key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetOtherProfileId_WhenGivenEmptyGuid_ThrowsArgumentException()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var ex = Assert.Throws<ArgumentException>(() => key.GetOtherProfileId(Guid.Empty));

        Assert.Equal("profileId", ex.ParamName);
        Assert.Contains("not a member of this pair key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SECTION D — Structural Equality, HashCode and Operators

    [Fact]
    public void Equals_Typed_SameReference_ReturnsTrue()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.True(key.Equals(key));
    }

    [Fact]
    public void Equals_Typed_Null_ReturnsFalse()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.False(key.Equals(null));
    }

    [Fact]
    public void Equals_Typed_EqualPairConstructedInSameOrder_ReturnsTrue()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.True(key1.Equals(key2));
        Assert.True(key2.Equals(key1));
    }

    [Fact]
    public void Equals_Typed_EqualPairConstructedInReversedOrder_ReturnsTrue()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.True(key1.Equals(key2));
        Assert.True(key2.Equals(key1));
    }

    [Fact]
    public void Equals_Typed_DifferentPairs_ReturnsFalse()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, GammaGuid);
        var key3 = new SocialProfilePairKey(GammaGuid, DeltaGuid);

        Assert.False(key1.Equals(key2));
        Assert.False(key1.Equals(key3));
        Assert.False(key2.Equals(key3));
    }

    [Fact]
    public void Equals_Object_NullOrDifferentType_ReturnsFalse()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.False(key.Equals((object?)null));
        Assert.False(key.Equals("some string"));
        Assert.False(key.Equals(AlphaGuid));
        Assert.False(key.Equals(new object()));
    }

    [Fact]
    public void Equals_Object_BoxedEqualPair_ReturnsTrue()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        object key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.True(key1.Equals(key2));
    }

    [Fact]
    public void GetHashCode_IdenticalPairs_ReturnSameHashCode()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_ReversedPairs_ReturnSameHashCode()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_MatchesHashCodeCombineOfFirstAndSecond()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var expected = HashCode.Combine(key.FirstProfileId, key.SecondProfileId);

        Assert.Equal(expected, key.GetHashCode());
    }

    [Fact]
    public void GetHashCode_DifferentPairs_ProduceDistinctHashCodes()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, GammaGuid);
        var key3 = new SocialProfilePairKey(BetaGuid, GammaGuid);

        // Distinct pairs must be structurally unequal
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key2, key3);

        // Equal and reversed pairs must produce identical hash codes (supported contract)
        var key1Reversed = new SocialProfilePairKey(BetaGuid, AlphaGuid);
        Assert.Equal(key1.GetHashCode(), key1Reversed.GetHashCode());

        var key2Reversed = new SocialProfilePairKey(GammaGuid, AlphaGuid);
        Assert.Equal(key2.GetHashCode(), key2Reversed.GetHashCode());

        var key3Reversed = new SocialProfilePairKey(GammaGuid, BetaGuid);
        Assert.Equal(key3.GetHashCode(), key3Reversed.GetHashCode());
    }

    [Fact]
    public void EqualityOperator_BothNull_ReturnsTrue()
    {
        SocialProfilePairKey? left = null;
        SocialProfilePairKey? right = null;

        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact]
    public void EqualityOperator_OneNull_ReturnsFalse()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        SocialProfilePairKey? nullKey = null;

        Assert.False(key == nullKey);
        Assert.False(nullKey == key);
        Assert.True(key != nullKey);
        Assert.True(nullKey != key);
    }

    [Fact]
    public void EqualityOperator_EqualInstances_ReturnsTrue()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.True(key1 == key2);
        Assert.False(key1 != key2);
    }

    [Fact]
    public void EqualityOperator_DifferentInstances_ReturnsFalse()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, GammaGuid);

        Assert.False(key1 == key2);
        Assert.True(key1 != key2);
    }

    #endregion

    #region SECTION E — Canonical Comparison and Sorting (IComparable)

    [Fact]
    public void CompareTo_WithNull_ReturnsOne()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.Equal(1, key.CompareTo(null));
    }

    [Fact]
    public void CompareTo_SameReference_ReturnsZero()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        Assert.Equal(0, key.CompareTo(key));
    }

    [Fact]
    public void CompareTo_EqualKeysConstructedInReverse_ReturnsZero()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        Assert.Equal(0, key1.CompareTo(key2));
        Assert.Equal(0, key2.CompareTo(key1));
    }

    [Fact]
    public void CompareTo_DifferentFirstProfileId_OrdersByFirstProfileId()
    {
        // key1: (Alpha, Gamma), key2: (Beta, Gamma)
        // Since Alpha < Beta, key1 < key2
        var key1 = new SocialProfilePairKey(AlphaGuid, GammaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, GammaGuid);

        Assert.True(key1.CompareTo(key2) < 0);
        Assert.True(key2.CompareTo(key1) > 0);
    }

    [Fact]
    public void CompareTo_IdenticalFirstProfileId_OrdersBySecondProfileId()
    {
        // key1: (Alpha, Beta), key2: (Alpha, Gamma)
        // Since Beta < Gamma, key1 < key2
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, GammaGuid);

        Assert.True(key1.CompareTo(key2) < 0);
        Assert.True(key2.CompareTo(key1) > 0);
    }

    [Fact]
    public void CompareTo_SortsCollectionInPredictableOrder()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, DeltaGuid);
        var key2 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key3 = new SocialProfilePairKey(BetaGuid, GammaGuid);
        var key4 = new SocialProfilePairKey(AlphaGuid, GammaGuid);

        var list = new List<SocialProfilePairKey> { key1, key2, key3, key4 };
        list.Sort();

        // Expected order:
        // (Alpha, Beta) < (Alpha, Gamma) < (Alpha, Delta) < (Beta, Gamma)
        Assert.Equal(new SocialProfilePairKey(AlphaGuid, BetaGuid), list[0]);
        Assert.Equal(new SocialProfilePairKey(AlphaGuid, GammaGuid), list[1]);
        Assert.Equal(new SocialProfilePairKey(AlphaGuid, DeltaGuid), list[2]);
        Assert.Equal(new SocialProfilePairKey(BetaGuid, GammaGuid), list[3]);
    }

    #endregion

    #region SECTION F — Defensive Immutability and Reflection Checks

    [Fact]
    public void Immutability_ClassIsSealed()
    {
        Assert.True(typeof(SocialProfilePairKey).IsSealed);
    }

    [Fact]
    public void Immutability_AllPublicPropertiesAreReadOnly()
    {
        var properties = typeof(SocialProfilePairKey).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            Assert.True(property.CanRead);
            Assert.False(property.CanWrite, $"Property '{property.Name}' should not have a public setter.");
            Assert.Null(property.SetMethod);
        }
    }

    [Fact]
    public void Immutability_NoPublicFieldsExposed()
    {
        var fields = typeof(SocialProfilePairKey).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(fields);
    }

    [Fact]
    public void Class_ExposesNoDirectionalInterestOrMutualMatchFlags()
    {
        var properties = typeof(SocialProfilePairKey).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "Directional", "Like", "Match", "Interest", "Timestamp", "Created", "Mutual", "Score", "Rank" };
        foreach (var prop in properties)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Class_ExposesNoCrossModuleOrInternalAccountIdentifiers()
    {
        var properties = typeof(SocialProfilePairKey).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedIdentifiers = new[] { "AccountId", "UserId", "Email", "Phone", "Tenant", "Module" };
        foreach (var prop in properties)
        {
            foreach (var prohibited in prohibitedIdentifiers)
            {
                Assert.DoesNotContain(prohibited, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region SECTION G — Privacy-Preserving ToString and JSON Serialization

    [Fact]
    public void ToString_ReturnsExactSafeConstantString()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var actual = key.ToString();

        Assert.Equal("SocialProfilePairKey [private]", actual);
    }

    [Fact]
    public void ToString_DoesNotContainEitherProfileGuid()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var result = key.ToString();

        Assert.DoesNotContain(AlphaGuid.ToString(), result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BetaGuid.ToString(), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSerialization_ProducesEmptyJsonObjectDueToJsonIgnoreAttributes()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var json = JsonSerializer.Serialize(key);

        Assert.Equal("{}", json);
    }

    [Fact]
    public void JsonSerialization_DoesNotContainEitherProfileGuid()
    {
        var key = new SocialProfilePairKey(AlphaGuid, BetaGuid);

        var json = JsonSerializer.Serialize(key);

        Assert.DoesNotContain(AlphaGuid.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BetaGuid.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllPublicProperties_HaveJsonIgnoreAttributeApplied()
    {
        var properties = typeof(SocialProfilePairKey).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttribute<JsonIgnoreAttribute>();
            Assert.NotNull(attribute);
        }
    }

    #endregion

    #region SECTION H — Deterministic Collection and Dictionary Operations

    [Fact]
    public void HashSet_DeduplicatesPairsConstructedWithReversedArguments()
    {
        var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var key2 = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        var set = new HashSet<SocialProfilePairKey> { key1, key2 };

        Assert.Single(set);
        Assert.Contains(key1, set);
        Assert.Contains(key2, set);
    }

    [Fact]
    public void Dictionary_LookupWithReversedKey_FindsExistingValue()
    {
        var keyForward = new SocialProfilePairKey(AlphaGuid, BetaGuid);
        var keyReverse = new SocialProfilePairKey(BetaGuid, AlphaGuid);

        var dict = new Dictionary<SocialProfilePairKey, string>
        {
            [keyForward] = "MutualChannel123"
        };

        Assert.True(dict.TryGetValue(keyReverse, out var value));
        Assert.Equal("MutualChannel123", value);
    }

    [Fact]
    public void Determinism_RepeatedCreation_ProducesExactSamePropertiesAndHashCodes()
    {
        for (var i = 0; i < 50; i++)
        {
            var key1 = new SocialProfilePairKey(AlphaGuid, BetaGuid);
            var key2 = SocialProfilePairKey.Create(BetaGuid, AlphaGuid);

            Assert.Equal(AlphaGuid, key1.FirstProfileId);
            Assert.Equal(BetaGuid, key1.SecondProfileId);
            Assert.Equal(AlphaGuid, key2.FirstProfileId);
            Assert.Equal(BetaGuid, key2.SecondProfileId);
            Assert.Equal(key1.GetHashCode(), key2.GetHashCode());
            Assert.Equal(key1, key2);
        }
    }

    #endregion
}
