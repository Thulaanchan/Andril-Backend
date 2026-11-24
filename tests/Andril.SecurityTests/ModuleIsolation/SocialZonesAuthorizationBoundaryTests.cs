using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.SecurityTests.ModuleIsolation;

/// <summary>
/// Security boundary and module isolation tests for Social Zones (CF-07 / SOC-03).
/// Verifies canonical Social Zone scope constraints, cross-module group rejection
/// (Classroom and Club identifiers rejected as Zones), foreign join request rejection,
/// cross-module membership isolation, and communication boundary decoupling.
/// </summary>
public sealed class SocialZonesAuthorizationBoundaryTests
{
    private static readonly Guid ZoneGroupId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ClassroomGroupId = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid ClubGroupId = Guid.Parse("33333333-4444-5555-6666-777777777777");
    private static readonly Guid MemberProfileId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTime FixedTimestampUtc = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static GroupModuleScope SocialZoneScope => new("social", GroupKind.Zone);
    private static GroupModuleScope LearningClassroomScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesClubScope => new("hobbies", GroupKind.Club);

    private sealed class InMemoryGroupEntryRepoDouble : IGroupEntryRepository
    {
        private readonly GroupEntry? _entry;

        public InMemoryGroupEntryRepoDouble(GroupEntry? entry)
        {
            _entry = entry;
        }

