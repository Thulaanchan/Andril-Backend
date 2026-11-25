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

    private static readonly DateTimeOffset TestNow = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static AuthoritativeGroupProfileEvidence CreateProfileEvidence(
        Guid profileId,
        string module = "learning",
        bool isActive = true,
        bool isRestricted = false,
        int? age = 25,
        DateTimeOffset? timestamp = null) =>
        new(profileId, module, isActive, isRestricted, age, timestamp ?? TestNow);

    private static AuthoritativeGroupStateEvidence CreateGroupEvidence(
        Guid groupId,
        GroupModuleScope scope,
        bool isActive = true,
        bool isOpen = true,
        DateTimeOffset? timestamp = null) =>
        new(groupId, scope, isActive, isOpen, timestamp ?? TestNow);

    private static AuthoritativeGroupMembershipEvidence CreateMembershipEvidence(
        Guid groupId,
        Guid profileId,
        bool isMember = true,
        GroupPrivilegedCapability capabilities = GroupPrivilegedCapability.None,
        DateTimeOffset? timestamp = null) =>
        new(groupId, profileId, isMember, capabilities, timestamp ?? TestNow);

    [Fact]
    public void EvaluateStrictAuthorization_MissingAuthoritativeEvidence_ReturnsMissingEvidence()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Null profile evidence
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            null,
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.MissingEvidence, res1.Status);
        Assert.True(res1.IsDenied);

        // Null group evidence
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            null,
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.MissingEvidence, res2.Status);
        Assert.True(res2.IsDenied);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void EvaluateStrictAuthorization_ForgedOrEmptyProfileIdentity_ReturnsInvalidProfile(string? module)
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Empty profile id
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            new AuthoritativeGroupProfileEvidence(Guid.Empty, "learning", true, false, 25, TestNow),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidProfile, res1.Status);
        Assert.True(res1.IsDenied);

        // Empty or null module
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            new AuthoritativeGroupProfileEvidence(profileId, module!, true, false, 25, TestNow),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidProfile, res2.Status);
        Assert.True(res2.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_GroupIdOrScopeMismatch_ReturnsInvalid()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Mismatched group id
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            CreateGroupEvidence(groupId, scope),
            scope,
            otherGroupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidGroup, res1.Status);
        Assert.True(res1.IsDenied);

        // Mismatched scope
        var otherScope = CreateScope("hobbies", GroupKind.Club);
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            CreateGroupEvidence(groupId, scope),
            otherScope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidScope, res2.Status);
        Assert.True(res2.IsDenied);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "hobbies")]
    [InlineData("learning", GroupKind.Classroom, "social")]
    [InlineData("hobbies", GroupKind.Club, "learning")]
    [InlineData("hobbies", GroupKind.Club, "social")]
    [InlineData("social", GroupKind.Zone, "learning")]
    [InlineData("social", GroupKind.Zone, "hobbies")]
    public void EvaluateStrictAuthorization_CrossModuleIdentity_ReturnsModuleMismatch(
        string groupModule,
        GroupKind groupKind,
        string foreignCallerModule)
    {
        var scope = CreateScope(groupModule, groupKind);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, foreignCallerModule),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);

        Assert.Equal(GroupParticipationAuthorizationStatus.ModuleMismatch, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_SocialZone_MissingAgeEvidence_ReturnsMissingEvidence()
    {
        var scope = CreateScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "social", age: null),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);

        Assert.Equal(GroupParticipationAuthorizationStatus.MissingEvidence, res.Status);
        Assert.True(res.IsDenied);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(16)]
    [InlineData(0)]
    public void EvaluateStrictAuthorization_SocialZone_Underage_ReturnsUnderageDenied(int underage)
    {
        var scope = CreateScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "social", age: underage),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);

        Assert.Equal(GroupParticipationAuthorizationStatus.UnderageDenied, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_InactiveGroup_ReturnsInvalidGroup()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            CreateGroupEvidence(groupId, scope, isActive: false),
            scope,
            groupId,
            TestNow);

        Assert.Equal(GroupParticipationAuthorizationStatus.InvalidGroup, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_ClosedGroup_ReturnsClosedGroup()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            CreateGroupEvidence(groupId, scope, isOpen: false),
            scope,
            groupId,
            TestNow);

        Assert.Equal(GroupParticipationAuthorizationStatus.ClosedGroup, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_AccountOrProfileRestricted_ReturnsAccountRestricted()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Inactive profile
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning", isActive: false),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.AccountRestricted, res1.Status);
        Assert.True(res1.IsDenied);

        // Moderated / restricted profile
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning", isRestricted: true),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.AccountRestricted, res2.Status);
        Assert.True(res2.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_StaleEvidence_ReturnsStaleEvidence()
    {
        var scope = CreateScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Expired evidence (> 5 minutes old)
        var staleTime = TestNow.AddMinutes(-6);
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning", timestamp: staleTime),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.StaleEvidence, res1.Status);
        Assert.True(res1.IsDenied);

        // Future evidence
        var futureTime = TestNow.AddMinutes(2);
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning", timestamp: futureTime),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.StaleEvidence, res2.Status);
        Assert.True(res2.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_MissingPrivilegedCapability_ReturnsPrivilegeDenied()
    {
        var scope = CreateScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Requires ApproveJoinRequest, but membership has None
        var membership = CreateMembershipEvidence(groupId, profileId, isMember: true, capabilities: GroupPrivilegedCapability.None);
        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "hobbies"),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow,
            requiredCapability: GroupPrivilegedCapability.ApproveJoinRequest,
            membershipEvidence: membership);

        Assert.Equal(GroupParticipationAuthorizationStatus.PrivilegeDenied, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_UnauthorizedRoleEscalation_WhenNotMember_ReturnsPrivilegeDenied()
    {
        var scope = CreateScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        // Not a member attempting privileged operation
        var membership = CreateMembershipEvidence(groupId, profileId, isMember: false, capabilities: GroupPrivilegedCapability.ApproveJoinRequest);
        var res = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "hobbies"),
            CreateGroupEvidence(groupId, scope),
            scope,
            groupId,
            TestNow,
            requiredCapability: GroupPrivilegedCapability.ApproveJoinRequest,
            membershipEvidence: membership);

        Assert.Equal(GroupParticipationAuthorizationStatus.PrivilegeDenied, res.Status);
        Assert.True(res.IsDenied);
    }

    [Fact]
    public void EvaluateStrictAuthorization_ValidParticipationAcrossModules_ReturnsAuthorized()
    {
        var profileId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        // 1. Learning Classroom
        var learningScope = CreateScope("learning", GroupKind.Classroom);
        var res1 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "learning"),
            CreateGroupEvidence(groupId, learningScope),
            learningScope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, res1.Status);
        Assert.True(res1.IsAuthorized);

        // 2. Hobbies Club
        var hobbiesScope = CreateScope("hobbies", GroupKind.Club);
        var res2 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "hobbies"),
            CreateGroupEvidence(groupId, hobbiesScope),
            hobbiesScope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, res2.Status);
        Assert.True(res2.IsAuthorized);

        // 3. Social Zone (adult >= 18)
        var socialScope = CreateScope("social", GroupKind.Zone);
        var res3 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "social", age: 18),
            CreateGroupEvidence(groupId, socialScope),
            socialScope,
            groupId,
            TestNow);
        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, res3.Status);
        Assert.True(res3.IsAuthorized);

        // 4. Privileged operation with satisfied capability
        var membership = CreateMembershipEvidence(groupId, profileId, isMember: true, capabilities: GroupPrivilegedCapability.ApproveJoinRequest);
        var res4 = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
            CreateProfileEvidence(profileId, "hobbies"),
            CreateGroupEvidence(groupId, hobbiesScope),
            hobbiesScope,
            groupId,
            TestNow,
            requiredCapability: GroupPrivilegedCapability.ApproveJoinRequest,
            membershipEvidence: membership);
        Assert.Equal(GroupParticipationAuthorizationStatus.Authorized, res4.Status);
        Assert.True(res4.IsAuthorized);
    }
}
