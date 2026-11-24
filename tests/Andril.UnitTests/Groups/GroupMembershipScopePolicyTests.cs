using System;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupMembershipScopePolicyTests
{
    private static readonly Guid ValidGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherGroupId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ValidProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void IsCompatible_MatchingGroupAndMembership_ReturnsTrue(string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var group = new GroupEntry(ValidGroupId, scope, "Test Group");
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        var result = GroupMembershipScopePolicy.IsCompatible(group, membership);

        Assert.True(result);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Validate_MatchingGroupAndMembership_SucceedsWithoutException(string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var group = new GroupEntry(ValidGroupId, scope, "Test Group");
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        GroupMembershipScopePolicy.Validate(group, membership);
    }

    [Fact]
    public void IsCompatible_DifferentGroupId_ReturnsFalse()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var group = new GroupEntry(ValidGroupId, scope, "Class A");
        var membership = new GroupMembership(OtherGroupId, scope, ValidProfileId);

        var result = GroupMembershipScopePolicy.IsCompatible(group, membership);

        Assert.False(result);
    }

    [Fact]
    public void Validate_DifferentGroupId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var group = new GroupEntry(ValidGroupId, scope, "Class A");
        var membership = new GroupMembership(OtherGroupId, scope, ValidProfileId);

        var ex = Assert.Throws<ArgumentException>(() => GroupMembershipScopePolicy.Validate(group, membership));
        Assert.Equal("membership", ex.ParamName);
        Assert.Contains("does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsCompatible_CrossModuleScopes_ReturnsFalse()
    {
        var learningScope = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbiesScope = new GroupModuleScope("hobbies", GroupKind.Club);

        var group = new GroupEntry(ValidGroupId, learningScope, "Learning Classroom");
        var membership = new GroupMembership(ValidGroupId, hobbiesScope, ValidProfileId);

        var result = GroupMembershipScopePolicy.IsCompatible(group, membership);

        Assert.False(result);
    }

    [Fact]
    public void Validate_CrossModuleScopes_ThrowsArgumentException()
    {
        var learningScope = new GroupModuleScope("learning", GroupKind.Classroom);
        var socialScope = new GroupModuleScope("social", GroupKind.Zone);

        var group = new GroupEntry(ValidGroupId, learningScope, "Learning Classroom");
        var membership = new GroupMembership(ValidGroupId, socialScope, ValidProfileId);

        var ex = Assert.Throws<ArgumentException>(() => GroupMembershipScopePolicy.Validate(group, membership));
        Assert.Equal("membership", ex.ParamName);
        Assert.Contains("incompatible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void IsCompatible_NullReferences_FailsClosedReturningFalse(bool hasGroup, bool hasMembership)
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var group = hasGroup ? new GroupEntry(ValidGroupId, scope, "Class") : null;
        var membership = hasMembership ? new GroupMembership(ValidGroupId, scope, ValidProfileId) : null;

        Assert.False(GroupMembershipScopePolicy.IsCompatible(group, membership));
    }

    [Fact]
    public void Validate_NullGroup_ThrowsArgumentNullException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        var ex = Assert.Throws<ArgumentNullException>(() => GroupMembershipScopePolicy.Validate(null!, membership));
        Assert.Equal("group", ex.ParamName);
    }

    [Fact]
    public void Validate_NullMembership_ThrowsArgumentNullException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var group = new GroupEntry(ValidGroupId, scope, "Class");

        var ex = Assert.Throws<ArgumentNullException>(() => GroupMembershipScopePolicy.Validate(group, null!));
        Assert.Equal("membership", ex.ParamName);
    }

    [Fact]
    public void IsCompatible_WithExplicitGroupIdAndScope_ReturnsExpectedResults()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        Assert.True(GroupMembershipScopePolicy.IsCompatible(ValidGroupId, scope, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(OtherGroupId, scope, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(Guid.Empty, scope, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(ValidGroupId, null, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(ValidGroupId, scope, null));
    }

    [Fact]
    public void IsCompatible_ScopesOnly_EvaluatesStrictly()
    {
        var learning = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbies = new GroupModuleScope("hobbies", GroupKind.Club);
        var social = new GroupModuleScope("social", GroupKind.Zone);

        Assert.True(GroupMembershipScopePolicy.IsCompatible(learning, learning));
        Assert.True(GroupMembershipScopePolicy.IsCompatible(hobbies, hobbies));
        Assert.True(GroupMembershipScopePolicy.IsCompatible(social, social));

        Assert.False(GroupMembershipScopePolicy.IsCompatible(learning, hobbies));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(learning, social));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(hobbies, social));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(null, learning));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(learning, null));
    }

    [Fact]
    public void IsCompatible_Deterministic_AcrossRepeatedInvocations()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var group = new GroupEntry(ValidGroupId, scope, "Class");
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(GroupMembershipScopePolicy.IsCompatible(group, membership));
            Assert.True(GroupMembershipScopePolicy.IsCompatible(ValidGroupId, scope, membership));
            Assert.True(GroupMembershipScopePolicy.IsCompatible(scope, scope));
        }
    }
}
