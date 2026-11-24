using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class HobbiesClubAdmissionPreflightServiceTests
{
    private static readonly GroupModuleScope HobbiesClubScope = new("hobbies", GroupKind.Club);
    private static readonly GroupModuleScope LearningClassroomScope = new("learning", GroupKind.Classroom);

    private sealed class FakeGroupEntryRepository : IGroupEntryRepository
    {
        public Func<Guid, GroupModuleScope, CancellationToken, Task<GroupEntry?>>? OnGetGroup { get; set; }

        public Task<GroupEntry?> GetGroupAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            OnGetGroup is not null
                ? OnGetGroup(groupId, scope, cancellationToken)
                : Task.FromResult<GroupEntry?>(null);

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeGroupMembershipRepository : IGroupMembershipRepository
    {
        public Func<Guid, GroupModuleScope, Guid, CancellationToken, Task<GroupMembership?>>? OnGetMembership { get; set; }
        public Func<Guid, GroupModuleScope, CancellationToken, Task<int>>? OnGetMemberCount { get; set; }
        public bool SaveMembershipCalled { get; private set; }

        public Task<GroupMembership?> GetMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            OnGetMembership is not null
                ? OnGetMembership(groupId, scope, memberProfileId, cancellationToken)
                : Task.FromResult<GroupMembership?>(null);

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default)
        {
            SaveMembershipCalled = true;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            OnGetMemberCount is not null
                ? OnGetMemberCount(groupId, scope, cancellationToken)
                : Task.FromResult(0);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_ValidClub_BelowLimit_ReturnsEligible()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Photography Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(null),
            OnGetMemberCount = (gid, s, ct) => Task.FromResult(25)
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.True(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.Eligible, result.Status);
        Assert.Equal(25, result.CurrentMemberCount);
        Assert.Equal(50, result.MaxCapacity);
        Assert.False(memberRepo.SaveMembershipCalled);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_AlreadyMember_ReturnsAlreadyMember()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Photography Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(
                new GroupMembership(gid, s, pid))
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.AlreadyMember, result.Status);
        Assert.False(memberRepo.SaveMembershipCalled);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_CountAtCapacity_ReturnsCapacityReached()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Chess Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(null),
            OnGetMemberCount = (gid, s, ct) => Task.FromResult(50)
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.CapacityReached, result.Status);
        Assert.Equal(50, result.CurrentMemberCount);
        Assert.Equal(50, result.MaxCapacity);
        Assert.False(memberRepo.SaveMembershipCalled);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_PremiumDowngrade_Above50_BlocksJoins()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Book Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(null),
            OnGetMemberCount = (gid, s, ct) => Task.FromResult(75)
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.ExpiredPremium);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.CapacityReached, result.Status);
        Assert.Equal(50, result.MaxCapacity);
        Assert.False(memberRepo.SaveMembershipCalled);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_MissingGroup_ReturnsMissingGroup()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(null)
        };
        var memberRepo = new FakeGroupMembershipRepository();

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.MissingGroup, result.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_WrongScopeOrKind_ReturnsInvalidIdentityOrScope()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository();
        var memberRepo = new FakeGroupMembershipRepository();

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var classroomResult = await service.PreflightAdmissionAsync(
            groupId, LearningClassroomScope, profileId, HobbiesClubEntitlementTier.Free);

        var nullScopeResult = await service.PreflightAdmissionAsync(
            groupId, null, profileId, HobbiesClubEntitlementTier.Free);

        var emptyGroupResult = await service.PreflightAdmissionAsync(
            Guid.Empty, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        var emptyProfileResult = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, Guid.Empty, HobbiesClubEntitlementTier.Free);

        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope, classroomResult.Status);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope, nullScopeResult.Status);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope, emptyGroupResult.Status);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InvalidIdentityOrScope, emptyProfileResult.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_MissingEntitlement_ReturnsMissingOrInvalidEntitlement()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository();
        var memberRepo = new FakeGroupMembershipRepository();

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var unknownResult = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Unknown);

        var invalidEnumResult = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, (HobbiesClubEntitlementTier)999);

        Assert.Equal(HobbiesClubAdmissionPreflightStatus.MissingOrInvalidEntitlement, unknownResult.Status);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.MissingOrInvalidEntitlement, invalidEnumResult.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_NegativeCount_ReturnsInconsistentEvidence()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Hiking Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(null),
            OnGetMemberCount = (gid, s, ct) => Task.FromResult(-5)
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InconsistentEvidence, result.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_ForeignMembershipRecord_ReturnsInconsistentEvidence()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => Task.FromResult<GroupEntry?>(new GroupEntry(id, s, "Club"))
        };
        var memberRepo = new FakeGroupMembershipRepository
        {
            OnGetMembership = (gid, s, pid, ct) => Task.FromResult<GroupMembership?>(
                new GroupMembership(gid, s, foreignProfileId))
        };

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.InconsistentEvidence, result.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_RepositoryException_ReturnsDependencyFailure()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository
        {
            OnGetGroup = (id, s, ct) => throw new InvalidOperationException("DB timeout")
        };
        var memberRepo = new FakeGroupMembershipRepository();

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        var result = await service.PreflightAdmissionAsync(
            groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free);

        Assert.False(result.IsEligible);
        Assert.Equal(HobbiesClubAdmissionPreflightStatus.DependencyFailure, result.Status);
    }

    [Fact]
    public async Task PreflightAdmissionAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var groupId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var groupRepo = new FakeGroupEntryRepository();
        var memberRepo = new FakeGroupMembershipRepository();

        var service = new HobbiesClubAdmissionPreflightService(groupRepo, memberRepo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.PreflightAdmissionAsync(groupId, HobbiesClubScope, profileId, HobbiesClubEntitlementTier.Free, cts.Token));
    }

    [Fact]
    public void PreflightResult_Privacy_ConcealsInternalDetails()
    {
        var result = HobbiesClubAdmissionPreflightResult.Create(
            HobbiesClubAdmissionPreflightStatus.Eligible,
            "Diagnostics",
            20,
            50);

        var json = JsonSerializer.Serialize(result);
        Assert.Equal("{}", json);

        var toString = result.ToString();
        Assert.DoesNotContain("20", toString);
        Assert.DoesNotContain("50", toString);
        Assert.Contains("Eligible", toString);
    }
}
