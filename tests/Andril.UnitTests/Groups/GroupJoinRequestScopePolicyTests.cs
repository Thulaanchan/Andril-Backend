using System;
using Andril.Groups.Entities;
using Andril.Groups.JoinRequests;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupJoinRequestScopePolicyTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MemberProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime RequestTimestamp = new(2026, 11, 23, 14, 0, 0, DateTimeKind.Utc);

    private static GroupModuleScope LearningClassroomScope =>
        new("learning", GroupKind.Classroom);

    private static GroupModuleScope HobbiesClubScope =>
        new("hobbies", GroupKind.Club);

    private static GroupModuleScope SocialZoneScope =>
        new("social", GroupKind.Zone);

    private static GroupEntry CreateGroup(Guid id, GroupModuleScope scope, string name = "Target Group") =>
        new(id, scope, name);

    private static GroupJoinRequest CreateRequest(Guid groupId, GroupModuleScope scope, Guid? profileId = null) =>
        new(groupId, scope, profileId ?? MemberProfileId, RequestTimestamp);

    #region 1. Null and Identity Validation

    [Fact]
    public void Validate_NullGroup_ReturnsNullInput()
    {
        var request = CreateRequest(GroupId1, LearningClassroomScope);
        var result = GroupJoinRequestScopePolicy.Validate(null, request);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, result.Status);
    }

    [Fact]
    public void Validate_NullRequest_ReturnsNullInput()
    {
        var group = CreateGroup(GroupId1, LearningClassroomScope);
        var result = GroupJoinRequestScopePolicy.Validate(group, null);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, result.Status);
    }

    [Fact]
    public void Validate_NullTargetScope_ReturnsNullInput()
    {
        var request = CreateRequest(GroupId1, LearningClassroomScope);
        var result = GroupJoinRequestScopePolicy.Validate(GroupId1, null, request);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, result.Status);
    }

    [Fact]
    public void Validate_EmptyGroupId_ReturnsInvalidIdentity()
    {
        var request = CreateRequest(GroupId1, LearningClassroomScope);
        var result = GroupJoinRequestScopePolicy.Validate(Guid.Empty, LearningClassroomScope, request);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.InvalidIdentity, result.Status);
    }



    #endregion

    #region 2. Compatible Scopes for All Group Kinds

    [Fact]
    public void Validate_LearningClassroom_ReturnsValid()
    {
        var group = CreateGroup(GroupId1, LearningClassroomScope);
        var request = CreateRequest(GroupId1, LearningClassroomScope);

        var result = GroupJoinRequestScopePolicy.Validate(group, request);

        Assert.True(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.Valid, result.Status);
        Assert.True(GroupJoinRequestScopePolicy.IsCompatible(group, request));
    }

    [Fact]
    public void Validate_HobbiesClub_ReturnsValid()
    {
        var group = CreateGroup(GroupId1, HobbiesClubScope);
        var request = CreateRequest(GroupId1, HobbiesClubScope);

        var result = GroupJoinRequestScopePolicy.Validate(group, request);

        Assert.True(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.Valid, result.Status);
        Assert.True(GroupJoinRequestScopePolicy.IsCompatible(group, request));
    }

    [Fact]
    public void Validate_SocialZone_ReturnsValid()
    {
        var group = CreateGroup(GroupId1, SocialZoneScope);
        var request = CreateRequest(GroupId1, SocialZoneScope);

        var result = GroupJoinRequestScopePolicy.Validate(group, request);

        Assert.True(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.Valid, result.Status);
        Assert.True(GroupJoinRequestScopePolicy.IsCompatible(group, request));
    }

    [Fact]
    public void IsCompatible_DirectParameters_ReturnsTrue()
    {
        var request = CreateRequest(GroupId1, LearningClassroomScope);
        Assert.True(GroupJoinRequestScopePolicy.IsCompatible(GroupId1, LearningClassroomScope, request));
    }

    #endregion

    #region 3. Scope Mismatches

    [Fact]
    public void Validate_DifferentGroupId_ReturnsGroupIdMismatch()
    {
        var group = CreateGroup(GroupId1, LearningClassroomScope);
        var request = CreateRequest(GroupId2, LearningClassroomScope);

        var result = GroupJoinRequestScopePolicy.Validate(group, request);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.GroupIdMismatch, result.Status);
        Assert.False(GroupJoinRequestScopePolicy.IsCompatible(group, request));
    }

    [Fact]
    public void Validate_CrossModuleRequest_ReturnsModuleMismatch()
    {
        var group = CreateGroup(GroupId1, LearningClassroomScope);
        var request = CreateRequest(GroupId1, HobbiesClubScope);

        var result = GroupJoinRequestScopePolicy.Validate(group, request);

        Assert.False(result.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, result.Status);
        Assert.False(GroupJoinRequestScopePolicy.IsCompatible(group, request));
    }

    #endregion

    #region 4. Privacy, Diagnostic and Equality Safeguards

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveData()
    {
        var validation = GroupJoinRequestScopeValidation.Success();
        Assert.Equal("GroupJoinRequestScopeValidation [private]", validation.ToString());
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var val1 = GroupJoinRequestScopeValidation.Success();
        var val2 = GroupJoinRequestScopeValidation.Success();

        Assert.Equal(val1, val2);
        Assert.Equal(val1.GetHashCode(), val2.GetHashCode());

        var fail = GroupJoinRequestScopeValidation.Fail(GroupJoinRequestScopeStatus.GroupIdMismatch);
        Assert.NotEqual(val1, fail);
    }

    #endregion
}
