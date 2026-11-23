using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.SecurityTests.ModuleIsolation;

/// <summary>
/// Regression and security boundary tests verifying strict module isolation across Groups boundaries:
/// - Learning/Classroom, Hobbies/Club, and Social/Zone scope compartmentalization.
/// - Prevention of cross-module group mismatch, foreign group spoofing, and profile identity confusion.
/// - Fail-closed handling of repository scope violations and absent records without implicit membership or role grants.
/// - Information concealment ensuring raw GUIDs are not leaked via serialization or diagnostic representations.
/// </summary>
public sealed class GroupsModuleIsolationRegressionTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime ValidTimestamp = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private sealed class SecurityTestGroupMembershipRepoDouble : IGroupMembershipRepository
    {
        public Func<Guid, GroupModuleScope, Guid, CancellationToken, Task<GroupMembership?>>? GetMembershipHandler { get; set; }

        public Task<GroupMembership?> GetMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetMembershipHandler is not null)
            {
                return GetMembershipHandler(groupId, scope, memberProfileId, cancellationToken);
            }
            return Task.FromResult<GroupMembership?>(null);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RemoveMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    #region 1. Module Scope Isolation and Allowed Combinations

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void ScopePolicy_CanonicalModulePairs_AreCompatible(string module, GroupKind kind)
    {
        Assert.True(GroupModuleScopePolicy.IsCompatible(module, kind));
        var scope = new GroupModuleScope(module, kind);
        Assert.Equal(module, scope.Module);
        Assert.Equal(kind, scope.GroupKind);
    }

    [Theory]
    [InlineData("learning", GroupKind.Club)]
    [InlineData("learning", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Zone)]
    [InlineData("social", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Club)]
    public void ScopePolicy_CrossModulePairs_ThrowArgumentException(string module, GroupKind kind)
    {
        Assert.False(GroupModuleScopePolicy.IsCompatible(module, kind));
        Assert.Throws<ArgumentException>(() => new GroupModuleScope(module, kind));
    }

    [Theory]
    [InlineData("admin", GroupKind.Classroom)]
    [InlineData("economy", GroupKind.Club)]
    [InlineData("unknown", GroupKind.Zone)]
    [InlineData("", GroupKind.Classroom)]
    [InlineData("   ", GroupKind.Club)]
    [InlineData(null, GroupKind.Zone)]
    public void ScopePolicy_UnauthorizedOrInvalidModuleNames_ThrowArgumentException(string? module, GroupKind kind)
    {
        Assert.False(GroupModuleScopePolicy.IsCompatible(module, kind));
        Assert.Throws<ArgumentException>(() => new GroupModuleScope(module, kind));
    }

    #endregion

    #region 2. Group Entry and Membership Scope Compatibility Boundaries

    [Fact]
    public void MembershipScopePolicy_CrossModuleMismatch_FailsClosed()
    {
        var learningGroup = new GroupEntry(GroupId1, LearningScope, "Math Classroom");
        var hobbiesGroup = new GroupEntry(GroupId1, HobbiesScope, "Photography Club");
        var socialGroup = new GroupEntry(GroupId1, SocialScope, "Downtown Zone");

        var learningMembership = new GroupMembership(GroupId1, LearningScope, ProfileId1);
        var hobbiesMembership = new GroupMembership(GroupId1, HobbiesScope, ProfileId1);
        var socialMembership = new GroupMembership(GroupId1, SocialScope, ProfileId1);

        // Learning group must reject Hobbies and Social memberships
        Assert.False(GroupMembershipScopePolicy.IsCompatible(learningGroup, hobbiesMembership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(learningGroup, socialMembership));

        // Hobbies group must reject Learning and Social memberships
        Assert.False(GroupMembershipScopePolicy.IsCompatible(hobbiesGroup, learningMembership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(hobbiesGroup, socialMembership));

        // Social group must reject Learning and Hobbies memberships
        Assert.False(GroupMembershipScopePolicy.IsCompatible(socialGroup, learningMembership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(socialGroup, hobbiesMembership));

        // Canonical matching scopes succeed
        Assert.True(GroupMembershipScopePolicy.IsCompatible(learningGroup, learningMembership));
        Assert.True(GroupMembershipScopePolicy.IsCompatible(hobbiesGroup, hobbiesMembership));
        Assert.True(GroupMembershipScopePolicy.IsCompatible(socialGroup, socialMembership));
    }

    [Fact]
    public void MembershipScopePolicy_GroupIdMismatch_FailsClosed()
    {
        var group = new GroupEntry(GroupId1, LearningScope, "Physics Classroom");
        var foreignMembership = new GroupMembership(GroupId2, LearningScope, ProfileId1);

        Assert.False(GroupMembershipScopePolicy.IsCompatible(group, foreignMembership));
    }

    [Fact]
    public void MembershipScopePolicy_NullOrEmptyParameters_FailsClosed()
    {
        var group = new GroupEntry(GroupId1, LearningScope, "Chemistry Classroom");
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);

        Assert.False(GroupMembershipScopePolicy.IsCompatible(null, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(group, null));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(Guid.Empty, LearningScope, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(GroupId1, null, membership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(null, LearningScope));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(LearningScope, null));
    }

    #endregion

    #region 3. Group Join Request Cross-Module Boundaries

    [Fact]
    public void JoinRequestScopePolicy_CrossModuleScopeMismatch_ReturnsScopeOrModuleMismatch()
    {
        var learningGroup = new GroupEntry(GroupId1, LearningScope, "Biology Classroom");
        var hobbiesGroup = new GroupEntry(GroupId1, HobbiesScope, "Robotics Club");

        var hobbiesJoinRequest = new GroupJoinRequest(GroupId1, HobbiesScope, ProfileId1, ValidTimestamp);
        var socialJoinRequest = new GroupJoinRequest(GroupId1, SocialScope, ProfileId1, ValidTimestamp);

        var val1 = GroupJoinRequestScopePolicy.Validate(learningGroup, hobbiesJoinRequest);
        Assert.False(val1.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, val1.Status);

        var val2 = GroupJoinRequestScopePolicy.Validate(hobbiesGroup, socialJoinRequest);
        Assert.False(val2.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, val2.Status);

        var val3 = GroupJoinRequestScopePolicy.Validate(learningGroup, socialJoinRequest);
        Assert.False(val3.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, val3.Status);
    }

    [Fact]
    public void JoinRequestScopePolicy_GroupIdMismatch_ReturnsGroupIdMismatch()
    {
        var group = new GroupEntry(GroupId1, LearningScope, "History Classroom");
        var foreignRequest = new GroupJoinRequest(GroupId2, LearningScope, ProfileId1, ValidTimestamp);

        var validation = GroupJoinRequestScopePolicy.Validate(group, foreignRequest);

        Assert.False(validation.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.GroupIdMismatch, validation.Status);
    }

    [Fact]
    public void JoinRequestScopePolicy_NullOrEmptyInputs_FailsClosed()
    {
        var group = new GroupEntry(GroupId1, LearningScope, "English Classroom");
        var request = new GroupJoinRequest(GroupId1, LearningScope, ProfileId1, ValidTimestamp);

        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, GroupJoinRequestScopePolicy.Validate(null, request).Status);
        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, GroupJoinRequestScopePolicy.Validate(group, null).Status);
        Assert.Equal(GroupJoinRequestScopeStatus.NullInput, GroupJoinRequestScopePolicy.Validate(GroupId1, null, request).Status);
        Assert.Equal(GroupJoinRequestScopeStatus.InvalidIdentity, GroupJoinRequestScopePolicy.Validate(Guid.Empty, LearningScope, request).Status);
    }

    #endregion

    #region 4. Group Membership Read Scope Verification

    [Fact]
    public void ReadScopePolicy_MemberProfileIdMismatch_ReturnsMemberProfileIdMismatch()
    {
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);

        var validation = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, ProfileId2, membership);

        Assert.False(validation.IsValid);
        Assert.False(validation.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.MemberProfileIdMismatch, validation.Status);
    }

    [Fact]
    public void ReadScopePolicy_GroupIdMismatch_ReturnsGroupIdMismatch()
    {
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);

        var validation = GroupMembershipReadScopePolicy.Validate(GroupId2, LearningScope, ProfileId1, membership);

        Assert.False(validation.IsValid);
        Assert.False(validation.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.GroupIdMismatch, validation.Status);
    }

    [Fact]
    public void ReadScopePolicy_CrossModuleScopeMismatch_ReturnsScopeMismatch()
    {
        var membership = new GroupMembership(GroupId1, HobbiesScope, ProfileId1);

        var validation = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, ProfileId1, membership);

        Assert.False(validation.IsValid);
        Assert.False(validation.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.ScopeMismatch, validation.Status);
    }

    [Fact]
    public void ReadScopePolicy_AbsentMembership_ReturnsNullResultWithoutMembership()
    {
        var validation = GroupMembershipReadScopePolicy.Validate(GroupId1, LearningScope, ProfileId1, null);

        Assert.True(validation.IsValid);
        Assert.False(validation.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, validation.Status);
    }

    #endregion

    #region 5. Scoped Lookup Service Module Isolation Boundaries

    [Fact]
    public async Task ScopedLookupService_CrossModuleRecordReturned_FailsClosedWithScopeMismatch()
    {
        var returnedMembership = new GroupMembership(GroupId1, HobbiesScope, ProfileId1);

        var repo = new SecurityTestGroupMembershipRepoDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(returnedMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.ScopeMismatch, result.Status);
    }

    [Fact]
    public async Task ScopedLookupService_ForeignGroupIdReturned_FailsClosedWithGroupIdMismatch()
    {
        var returnedMembership = new GroupMembership(GroupId2, LearningScope, ProfileId1);

        var repo = new SecurityTestGroupMembershipRepoDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(returnedMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.GroupIdMismatch, result.Status);
    }

    [Fact]
    public async Task ScopedLookupService_ForeignMemberProfileReturned_FailsClosedWithProfileMismatch()
    {
        var returnedMembership = new GroupMembership(GroupId1, LearningScope, ProfileId2);

        var repo = new SecurityTestGroupMembershipRepoDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(returnedMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.MemberProfileIdMismatch, result.Status);
    }

    [Fact]
    public async Task ScopedLookupService_AbsentMembership_ReturnsValidNullResultCleanly()
    {
        var repo = new SecurityTestGroupMembershipRepoDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(null)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, result.Status);
    }

    #endregion

    #region 6. Information Disclosure and Diagnostic Concealment

    [Fact]
    public void Serialization_MembershipAndJoinRequest_ConcealConfidentialIds()
    {
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);
        var request = new GroupJoinRequest(GroupId1, LearningScope, ProfileId1, ValidTimestamp);

        var membershipJson = JsonSerializer.Serialize(membership);
        var requestJson = JsonSerializer.Serialize(request);

        Assert.DoesNotContain(GroupId1.ToString(), membershipJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId1.ToString(), membershipJson, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(GroupId1.ToString(), requestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId1.ToString(), requestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToString_ConcealsConfidentialGuids()
    {
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);
        var request = new GroupJoinRequest(GroupId1, LearningScope, ProfileId1, ValidTimestamp);
        var joinValidation = GroupJoinRequestScopeValidation.Success();
        var readValidation = GroupMembershipReadScopeValidation.Success();
        var lookupResult = GroupMembershipLookupResult.Found(membership, readValidation);

        Assert.DoesNotContain(GroupId1.ToString(), membership.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId1.ToString(), membership.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(GroupId1.ToString(), request.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId1.ToString(), request.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(GroupId1.ToString(), joinValidation.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(GroupId1.ToString(), readValidation.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(GroupId1.ToString(), lookupResult.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("[private]", joinValidation.ToString());
        Assert.Contains("[private]", readValidation.ToString());
        Assert.Contains("GroupMembershipLookupResult", lookupResult.ToString());
    }

    #endregion
}
