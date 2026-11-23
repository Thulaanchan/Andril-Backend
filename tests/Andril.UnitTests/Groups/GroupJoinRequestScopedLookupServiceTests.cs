using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupJoinRequestScopedLookupServiceTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime RequestedAtUtc = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private sealed class TestGroupJoinRequestRepositoryDouble : IGroupJoinRequestRepository
    {
        public int CallCount { get; private set; }
        public Func<Guid, GroupModuleScope, Guid, CancellationToken, Task<GroupJoinRequest?>>? GetHandler { get; set; }

        public Task<GroupJoinRequest?> GetJoinRequestAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return GetHandler is not null
                ? GetHandler(groupId, scope, requestingProfileId, cancellationToken)
                : Task.FromResult<GroupJoinRequest?>(null);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveJoinRequestAsync(GroupJoinRequest joinRequest, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RemoveJoinRequestAsync(Guid groupId, GroupModuleScope scope, Guid requestingProfileId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetJoinRequestCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    #region 1. Constructor and Parameter Validation

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupJoinRequestScopedLookupService(null!));
    }

    [Fact]
    public async Task LookupJoinRequestAsync_EmptyGroupId_ReturnsInvalidIdentityWithoutCallingRepository()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(Guid.Empty, LearningScope, ProfileId1);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_EmptyRequestingProfileId_ReturnsInvalidIdentityWithoutCallingRepository()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, LearningScope, Guid.Empty);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_NullScope_ReturnsNullInputWithoutCallingRepository()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, null!, ProfileId1);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.NullInput, result.Status);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_NullGroupEntry_ReturnsNullInputWithoutCallingRepository()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync((GroupEntry)null!, ProfileId1);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.NullInput, result.Status);
    }

    #endregion

    #region 2. Valid Scoped Lookups and Missing Requests

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task LookupJoinRequestAsync_MatchingRecordInApprovedScope_ReturnsFound(string module, GroupKind kind)
    {
        var scope = new GroupModuleScope(module, kind);
        var expected = new GroupJoinRequest(GroupId1, scope, ProfileId1, RequestedAtUtc);
        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (_, _, _, _) => Task.FromResult<GroupJoinRequest?>(expected)
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, scope, ProfileId1);
        var hasRequest = await service.HasJoinRequestAsync(GroupId1, scope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.True(result.HasJoinRequest);
        Assert.True(hasRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.Found, result.Status);
        Assert.Equal(expected, result.JoinRequest);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_GroupEntryOverload_DelegatesWithExactGroupScope()
    {
        var group = new GroupEntry(GroupId1, HobbiesScope, "Chess Club");
        var expected = new GroupJoinRequest(GroupId1, HobbiesScope, ProfileId1, RequestedAtUtc);
        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (gid, scope, pid, _) =>
            {
                Assert.Equal(GroupId1, gid);
                Assert.Equal(HobbiesScope, scope);
                Assert.Equal(ProfileId1, pid);
                return Task.FromResult<GroupJoinRequest?>(expected);
            }
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(group, ProfileId1);

        Assert.True(result.IsValid);
        Assert.True(result.HasJoinRequest);
        Assert.Equal(expected, result.JoinRequest);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_MissingRecord_ReturnsNotFoundWithoutMembershipOrError()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, SocialScope, ProfileId1);
        var hasRequest = await service.HasJoinRequestAsync(GroupId1, SocialScope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.False(hasRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.NotFound, result.Status);
    }

    #endregion

    #region 3. Fail-Closed Scope and Identity Violation Rejection

    [Fact]
    public async Task LookupJoinRequestAsync_ForeignGroupReturned_RejectsWithGroupIdMismatch()
    {
        var foreignGroupRequest = new GroupJoinRequest(GroupId2, LearningScope, ProfileId1, RequestedAtUtc);
        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (_, _, _, _) => Task.FromResult<GroupJoinRequest?>(foreignGroupRequest)
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.GroupIdMismatch, result.Status);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_WrongRequesterReturned_RejectsWithRequestingProfileIdMismatch()
    {
        var foreignRequester = new GroupJoinRequest(GroupId1, LearningScope, ProfileId2, RequestedAtUtc);
        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (_, _, _, _) => Task.FromResult<GroupJoinRequest?>(foreignRequester)
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.RequestingProfileIdMismatch, result.Status);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "hobbies", GroupKind.Club)]
    [InlineData("learning", GroupKind.Classroom, "social", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Club, "social", GroupKind.Zone)]
    [InlineData("social", GroupKind.Zone, "learning", GroupKind.Classroom)]
    public async Task LookupJoinRequestAsync_CrossModuleOrWrongGroupKindReturned_RejectsWithModuleMismatch(
        string requestedModule,
        GroupKind requestedKind,
        string returnedModule,
        GroupKind returnedKind)
    {
        var requestedScope = new GroupModuleScope(requestedModule, requestedKind);
        var returnedScope = new GroupModuleScope(returnedModule, returnedKind);
        var mismatchedRequest = new GroupJoinRequest(GroupId1, returnedScope, ProfileId1, RequestedAtUtc);

        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (_, _, _, _) => Task.FromResult<GroupJoinRequest?>(mismatchedRequest)
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        var result = await service.LookupJoinRequestAsync(GroupId1, requestedScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasJoinRequest);
        Assert.Null(result.JoinRequest);
        Assert.Equal(GroupJoinRequestLookupStatus.ModuleMismatch, result.Status);
    }

    #endregion

    #region 4. Cancellation and Repository Failure Safety

    [Fact]
    public async Task LookupJoinRequestAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble();
        var service = new GroupJoinRequestScopedLookupService(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LookupJoinRequestAsync(GroupId1, LearningScope, ProfileId1, cts.Token));
        Assert.Equal(0, repo.CallCount);
    }

    [Fact]
    public async Task LookupJoinRequestAsync_RepositoryFailure_PropagatesExceptionWithoutAdmission()
    {
        var repo = new TestGroupJoinRequestRepositoryDouble
        {
            GetHandler = (_, _, _, _) => throw new InvalidOperationException("Simulated persistence failure")
        };
        var service = new GroupJoinRequestScopedLookupService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LookupJoinRequestAsync(GroupId1, LearningScope, ProfileId1));
    }

    #endregion

    #region 5. Privacy-Safe Diagnostics, Serialization, and Non-Authorization Invariants

    [Fact]
    public void Result_SerializationAndDiagnostics_ConcealPrivateState()
    {
        var request = new GroupJoinRequest(GroupId1, LearningScope, ProfileId1, RequestedAtUtc);
        var found = GroupJoinRequestLookupResult.Found(request);
        var notFound = GroupJoinRequestLookupResult.NotFound();
        var rejected = GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.ModuleMismatch);

        foreach (var result in new[] { found, notFound, rejected })
        {
            Assert.Equal("{}", JsonSerializer.Serialize(result));
            var diagnostic = result.ToString();
            Assert.Equal("GroupJoinRequestLookupResult [private]", diagnostic);
            Assert.DoesNotContain(GroupId1.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileId1.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Result_NoImplicitMembershipOrRoleGrantMembers_AndDeterministicEquality()
    {
        var propertyNames = typeof(GroupJoinRequestLookupResult)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Member", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Role", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Approve", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase));

        var request = new GroupJoinRequest(GroupId1, LearningScope, ProfileId1, RequestedAtUtc);
        var r1 = GroupJoinRequestLookupResult.Found(request);
        var r2 = GroupJoinRequestLookupResult.Found(request);
        var r3 = GroupJoinRequestLookupResult.NotFound();

        Assert.Equal(r1, r2);
        Assert.Equal(r1.GetHashCode(), r2.GetHashCode());
        Assert.NotEqual(r1, r3);

        Assert.Throws<ArgumentException>(() => GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.Found));
        Assert.Throws<ArgumentException>(() => GroupJoinRequestLookupResult.Rejected(GroupJoinRequestLookupStatus.NotFound));
    }

    #endregion
}
