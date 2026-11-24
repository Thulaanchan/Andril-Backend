using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

/// <summary>
/// Unit, structural equality, immutability, and privacy regression tests for <see cref="GroupMembership"/>.
/// </summary>
public sealed class GroupMembershipTests
{
    private static readonly Guid SyntheticGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SyntheticMemberProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    #region SECTION A — Construction and Parameter Validation

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Constructor_WithValidParameters_ConstructsSuccessfullyAndPreservesProperties(
        string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);

        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.Equal(SyntheticGroupId, membership.GroupId);
        Assert.Same(scope, membership.Scope);
        Assert.Equal(SyntheticMemberProfileId, membership.MemberProfileId);
    }

    [Fact]
    public void Create_FactoryMethod_CreatesEquivalentInstance()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var fromCtor = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        var fromFactory = GroupMembership.Create(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.Equal(fromCtor, fromFactory);
        Assert.Equal(fromCtor.GroupId, fromFactory.GroupId);
        Assert.Equal(fromCtor.Scope, fromFactory.Scope);
        Assert.Equal(fromCtor.MemberProfileId, fromFactory.MemberProfileId);
    }

    [Fact]
    public void Constructor_WithEmptyGroupId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var ex = Assert.Throws<ArgumentException>(() =>
            new GroupMembership(Guid.Empty, scope, SyntheticMemberProfileId));

        Assert.Equal("groupId", ex.ParamName);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithNullScope_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new GroupMembership(SyntheticGroupId, null!, SyntheticMemberProfileId));

        Assert.Equal("scope", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithEmptyMemberProfileId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);

        var ex = Assert.Throws<ArgumentException>(() =>
            new GroupMembership(SyntheticGroupId, scope, Guid.Empty));

        Assert.Equal("memberProfileId", ex.ParamName);
        Assert.Contains("required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WithEmptyOrNullParameters_ThrowsAppropriateException()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);

        Assert.Throws<ArgumentException>(() => GroupMembership.Create(Guid.Empty, scope, SyntheticMemberProfileId));
        Assert.Throws<ArgumentNullException>(() => GroupMembership.Create(SyntheticGroupId, null!, SyntheticMemberProfileId));
        Assert.Throws<ArgumentException>(() => GroupMembership.Create(SyntheticGroupId, scope, Guid.Empty));
    }

    #endregion

    #region SECTION B — Structural Equality and Operators

    [Fact]
    public void Equals_Typed_SameReference_ReturnsTrue()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.True(membership.Equals(membership));
    }

    [Fact]
    public void Equals_Typed_Null_ReturnsFalse()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.False(membership.Equals(null));
    }

    [Fact]
    public void Equals_Typed_EqualInstances_ReturnsTrue()
    {
        var scope1 = new GroupModuleScope("learning", GroupKind.Classroom);
        var scope2 = new GroupModuleScope("learning", GroupKind.Classroom);

        var membership1 = new GroupMembership(SyntheticGroupId, scope1, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(SyntheticGroupId, scope2, SyntheticMemberProfileId);

        Assert.True(membership1.Equals(membership2));
        Assert.True(membership2.Equals(membership1));
    }

    [Fact]
    public void Equals_Typed_DifferentGroupId_ReturnsFalse()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var differentGroupId = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var membership1 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(differentGroupId, scope, SyntheticMemberProfileId);

        Assert.False(membership1.Equals(membership2));
    }

    [Fact]
    public void Equals_Typed_DifferentScope_ReturnsFalse()
    {
        var scope1 = new GroupModuleScope("learning", GroupKind.Classroom);
        var scope2 = new GroupModuleScope("hobbies", GroupKind.Club);

        var membership1 = new GroupMembership(SyntheticGroupId, scope1, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(SyntheticGroupId, scope2, SyntheticMemberProfileId);

        Assert.False(membership1.Equals(membership2));
    }

    [Fact]
    public void Equals_Typed_DifferentMemberProfileId_ReturnsFalse()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var differentProfileId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        var membership1 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(SyntheticGroupId, scope, differentProfileId);

        Assert.False(membership1.Equals(membership2));
    }

    [Fact]
    public void Equals_Object_NullOrDifferentType_ReturnsFalse()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.False(membership.Equals((object?)null));
        Assert.False(membership.Equals("some string"));
        Assert.False(membership.Equals(SyntheticGroupId));
        Assert.False(membership.Equals(new object()));
    }

    [Fact]
    public void Equals_Object_BoxedEqualInstance_ReturnsTrue()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership1 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        object membership2 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.True(membership1.Equals(membership2));
    }

    [Fact]
    public void GetHashCode_EqualInstances_ReturnIdenticalHashCode()
    {
        var scope1 = new GroupModuleScope("learning", GroupKind.Classroom);
        var scope2 = new GroupModuleScope("learning", GroupKind.Classroom);

        var membership1 = new GroupMembership(SyntheticGroupId, scope1, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(SyntheticGroupId, scope2, SyntheticMemberProfileId);

        Assert.Equal(membership1.GetHashCode(), membership2.GetHashCode());
    }

    [Fact]
    public void GetHashCode_MatchesHashCodeCombineOfProperties()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        var expected = HashCode.Combine(membership.GroupId, membership.Scope, membership.MemberProfileId);

        Assert.Equal(expected, membership.GetHashCode());
    }

    [Fact]
    public void EqualityOperator_BothNull_ReturnsTrue()
    {
        GroupMembership? left = null;
        GroupMembership? right = null;

        Assert.True(left == right);
        Assert.False(left != right);
    }

    [Fact]
    public void EqualityOperator_OneNull_ReturnsFalse()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        GroupMembership? nullMembership = null;

        Assert.False(membership == nullMembership);
        Assert.False(nullMembership == membership);
        Assert.True(membership != nullMembership);
        Assert.True(nullMembership != membership);
    }

    [Fact]
    public void EqualityOperator_EqualInstances_ReturnsTrue()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership1 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        Assert.True(membership1 == membership2);
        Assert.False(membership1 != membership2);
    }

    [Fact]
    public void EqualityOperator_DifferentInstances_ReturnsFalse()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var otherGroupId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var membership1 = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);
        var membership2 = new GroupMembership(otherGroupId, scope, SyntheticMemberProfileId);

        Assert.False(membership1 == membership2);
        Assert.True(membership1 != membership2);
    }

    #endregion

    #region SECTION C — Defensive Immutability and Scope Safety

    [Fact]
    public void Immutability_ClassIsSealed()
    {
        Assert.True(typeof(GroupMembership).IsSealed);
    }

    [Fact]
    public void Immutability_AllPublicPropertiesAreReadOnly()
    {
        var properties = typeof(GroupMembership).GetProperties(BindingFlags.Public | BindingFlags.Instance);

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
        var fields = typeof(GroupMembership).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(fields);
    }

    [Fact]
    public void Class_DoesNotExposeRoleOrPermissionHierarchies()
    {
        var properties = typeof(GroupMembership).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "Role", "Permission", "Rank", "Level", "Admin", "Owner", "Moderator", "Quota", "Status" };
        foreach (var prop in properties)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Class_DoesNotExposeCrossModuleOrAccountIdentities()
    {
        var properties = typeof(GroupMembership).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "AccountId", "UserId", "Email", "Phone", "Tenant", "Password" };
        foreach (var prop in properties)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, prop.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region SECTION D — Privacy-Preserving ToString and JSON Serialization

    [Fact]
    public void ToString_ReturnsSafeDiagnosticSummaryWithScope()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        var result = membership.ToString();

        Assert.Equal("GroupMembership [social:Zone]", result);
    }

    [Fact]
    public void ToString_DoesNotContainRawGroupOrProfileGuids()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        var result = membership.ToString();

        Assert.DoesNotContain(SyntheticGroupId.ToString(), result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticMemberProfileId.ToString(), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonSerialization_DoesNotExposePrivateGroupOrProfileGuids()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership = new GroupMembership(SyntheticGroupId, scope, SyntheticMemberProfileId);

        var json = JsonSerializer.Serialize(membership);

        Assert.DoesNotContain(SyntheticGroupId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticMemberProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivateIdentifierProperties_HaveJsonIgnoreAttributeApplied()
    {
        var groupIdProp = typeof(GroupMembership).GetProperty(nameof(GroupMembership.GroupId));
        var memberIdProp = typeof(GroupMembership).GetProperty(nameof(GroupMembership.MemberProfileId));

        Assert.NotNull(groupIdProp?.GetCustomAttribute<JsonIgnoreAttribute>());
        Assert.NotNull(memberIdProp?.GetCustomAttribute<JsonIgnoreAttribute>());
    }

    #endregion
}
