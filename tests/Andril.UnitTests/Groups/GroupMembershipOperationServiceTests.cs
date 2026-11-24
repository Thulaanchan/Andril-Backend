using System;
using System.Collections.Generic;
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

namespace Andril.UnitTests.Groups;

public sealed class GroupMembershipOperationServiceTests
{
    private sealed class InMemoryGroupEntryRepository : IGroupEntryRepository
    {
        private readonly Dictionary<(Guid, string), GroupEntry> _groups = new();

        public void Add(GroupEntry entry) =>
            _groups[(entry.Id, entry.Scope.Module.ToLowerInvariant())] = entry;

        public Task<GroupEntry?> GetGroupAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
        {
            _groups.TryGetValue((groupId, scope.Module.ToLowerInvariant()), out var entry);
            return Task.FromResult(entry);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_groups.ContainsKey((groupId, scope.Module.ToLowerInvariant())));
    }

    private sealed class InMemoryGroupMembershipRepository : IGroupMembershipRepository
    {
        private readonly HashSet<(Guid, Guid)> _memberships = new();

        public void Add(Guid groupId, Guid profileId) =>
            _memberships.Add((groupId, profileId));

        public Task<GroupMembership?> GetMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default)
        {
            if (_memberships.Contains((groupId, memberProfileId)))
            {
                return Task.FromResult<GroupMembership?>(new GroupMembership(groupId, scope, memberProfileId));
            }
            return Task.FromResult<GroupMembership?>(null);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_memberships.Contains((groupId, memberProfileId)));

        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default)
        {
            _memberships.Add((membership.GroupId, membership.MemberProfileId));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_memberships.Remove((groupId, memberProfileId)));

        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_memberships.Count);
    }

    private sealed class InMemoryGroupJoinRequestRepository : IGroupJoinRequestRepository
    {
        private readonly Dictionary<(Guid, Guid), GroupJoinRequest> _requests = new();

        public void Add(GroupJoinRequest request) =>
            _requests[(request.GroupId, request.RequestingProfileId)] = request;

        public Task<GroupJoinRequest?> GetJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default)
        {
            _requests.TryGetValue((groupId, requestingProfileId), out var req);
            return Task.FromResult(req);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_requests.ContainsKey((groupId, requestingProfileId)));

        public Task SaveJoinRequestAsync(GroupJoinRequest joinRequest, CancellationToken cancellationToken = default)
        {
            _requests[(joinRequest.GroupId, joinRequest.RequestingProfileId)] = joinRequest;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_requests.Remove((groupId, requestingProfileId)));

        public Task<int> GetJoinRequestCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(_requests.Count);
    }

    private static (GroupMembershipOperationService Service, InMemoryGroupEntryRepository EntryRepo, InMemoryGroupMembershipRepository MembershipRepo, InMemoryGroupJoinRequestRepository JoinRepo) CreateService()
    {
        var entryRepo = new InMemoryGroupEntryRepository();
        var membershipRepo = new InMemoryGroupMembershipRepository();
        var joinRepo = new InMemoryGroupJoinRequestRepository();
        var service = new GroupMembershipOperationService(entryRepo, membershipRepo, joinRepo);
        return (service, entryRepo, membershipRepo, joinRepo);
    }

    [Fact]
    public async Task SubmitJoinRequestAsync_ValidRequest_PersistsAndSucceeds()
    {
        var (service, entryRepo, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        entryRepo.Add(new GroupEntry(groupId, scope, "Chess Club"));

        var result = await service.SubmitJoinRequestAsync(groupId, scope, "hobbies", profileId, callerAge: 25);

        Assert.True(result.Success);
        Assert.True(await joinRepo.ExistsAsync(groupId, scope, profileId));
    }

    [Fact]
    public async Task SubmitJoinRequestAsync_AlreadyMember_ThrowsInvalidOperationException()
    {
        var (service, entryRepo, membershipRepo, _) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        entryRepo.Add(new GroupEntry(groupId, scope, "Algebra 101"));
        membershipRepo.Add(groupId, profileId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitJoinRequestAsync(groupId, scope, "learning", profileId, callerAge: 20));

        Assert.Contains("already an active member", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitJoinRequestAsync_AlreadyPendingRequest_ThrowsInvalidOperationException()
    {
        var (service, entryRepo, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        entryRepo.Add(new GroupEntry(groupId, scope, "Algebra 101"));
        joinRepo.Add(new GroupJoinRequest(groupId, scope, profileId, DateTime.UtcNow));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitJoinRequestAsync(groupId, scope, "learning", profileId, callerAge: 20));

        Assert.Contains("already pending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitJoinRequestAsync_NonExistentGroup_ThrowsInvalidOperationException()
    {
        var (service, _, _, _) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitJoinRequestAsync(groupId, scope, "learning", profileId, callerAge: 20));

        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitJoinRequestAsync_MismatchedModule_ThrowsUnauthorizedAccessException()
    {
        var (service, entryRepo, _, _) = CreateService();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        entryRepo.Add(new GroupEntry(groupId, scope, "Social Zone"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SubmitJoinRequestAsync(groupId, scope, "learning", profileId, callerAge: 25));
    }

    [Fact]
    public async Task CancelJoinRequestAsync_ExistingRequest_CancelsAndRemovesEntity()
    {
        var (service, _, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, profileId, DateTime.UtcNow));

        var result = await service.CancelJoinRequestAsync(groupId, scope, "hobbies", profileId);

        Assert.True(result.Success);
        Assert.True(result.EntityRemoved);
        Assert.False(await joinRepo.ExistsAsync(groupId, scope, profileId));
    }

    [Fact]
    public async Task CancelJoinRequestAsync_NonExistentRequest_ThrowsInvalidOperationException()
    {
        var (service, _, _, _) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelJoinRequestAsync(groupId, scope, "hobbies", profileId));
    }

    [Fact]
    public async Task CancelJoinRequestAsync_CrossModule_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, profileId, DateTime.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CancelJoinRequestAsync(groupId, scope, "learning", profileId));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_ApproveByAuthorizedRole_SavesMembershipAndRemovesRequest()
    {
        var (service, _, membershipRepo, joinRepo) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, applicantId, DateTime.UtcNow));

        var result = await service.ProcessJoinRequestAsync(
            groupId, scope, "learning", actorId, isActorAuthorizedRole: true,
            applicantProfileId: applicantId, approve: true, applicantAge: 20);

        Assert.True(result.Success);
        Assert.True(result.MembershipCreated);
        Assert.True(await membershipRepo.ExistsAsync(groupId, scope, applicantId));
        Assert.False(await joinRepo.ExistsAsync(groupId, scope, applicantId));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_RejectByAuthorizedRole_RemovesRequestWithoutMembership()
    {
        var (service, _, membershipRepo, joinRepo) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, applicantId, DateTime.UtcNow));

        var result = await service.ProcessJoinRequestAsync(
            groupId, scope, "learning", actorId, isActorAuthorizedRole: true,
            applicantProfileId: applicantId, approve: false, applicantAge: 20);

        Assert.True(result.Success);
        Assert.False(result.MembershipCreated);
        Assert.True(result.EntityRemoved);
        Assert.False(await membershipRepo.ExistsAsync(groupId, scope, applicantId));
        Assert.False(await joinRepo.ExistsAsync(groupId, scope, applicantId));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_NonAuthorizedRole_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, applicantId, DateTime.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessJoinRequestAsync(
                groupId, scope, "learning", actorId, isActorAuthorizedRole: false,
                applicantProfileId: applicantId, approve: true, applicantAge: 20));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_CrossModuleActor_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, applicantId, DateTime.UtcNow));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessJoinRequestAsync(
                groupId, scope, "hobbies", actorId, isActorAuthorizedRole: true,
                applicantProfileId: applicantId, approve: true, applicantAge: 20));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_NonExistentRequest_ThrowsInvalidOperationException()
    {
        var (service, _, _, _) = CreateService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessJoinRequestAsync(
                groupId, scope, "learning", actorId, isActorAuthorizedRole: true,
                applicantProfileId: applicantId, approve: true, applicantAge: 20));
    }

    [Fact]
    public async Task ProcessJoinRequestAsync_ApproveUnderageApplicantForSocialZone_ThrowsInvalidOperationException()
    {
        var (service, _, _, joinRepo) = CreateService();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        joinRepo.Add(new GroupJoinRequest(groupId, scope, applicantId, DateTime.UtcNow));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessJoinRequestAsync(
                groupId, scope, "social", actorId, isActorAuthorizedRole: true,
                applicantProfileId: applicantId, approve: true, applicantAge: 17));

        Assert.Contains("ineligible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaveGroupAsync_ActiveMember_RemovesMembershipAndSucceeds()
    {
        var (service, _, membershipRepo, _) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        membershipRepo.Add(groupId, profileId);

        var result = await service.LeaveGroupAsync(groupId, scope, "hobbies", profileId);

        Assert.True(result.Success);
        Assert.True(result.EntityRemoved);
        Assert.False(await membershipRepo.ExistsAsync(groupId, scope, profileId));
    }

    [Fact]
    public async Task LeaveGroupAsync_NotMember_ThrowsInvalidOperationException()
    {
        var (service, _, _, _) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LeaveGroupAsync(groupId, scope, "hobbies", profileId));
    }

    [Fact]
    public async Task LeaveGroupAsync_CrossModule_ThrowsUnauthorizedAccessException()
    {
        var (service, _, membershipRepo, _) = CreateService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        membershipRepo.Add(groupId, profileId);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LeaveGroupAsync(groupId, scope, "social", profileId));
    }

    [Fact]
    public void GroupJoinOperationResult_JsonSerialization_ConcealsSensitiveState()
    {
        var result = GroupJoinOperationResult.Approved();

        Assert.Equal("{}", JsonSerializer.Serialize(result));
        Assert.Equal("GroupJoinOperationResult [private]", result.ToString());
    }
}
