using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupMembershipScopedLookupServiceTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private sealed class TestGroupMembershipRepositoryDouble : IGroupMembershipRepository
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

    #region 1. Constructor and Parameter Validation

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupMembershipScopedLookupService(null!));
    }

    [Fact]
    public async Task LookupMembershipAsync_EmptyGroupId_ReturnsRejectedInvalidIdentity()
    {
        var repo = new TestGroupMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(Guid.Empty, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public async Task LookupMembershipAsync_EmptyMemberProfileId_ReturnsRejectedInvalidIdentity()
    {
        var repo = new TestGroupMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, Guid.Empty);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public async Task LookupMembershipAsync_NullScope_ReturnsRejectedNullInput()
    {
        var repo = new TestGroupMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, null!, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullInput, result.Status);
    }

    #endregion

    #region 2. Valid Lookups (Found and NotFound)

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task LookupMembershipAsync_MatchingMembership_ReturnsFound(string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var expectedMembership = new GroupMembership(GroupId1, scope, ProfileId1);

        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(expectedMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, scope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.NotNull(result.Membership);
        Assert.Equal(expectedMembership, result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);

        var hasActive = await service.HasActiveMembershipAsync(GroupId1, scope, ProfileId1);
        Assert.True(hasActive);
    }

    [Fact]
    public async Task LookupMembershipAsync_MissingMembership_ReturnsNotFoundValid()
    {
        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(null)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, result.Status);

        var hasActive = await service.HasActiveMembershipAsync(GroupId1, LearningScope, ProfileId1);
        Assert.False(hasActive);
    }

    #endregion

    #region 3. Scope Boundary and Malformed Repository Records

    [Fact]
    public async Task LookupMembershipAsync_RepositoryReturnsForeignGroupId_ReturnsRejectedGroupIdMismatch()
    {
        var foreignMembership = new GroupMembership(GroupId2, LearningScope, ProfileId1);

        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(foreignMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.GroupIdMismatch, result.Status);

        var hasActive = await service.HasActiveMembershipAsync(GroupId1, LearningScope, ProfileId1);
        Assert.False(hasActive);
    }

    [Fact]
    public async Task LookupMembershipAsync_RepositoryReturnsForeignProfileId_ReturnsRejectedProfileIdMismatch()
    {
        var foreignMembership = new GroupMembership(GroupId1, LearningScope, ProfileId2);

        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(foreignMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.MemberProfileIdMismatch, result.Status);
    }

    [Fact]
    public async Task LookupMembershipAsync_RepositoryReturnsCrossModuleScope_ReturnsRejectedScopeMismatch()
    {
        var crossScopeMembership = new GroupMembership(GroupId1, HobbiesScope, ProfileId1);

        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => Task.FromResult<GroupMembership?>(crossScopeMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.ScopeMismatch, result.Status);
    }

    #endregion

    #region 4. Cancellation and Dependency Failures

    [Fact]
    public async Task LookupMembershipAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var repo = new TestGroupMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1, cts.Token));
    }

    [Fact]
    public async Task LookupMembershipAsync_RepositoryFailure_PropagatesException()
    {
        var repo = new TestGroupMembershipRepositoryDouble
        {
            GetMembershipHandler = (_, _, _, _) => throw new InvalidOperationException("Storage failure")
        };
        var service = new GroupMembershipScopedLookupService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LookupMembershipAsync(GroupId1, LearningScope, ProfileId1));
    }

    #endregion

    #region 5. Privacy Safeguards and Equality

    [Fact]
    public void Diagnostic_ToString_HidesSensitiveData()
    {
        var validation = GroupMembershipReadScopeValidation.Success();
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);
        var result = GroupMembershipLookupResult.Found(membership, validation);

        var text = result.ToString();
        Assert.Contains("GroupMembershipLookupResult", text);
        Assert.Contains("Status=Valid", text);
        Assert.DoesNotContain(GroupId1.ToString(), text);
        Assert.DoesNotContain(ProfileId1.ToString(), text);
    }

    [Fact]
    public void EqualityAndHashCode_BehaviorIsDeterministic()
    {
        var validation = GroupMembershipReadScopeValidation.Success();
        var membership = new GroupMembership(GroupId1, LearningScope, ProfileId1);

        var res1 = GroupMembershipLookupResult.Found(membership, validation);
        var res2 = GroupMembershipLookupResult.Found(membership, validation);
        var notFound = GroupMembershipLookupResult.NotFound(GroupMembershipReadScopeValidation.None());

        Assert.Equal(res1, res2);
        Assert.Equal(res1.GetHashCode(), res2.GetHashCode());
        Assert.NotEqual(res1, notFound);
    }

    #endregion
}
