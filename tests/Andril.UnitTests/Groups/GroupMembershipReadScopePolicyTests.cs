using System;
using Andril.Groups.Entities;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupMembershipReadScopePolicyTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MemberProfileId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MemberProfileId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private static GroupEntry CreateGroup(Guid id, GroupModuleScope scope, string name = "Test Group") =>
        new(id, scope, name);

    private static GroupMembership CreateMembership(Guid groupId, GroupModuleScope scope, Guid memberProfileId) =>
        new(groupId, scope, memberProfileId);

    #region 1. Null and Identity Input Validation

    [Fact]
    public void Validate_EmptyRequestedGroupId_ReturnsInvalidIdentity()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(Guid.Empty, LearningScope, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public void Validate_EmptyRequestedMemberProfileId_ReturnsInvalidIdentity()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, Guid.Empty, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public void Validate_NullRequestedScope_ReturnsNullInput()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, null, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullInput, result.Status);
    }

    [Fact]
    public void Validate_NullRequestedGroupEntry_ReturnsNullInput()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(null, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullInput, result.Status);
    }

    #endregion

    #region 2. Valid Absent Membership (Null Result)

    [Fact]
    public void Validate_NullMembership_ReturnsNullResultScopeValid()
    {
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, MemberProfileId1, null);

        Assert.True(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, result.Status);
        Assert.True(GroupMembershipReadScopePolicy.IsCompatible(GroupId1, LearningScope, MemberProfileId1, null));
    }

    [Fact]
    public void Validate_NullMembershipWithGroupEntry_ReturnsNullResultScopeValid()
    {
        var group = CreateGroup(GroupId1, LearningScope);
        var result = GroupMembershipReadScopePolicy.Validate(group, MemberProfileId1, null);

        Assert.True(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, result.Status);
        Assert.True(GroupMembershipReadScopePolicy.IsCompatible(group, MemberProfileId1, null));
    }

    #endregion

    #region 3. Valid Matching Memberships

    [Fact]
    public void Validate_MatchingLearningMembership_ReturnsValid()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, MemberProfileId1, membership);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);
        Assert.True(GroupMembershipReadScopePolicy.IsCompatible(GroupId1, LearningScope, MemberProfileId1, membership));
    }

    [Fact]
    public void Validate_MatchingHobbiesMembership_ReturnsValid()
    {
        var membership = CreateMembership(GroupId1, HobbiesScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, HobbiesScope, MemberProfileId1, membership);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);
    }

    [Fact]
    public void Validate_MatchingSocialMembership_ReturnsValid()
    {
        var membership = CreateMembership(GroupId1, SocialScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, SocialScope, MemberProfileId1, membership);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);
    }

    [Fact]
    public void Validate_MatchingGroupEntryOverload_ReturnsValid()
    {
        var group = CreateGroup(GroupId1, LearningScope);
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId1);

        var result = GroupMembershipReadScopePolicy.Validate(group, MemberProfileId1, membership);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);
        Assert.True(GroupMembershipReadScopePolicy.IsCompatible(group, MemberProfileId1, membership));
    }

    #endregion

    #region 4. Scope and Boundary Mismatches

    [Fact]
    public void Validate_DifferentGroupId_ReturnsGroupIdMismatch()
    {
        var membership = CreateMembership(GroupId2, LearningScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.GroupIdMismatch, result.Status);
        Assert.False(GroupMembershipReadScopePolicy.IsCompatible(GroupId1, LearningScope, MemberProfileId1, membership));
    }

    [Fact]
    public void Validate_DifferentMemberProfileId_ReturnsMemberProfileIdMismatch()
    {
        var membership = CreateMembership(GroupId1, LearningScope, MemberProfileId2);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.MemberProfileIdMismatch, result.Status);
        Assert.False(GroupMembershipReadScopePolicy.IsCompatible(GroupId1, LearningScope, MemberProfileId1, membership));
    }

    [Fact]
    public void Validate_CrossModuleScopeMismatch_ReturnsScopeMismatch()
    {
        var membership = CreateMembership(GroupId1, HobbiesScope, MemberProfileId1);
        var result = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, MemberProfileId1, membership);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.ScopeMismatch, result.Status);
        Assert.False(GroupMembershipReadScopePolicy.IsCompatible(GroupId1, LearningScope, MemberProfileId1, membership));
    }

    #endregion

    #region 5. Privacy, Diagnostics, and Equality

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveData()
    {
        var validation = GroupMembershipReadScopeValidation.Success();
        Assert.Equal("GroupMembershipReadScopeValidation [private]", validation.ToString());
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var val1 = GroupMembershipReadScopeValidation.Success();
        var val2 = GroupMembershipReadScopeValidation.Success();
        var none = GroupMembershipReadScopeValidation.None();
        var fail = GroupMembershipReadScopeValidation.Fail(GroupMembershipReadScopeStatus.GroupIdMismatch);

        Assert.Equal(val1, val2);
        Assert.Equal(val1.GetHashCode(), val2.GetHashCode());
        Assert.NotEqual(val1, none);
        Assert.NotEqual(val1, fail);
    }

    #endregion
}
