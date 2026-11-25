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

    private sealed class StubGroupMembershipTransactionRepository : IGroupMembershipTransactionRepository
    {
        public bool SubmitCalled { get; private set; }
        public bool CancelCalled { get; private set; }
        public bool ProcessAdmissionCalled { get; private set; }
        public bool LeaveCalled { get; private set; }
        public bool ReconcileCalled { get; private set; }
        public bool ShouldThrow { get; set; }

        public GroupJoinRequestSubmissionTransactionResult SubmitResult { get; set; } =
            new(true, GroupMembershipTransactionStatus.Committed);

        public GroupJoinRequestCancellationTransactionResult CancelResult { get; set; } =
            new(true, GroupMembershipTransactionStatus.Committed, requestCancelled: true);

        public GroupAdmissionTransactionResult AdmissionResult { get; set; } =
            new(true, GroupMembershipTransactionStatus.Committed, membershipCreated: true, requestResolved: true);

        public GroupDepartureTransactionResult LeaveResult { get; set; } =
            new(true, GroupMembershipTransactionStatus.Committed, membershipRemoved: true);

        public GroupTransactionReconciliationResult ReconcileResult { get; set; } =
            GroupTransactionReconciliationResult.Confirmed(GroupMembershipTransactionStatus.Committed);

        public Task<GroupJoinRequestSubmissionTransactionResult> SubmitJoinRequestAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid requestingProfileId, DateTime requestedAtUtc,
            long? expectedRevision = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository transaction failure");
            SubmitCalled = true;
            return Task.FromResult(SubmitResult);
        }

        public Task<GroupJoinRequestCancellationTransactionResult> CancelJoinRequestAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid requestingProfileId,
            long? expectedRevision = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository transaction failure");
            CancelCalled = true;
            return Task.FromResult(CancelResult);
        }

        public Task<GroupAdmissionTransactionResult> ProcessAdmissionAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid applicantProfileId, bool approve,
            Guid actorProfileId, DateTime decisionAtUtc, long? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository transaction failure");
            ProcessAdmissionCalled = true;
            return Task.FromResult(AdmissionResult);
        }

        public Task<GroupDepartureTransactionResult> LeaveGroupAtomicAsync(
            Guid groupId, GroupModuleScope scope, Guid memberProfileId,
            long? expectedRevision = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository transaction failure");
            LeaveCalled = true;
            return Task.FromResult(LeaveResult);
        }

        public Task<GroupTransactionReconciliationResult> ReconcileTransactionOutcomeAsync(
            Guid groupId, GroupModuleScope scope, Guid operationCorrelationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Repository transaction failure");
            ReconcileCalled = true;
            return Task.FromResult(ReconcileResult);
        }
    }

    private sealed class StubGroupAuthoritativeEvidenceProvider : IGroupAuthoritativeEvidenceProvider
    {
        public Dictionary<Guid, AuthoritativeGroupProfileEvidence> Profiles { get; } = new();
        public Dictionary<Guid, AuthoritativeGroupStateEvidence> Groups { get; } = new();
        public Dictionary<(Guid, Guid), AuthoritativeGroupMembershipEvidence> Memberships { get; } = new();
        public HashSet<(Guid, Guid)> Owners { get; } = new();
        public int MemberCount { get; set; } = 5;
        public int? GroupCapacity { get; set; } = 100;
        public bool ShouldThrow { get; set; }

        public Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(
            Guid profileId, string module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            Profiles.TryGetValue(profileId, out var ev);
            return Task.FromResult(ev);
        }

        public Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            Groups.TryGetValue(groupId, out var ev);
            return Task.FromResult(ev);
        }

        public Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(
            Guid groupId, Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            Memberships.TryGetValue((groupId, profileId), out var ev);
            return Task.FromResult(ev);
        }

        public Task<bool> IsGroupOwnerAsync(
            Guid groupId, Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            return Task.FromResult(Owners.Contains((groupId, profileId)));
        }

        public Task<int> GetCurrentMemberCountAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            return Task.FromResult(MemberCount);
        }

        public Task<int?> GetGroupCapacityAsync(
            Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Evidence provider failure");
            return Task.FromResult(GroupCapacity);
        }
    }

    private sealed class StubGroupActorContext : IGroupActorContext
    {
        public AuthoritativeGroupProfileEvidence? CurrentActor { get; set; }
        public bool ShouldThrow { get; set; }

        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(
            string expectedModule, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldThrow) throw new InvalidOperationException("Actor context failure");
            return Task.FromResult(CurrentActor);
        }
    }

    private static (GroupMembershipOperationService Service,
        InMemoryGroupEntryRepository EntryRepo,
        InMemoryGroupMembershipRepository MembershipRepo,
        InMemoryGroupJoinRequestRepository JoinRepo,
        StubGroupMembershipTransactionRepository TxRepo,
        StubGroupAuthoritativeEvidenceProvider EvidenceProvider,
        StubGroupActorContext ActorContext) CreateStrictService()
    {
        var entryRepo = new InMemoryGroupEntryRepository();
        var membershipRepo = new InMemoryGroupMembershipRepository();
        var joinRepo = new InMemoryGroupJoinRequestRepository();
        var txRepo = new StubGroupMembershipTransactionRepository();
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider();
        var actorContext = new StubGroupActorContext();

        var service = new GroupMembershipOperationService(
            entryRepo, membershipRepo, joinRepo, txRepo, evidenceProvider, actorContext);

        return (service, entryRepo, membershipRepo, joinRepo, txRepo, evidenceProvider, actorContext);
    }

    private static DateTimeOffset TestNow => DateTimeOffset.UtcNow;

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

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_SuccessfulAuthorizedAdmission_InvokesAtomicAdmissionAndSucceeds()
    {
        var (service, _, membershipRepo, joinRepo, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "hobbies", true, false, 22, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);

        var committedMembership = new GroupMembership(groupId, scope, applicantId);
        txRepo.AdmissionResult = GroupAdmissionTransactionResult.Approved(committedMembership);

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.True(result.Success);
        Assert.True(result.MembershipCreated);
        Assert.True(result.EntityRemoved);
        Assert.True(txRepo.ProcessAdmissionCalled);

        // Demonstrates that atomic contract was invoked rather than separate membership-save and request-delete operations
        Assert.Equal(0, await membershipRepo.GetMemberCountAsync(groupId, scope));
        Assert.Equal(0, await joinRepo.GetJoinRequestCountAsync(groupId, scope));
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_FailedAtomicAdmission_ReturnsFailedResultWithoutPartialSuccess()
    {
        var (service, _, membershipRepo, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "hobbies", true, false, 22, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);

        txRepo.AdmissionResult = new GroupAdmissionTransactionResult(
            success: false, GroupMembershipTransactionStatus.CapacityExceeded, membershipCreated: false, requestResolved: false);

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
        Assert.False(result.MembershipCreated);
        Assert.False(await membershipRepo.ExistsAsync(groupId, scope, applicantId));
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_NoPartialMembershipSuccess_WhenAdmissionTransactionFails()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "learning", true, false, 30, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "learning", true, false, 20, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);

        txRepo.AdmissionResult = new GroupAdmissionTransactionResult(
            success: false, GroupMembershipTransactionStatus.ConcurrencyConflict, membershipCreated: false, requestResolved: false);

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
        Assert.False(result.MembershipCreated);
        Assert.False(result.EntityRemoved);
    }

    [Fact]
    public async Task SubmitJoinRequestStrictAsync_DuplicateRequestReconciliation_ReturnsFailedResult()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 25, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);

        // 1. Reconcile already pending
        var existingRequest = new GroupJoinRequest(groupId, scope, actorId, DateTime.UtcNow);
        txRepo.SubmitResult = GroupJoinRequestSubmissionTransactionResult.AlreadyPending(existingRequest);

        var res1 = await service.SubmitJoinRequestStrictAsync(groupId, scope);
        Assert.False(res1.Success);

        // 2. Reconcile already member
        txRepo.SubmitResult = GroupJoinRequestSubmissionTransactionResult.AlreadyMember();

        var res2 = await service.SubmitJoinRequestStrictAsync(groupId, scope);
        Assert.False(res2.Success);
    }

    [Fact]
    public async Task SubmitJoinRequestStrictAsync_InvalidGroupScope_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, _, _, _, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(Guid.NewGuid(), "hobbies", true, false, 25, TestNow);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SubmitJoinRequestStrictAsync(Guid.Empty, scope));
    }

    [Fact]
    public async Task SubmitJoinRequestStrictAsync_InvalidActorIdentity_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, _, _, _, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();

        actorContext.CurrentActor = null;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SubmitJoinRequestStrictAsync(groupId, scope));
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_MissingPrivilegedCapability_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, _, _, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        // Lacks ApproveJoinRequest capability
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.None, TestNow);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true));
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_UnderageApplicantForSocialZone_ReturnsFailedResult()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "social", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);
        // Underage applicant (17)
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "social", true, false, 17, TestNow);

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
        Assert.False(txRepo.ProcessAdmissionCalled);
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_ClosedGroup_ThrowsUnauthorizedAccessException()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "learning", true, false, 28, TestNow);
        // Closed group
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, false, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "learning", true, false, 20, TestNow);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true));

        Assert.False(txRepo.ProcessAdmissionCalled);
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_StaleAuthorizationEvidence_ReturnsFailedResult()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "learning", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);
        // Stale applicant evidence (> 5 minutes)
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(
            applicantId, "learning", true, false, 20, DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
        Assert.False(txRepo.ProcessAdmissionCalled);
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_CapacityDenial_DeniesAdmissionWithoutInvokingTransaction()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "hobbies", true, false, 22, TestNow);

        // Capacity limit reached (current count 10 >= capacity 10)
        evidenceProvider.MemberCount = 10;
        evidenceProvider.GroupCapacity = 10;

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
        Assert.False(txRepo.ProcessAdmissionCalled);
    }

    [Fact]
    public async Task LeaveGroupStrictAsync_WhenGroupOwnerWithoutTransferPolicy_ThrowsInvalidOperationException()
    {
        var (service, _, _, _, _, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Owners.Add((groupId, actorId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LeaveGroupStrictAsync(groupId, scope));

        Assert.Contains("ownership", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessJoinRequestStrictAsync_IncorrectCommittedResultScope_ReturnsFailedResult()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "hobbies", true, false, 22, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);

        // Foreign scope or different group ID returned in membership
        var wrongGroupMembership = new GroupMembership(Guid.NewGuid(), scope, applicantId);
        txRepo.AdmissionResult = GroupAdmissionTransactionResult.Approved(wrongGroupMembership);

        var result = await service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task StrictOperations_RepositoryFailure_PropagatesException()
    {
        var (service, _, _, _, txRepo, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);
        evidenceProvider.Profiles[applicantId] = new AuthoritativeGroupProfileEvidence(applicantId, "hobbies", true, false, 22, TestNow);
        evidenceProvider.Memberships[(groupId, actorId)] = new AuthoritativeGroupMembershipEvidence(
            groupId, actorId, true, GroupPrivilegedCapability.ApproveJoinRequest, TestNow);

        txRepo.ShouldThrow = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitJoinRequestStrictAsync(groupId, scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LeaveGroupStrictAsync(groupId, scope));
    }

    [Fact]
    public async Task StrictOperations_Cancellation_PropagatesCancellation()
    {
        var (service, _, _, _, _, evidenceProvider, actorContext) = CreateStrictService();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var groupId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();

        actorContext.CurrentActor = new AuthoritativeGroupProfileEvidence(actorId, "hobbies", true, false, 28, TestNow);
        evidenceProvider.Groups[groupId] = new AuthoritativeGroupStateEvidence(groupId, scope, true, true, TestNow);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.SubmitJoinRequestStrictAsync(groupId, scope, cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.CancelJoinRequestStrictAsync(groupId, scope, cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ProcessJoinRequestStrictAsync(groupId, scope, applicantId, approve: true, cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.LeaveGroupStrictAsync(groupId, scope, cts.Token));
    }
}
