using System;
using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupParticipationAuthorizationPolicyTests
{
    private static GroupModuleScope CreateScope(string module, GroupKind kind) =>
        new(module, kind);

    [Fact]
    public void EvaluateParticipation_LearningClassroom_WithLearningProfile_Authorized()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "learning", profileId, callerAge: 20);

        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, result.Status);
        Assert.True(result.IsAuthorized);
        Assert.False(result.IsDenied);
        Assert.Equal(GroupKind.Classroom, result.GroupKind);
        Assert.Equal("learning", result.Module);
    }

    [Fact]
    public void EvaluateParticipation_HobbiesClub_WithHobbiesProfile_Authorized()
    {
        var scope = CreateScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "hobbies", profileId, callerAge: 22);

        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, result.Status);
        Assert.True(result.IsAuthorized);
        Assert.False(result.IsDenied);
        Assert.Equal(GroupKind.Club, result.GroupKind);
        Assert.Equal("hobbies", result.Module);
    }

    [Fact]
    public void EvaluateParticipation_SocialZone_WithAdultSocialProfile_Authorized()
    {
        var scope = CreateScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "social", profileId, callerAge: 18);

        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, result.Status);
        Assert.True(result.IsAuthorized);
        Assert.False(result.IsDenied);
        Assert.Equal(GroupKind.Zone, result.GroupKind);
        Assert.Equal("social", result.Module);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "hobbies")]
    [InlineData("learning", GroupKind.Classroom, "social")]
    [InlineData("hobbies", GroupKind.Club, "learning")]
    [InlineData("hobbies", GroupKind.Club, "social")]
    [InlineData("social", GroupKind.Zone, "learning")]
    [InlineData("social", GroupKind.Zone, "hobbies")]
    public void EvaluateParticipation_CrossModuleAccess_ReturnsModuleMismatchAndDenied(
        string groupModule,
        GroupKind groupKind,
        string foreignCallerModule)
    {
        var scope = CreateScope(groupModule, groupKind);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, foreignCallerModule, profileId, callerAge: 25);

        Assert.Equal(GroupParticipationAuthorizationStatus.ModuleMismatch, result.Status);
        Assert.False(result.IsAuthorized);
        Assert.True(result.IsDenied);
    }

    [Theory]
    [InlineData(17, GroupParticipationAuthorizationStatus.UnderageDenied)]
    [InlineData(16, GroupParticipationAuthorizationStatus.UnderageDenied)]
    [InlineData(18, GroupParticipationAuthorizationStatus.Authorized)]
    [InlineData(21, GroupParticipationAuthorizationStatus.Authorized)]
    public void EvaluateParticipation_SocialZone_AgeGateVerification(
        int callerAge,
        GroupParticipationAuthorizationStatus expectedStatus)
    {
        var scope = CreateScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "social", profileId, callerAge: callerAge);

        Assert.Equal(expectedStatus, result.Status);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, 15)]
    [InlineData("hobbies", GroupKind.Club, 16)]
    public void EvaluateParticipation_NonSocialGroups_PermitMinors(
        string module,
        GroupKind kind,
        int age)
    {
        var scope = CreateScope(module, kind);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, module, profileId, callerAge: age);

        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, result.Status);
        Assert.True(result.IsAuthorized);
    }

    [Fact]
    public void EvaluateParticipation_ClosedGroup_ReturnsClosedGroupAndDenied()
    {
        var scope = CreateScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "hobbies", profileId, callerAge: 25, isGroupOpen: false);

        Assert.Equal(GroupParticipationAuthorizationStatus.ClosedGroup, result.Status);
        Assert.False(result.IsAuthorized);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public void EvaluateParticipation_NullOrEmptyGroup_ReturnsInvalidGroup()
    {
        var profileId = Guid.NewGuid();

        var nullScope = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            null, Guid.NewGuid(), "learning", profileId);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidGroup, nullScope.Status);
        Assert.True(nullScope.IsDenied);

        var scope = CreateScope("learning", GroupKind.Classroom);
        var emptyGroupId = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, Guid.Empty, "learning", profileId);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidGroup, emptyGroupId.Status);
        Assert.True(emptyGroupId.IsDenied);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void EvaluateParticipation_InvalidCallerModule_ReturnsInvalidProfile(string? callerModule)
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, callerModule, profileId);

        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidProfile, result.Status);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public void EvaluateParticipation_EmptyCallerProfileId_ReturnsInvalidProfile()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();

        var result = GroupParticipationAuthorizationPolicy.EvaluateParticipation(
            scope, groupId, "learning", Guid.Empty);

        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidProfile, result.Status);
        Assert.True(result.IsDenied);
    }

    [Fact]
    public void GroupParticipationAuthorizationResult_JsonSerialization_ConcealsSensitiveState()
    {
        var result = new GroupParticipationAuthorizationResult(
            GroupParticipationAuthorizationStatus.Authorized,
            GroupKind.Classroom,
            "learning");

        var json = JsonSerializer.Serialize(result);
        Assert.Equal("{}", json);
        Assert.Equal("GroupParticipationAuthorizationResult [private]", result.ToString());
    }
}