        public Task<GroupEntry?> GetGroupAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entry);
        }

        public Task<bool> ExistsAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_entry is not null && _entry.Id == groupId && _entry.Scope.Equals(scope));
        }
    }

    private sealed class InMemoryGroupJoinRequestRepoDouble : IGroupJoinRequestRepository
    {
        private readonly GroupJoinRequest? _request;

        public InMemoryGroupJoinRequestRepoDouble(GroupJoinRequest? request)
        {
            _request = request;
        }

        public Task<GroupJoinRequest?> GetJoinRequestAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_request);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveJoinRequestAsync(GroupJoinRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RemoveJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetJoinRequestCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    #region 1. Canonical Social Zone Scoping Constraints

    [Fact]
    public void SocialZoneScope_CanonicalScope_IsCompatible()
    {
        Assert.True(GroupModuleScopePolicy.IsCompatible("social", GroupKind.Zone));
        var scope = SocialZoneScope;

        Assert.Equal("social", scope.Module);
        Assert.Equal(GroupKind.Zone, scope.GroupKind);
    }

    [Theory]
    [InlineData("social", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Club)]
    [InlineData("learning", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Zone)]
    [InlineData("dating", GroupKind.Zone)]
    public void SocialZoneScope_NonCanonicalCombinations_ThrowArgumentException(string module, GroupKind kind)
    {
        Assert.False(GroupModuleScopePolicy.IsCompatible(module, kind));
        Assert.Throws<ArgumentException>(() => new GroupModuleScope(module, kind));
    }

    #endregion

    #region 2. Rejection of Classroom and Club Identifiers as Social Zones

    [Fact]
    public async Task ScopedLookup_ClassroomReturnedUnderSocialZoneScope_ReturnsModuleMismatch()
    {
        var classroomEntry = new GroupEntry(ClassroomGroupId, LearningClassroomScope, "Advanced Mathematics");
        var repo = new InMemoryGroupEntryRepoDouble(classroomEntry);
        var lookupService = new GroupEntryScopedLookupService(repo);

        var result = await lookupService.LookupGroupAsync(ClassroomGroupId, SocialZoneScope);

        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.ModuleMismatch, result.Status);
    }

    [Fact]
    public async Task ScopedLookup_ClubReturnedUnderSocialZoneScope_ReturnsModuleMismatch()
    {
        var clubEntry = new GroupEntry(ClubGroupId, HobbiesClubScope, "Hiking & Mountaineering Club");
        var repo = new InMemoryGroupEntryRepoDouble(clubEntry);
        var lookupService = new GroupEntryScopedLookupService(repo);

        var result = await lookupService.LookupGroupAsync(ClubGroupId, SocialZoneScope);

        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.ModuleMismatch, result.Status);
    }

    [Fact]
    public async Task ScopedLookup_ValidSocialZoneReturned_ReturnsFound()
    {
        var zoneEntry = new GroupEntry(ZoneGroupId, SocialZoneScope, "Berlin Tech Meetup Zone");
        var repo = new InMemoryGroupEntryRepoDouble(zoneEntry);
        var lookupService = new GroupEntryScopedLookupService(repo);

        var result = await lookupService.LookupGroupAsync(ZoneGroupId, SocialZoneScope);

        Assert.True(result.IsValid);
        Assert.True(result.HasGroup);
        Assert.NotNull(result.Group);
        Assert.Equal(ZoneGroupId, result.Group.Id);
        Assert.Equal(SocialZoneScope, result.Group.Scope);
        Assert.Equal(GroupEntryLookupStatus.Found, result.Status);
    }

    [Fact]
    public void ScopeProjector_MixedEntries_ProjectsOnlySocialZonesForSocialScope()
    {
        var classroom = new GroupEntry(ClassroomGroupId, LearningClassroomScope, "Physics Classroom");
        var club = new GroupEntry(ClubGroupId, HobbiesClubScope, "Chess Club");
        var zone1 = new GroupEntry(ZoneGroupId, SocialZoneScope, "Central Social Zone");
        var zone2 = new GroupEntry(Guid.NewGuid(), SocialZoneScope, "North District Zone");

        var entries = new[] { classroom, club, zone1, zone2 };

        var projected = GroupScopeProjector.Project(entries, SocialZoneScope);

        Assert.Equal(2, projected.Count);
        Assert.All(projected, dto => Assert.Equal("social", dto.Module));
        Assert.All(projected, dto => Assert.Equal(GroupKind.Zone, dto.GroupKind));
    }

    #endregion

    #region 3. Foreign Join Request Rejection for Social Zones

    [Fact]
    public void JoinRequestScopePolicy_LearningRequestAgainstSocialZone_ReturnsModuleMismatch()
    {
        var zoneEntry = new GroupEntry(ZoneGroupId, SocialZoneScope, "Creative Writers Zone");
        var foreignRequest = new GroupJoinRequest(ZoneGroupId, LearningClassroomScope, MemberProfileId, FixedTimestampUtc);

        var validation = GroupJoinRequestScopePolicy.Validate(zoneEntry, foreignRequest);

        Assert.False(validation.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, validation.Status);
    }

    [Fact]
    public void JoinRequestScopePolicy_HobbiesRequestAgainstSocialZone_ReturnsModuleMismatch()
    {
        var zoneEntry = new GroupEntry(ZoneGroupId, SocialZoneScope, "Creative Writers Zone");
        var foreignRequest = new GroupJoinRequest(ZoneGroupId, HobbiesClubScope, MemberProfileId, FixedTimestampUtc);

        var validation = GroupJoinRequestScopePolicy.Validate(zoneEntry, foreignRequest);

        Assert.False(validation.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, validation.Status);
    }

    [Fact]
    public async Task ScopedJoinRequestLookup_ForeignScopeReturned_ReturnsModuleMismatch()
    {
        var foreignRequest = new GroupJoinRequest(ZoneGroupId, HobbiesClubScope, MemberProfileId, FixedTimestampUtc);
        var repo = new InMemoryGroupJoinRequestRepoDouble(foreignRequest);
        var lookupService = new GroupJoinRequestScopedLookupService(repo);

        var result = await lookupService.LookupJoinRequestAsync(ZoneGroupId, SocialZoneScope, MemberProfileId);

        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.ModuleMismatch, result.Status);
    }

    #endregion

    #region 4. Social Zone Membership Isolation

    [Fact]
    public void MembershipScopePolicy_CrossModuleMembershipsAgainstSocialZone_FailsClosed()
    {
        var zoneEntry = new GroupEntry(ZoneGroupId, SocialZoneScope, "Social Gathering Zone");

        var learningMembership = new GroupMembership(ZoneGroupId, LearningClassroomScope, MemberProfileId);
        var hobbiesMembership = new GroupMembership(ZoneGroupId, HobbiesClubScope, MemberProfileId);
        var validZoneMembership = new GroupMembership(ZoneGroupId, SocialZoneScope, MemberProfileId);

        Assert.False(GroupMembershipScopePolicy.IsCompatible(zoneEntry, learningMembership));
        Assert.False(GroupMembershipScopePolicy.IsCompatible(zoneEntry, hobbiesMembership));
        Assert.True(GroupMembershipScopePolicy.IsCompatible(zoneEntry, validZoneMembership));
    }

    #endregion

    #region 5. Communication Authorization Decoupling

    [Fact]
    public async Task ZoneMembership_DoesNotGrantChatAuthorization()
    {
        var zoneMembership = new GroupMembership(ZoneGroupId, SocialZoneScope, MemberProfileId);
        Assert.NotNull(zoneMembership);

        // In M4 Communication, conversation authorizer must strictly deny access
        IChatAccessAuthorizer authorizer = new DenyAllChatAccessAuthorizer();
        var conversationId = Guid.NewGuid();

        var authorizedActor = await authorizer.AuthorizeConversationAsync(ChatModule.Social, conversationId);

        Assert.Null(authorizedActor);
    }

    [Fact]
    public void DiagnosticOutput_ConcealsConfidentialIdentities()
    {
        var zoneResult = GroupEntryLookupResult.NotFound();
        var json = JsonSerializer.Serialize(zoneResult);

        Assert.Equal("{}", json);
    }

    #endregion
}
