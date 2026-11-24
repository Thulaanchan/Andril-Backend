using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupMembershipRepositoryContractTests
{
    private static readonly Guid ValidGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherGroupId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ValidProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherProfileId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    #region A. Interface Contract Shape Tests

    [Fact]
    public void Interface_IsInterfaceType()
    {
        Assert.True(typeof(IGroupMembershipRepository).IsInterface);
    }

    [Fact]
    public void Interface_DeclaresExactlyFiveMethods()
    {
        var methods = typeof(IGroupMembershipRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(5, methods.Length);
        Assert.Contains(methods, m => m.Name == "GetMembershipAsync");
        Assert.Contains(methods, m => m.Name == "ExistsAsync");
        Assert.Contains(methods, m => m.Name == "SaveMembershipAsync");
        Assert.Contains(methods, m => m.Name == "RemoveMembershipAsync");
        Assert.Contains(methods, m => m.Name == "GetMemberCountAsync");
    }

    [Fact]
    public void Interface_AllMethods_AcceptOptionalCancellationToken()
    {
        var methods = typeof(IGroupMembershipRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var lastParam = method.GetParameters().LastOrDefault();
            Assert.NotNull(lastParam);
            Assert.Equal(typeof(CancellationToken), lastParam.ParameterType);
            Assert.True(lastParam.IsOptional);
        }
    }

    [Fact]
    public void Interface_DoesNotExposeProhibitedBulkOrUnscopedMethods()
    {
        var methods = typeof(IGroupMembershipRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "GetAll", "ListAll", "FindAll", "QueryAll", "EnumerateAll", "FetchAll" };

        foreach (var method in methods)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interface_DoesNotExposeAuthenticationOrCredentialParameters()
    {
        var methods = typeof(IGroupMembershipRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var prohibitedTypes = new[] { "ClaimsPrincipal", "Token", "Credential", "Session", "Password", "Auth" };

        foreach (var method in methods)
        {
            foreach (var param in method.GetParameters())
            {
                if (param.ParameterType == typeof(CancellationToken)) continue;

                foreach (var prohibited in prohibitedTypes)
                {
                    Assert.DoesNotContain(prohibited, param.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(prohibited, param.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    #endregion

    #region B. In-Memory Test Fake Semantics Tests

    [Fact]
    public async Task SaveMembershipAsync_And_GetMembershipAsync_RoundTripSuccessfully()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        await repo.SaveMembershipAsync(membership);
        var retrieved = await repo.GetMembershipAsync(ValidGroupId, scope, ValidProfileId);

        Assert.NotNull(retrieved);
        Assert.Equal(membership, retrieved);
    }

    [Fact]
    public async Task GetMembershipAsync_UnknownProfile_ReturnsNull()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        await repo.SaveMembershipAsync(membership);
        var result = await repo.GetMembershipAsync(ValidGroupId, scope, OtherProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMembershipAsync_DifferentGroup_ReturnsNull()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        await repo.SaveMembershipAsync(membership);
        var result = await repo.GetMembershipAsync(OtherGroupId, scope, ValidProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMembershipAsync_MismatchedScope_ReturnsNull()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var learningScope = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbiesScope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership = new GroupMembership(ValidGroupId, learningScope, ValidProfileId);

        await repo.SaveMembershipAsync(membership);
        var result = await repo.GetMembershipAsync(ValidGroupId, hobbiesScope, ValidProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExistsAsync_ReflectsStorageState()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        Assert.False(await repo.ExistsAsync(ValidGroupId, scope, ValidProfileId));

        await repo.SaveMembershipAsync(membership);

        Assert.True(await repo.ExistsAsync(ValidGroupId, scope, ValidProfileId));
        Assert.False(await repo.ExistsAsync(ValidGroupId, scope, OtherProfileId));
        Assert.False(await repo.ExistsAsync(OtherGroupId, scope, ValidProfileId));
    }

    [Fact]
    public async Task RemoveMembershipAsync_ExistingMembership_RemovesAndReturnsTrue()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        await repo.SaveMembershipAsync(membership);
        Assert.Equal(1, await repo.GetMemberCountAsync(ValidGroupId, scope));

        var removed = await repo.RemoveMembershipAsync(ValidGroupId, scope, ValidProfileId);
        Assert.True(removed);
        Assert.Equal(0, await repo.GetMemberCountAsync(ValidGroupId, scope));
        Assert.Null(await repo.GetMembershipAsync(ValidGroupId, scope, ValidProfileId));

        var removedAgain = await repo.RemoveMembershipAsync(ValidGroupId, scope, ValidProfileId);
        Assert.False(removedAgain);
    }

    [Fact]
    public async Task GetMemberCountAsync_ReturnsExactCountForGroupAndScope()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var otherScope = new GroupModuleScope("social", GroupKind.Zone);

        var m1 = new GroupMembership(ValidGroupId, scope, ValidProfileId);
        var m2 = new GroupMembership(ValidGroupId, scope, OtherProfileId);
        var m3OtherGroup = new GroupMembership(OtherGroupId, scope, ValidProfileId);
        var m4OtherScope = new GroupMembership(ValidGroupId, otherScope, ValidProfileId);

        await repo.SaveMembershipAsync(m1);
        await repo.SaveMembershipAsync(m2);
        await repo.SaveMembershipAsync(m3OtherGroup);
        await repo.SaveMembershipAsync(m4OtherScope);

        Assert.Equal(2, await repo.GetMemberCountAsync(ValidGroupId, scope));
        Assert.Equal(1, await repo.GetMemberCountAsync(OtherGroupId, scope));
        Assert.Equal(1, await repo.GetMemberCountAsync(ValidGroupId, otherScope));
    }

    [Fact]
    public async Task SaveMembershipAsync_NullMembership_ThrowsArgumentNullException()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        await Assert.ThrowsAsync<ArgumentNullException>(() => repo.SaveMembershipAsync(null!));
    }

    #endregion

    #region C. Cancellation Tests

    [Fact]
    public async Task Operations_WithCancelledToken_ThrowOperationCanceledException()
    {
        var repo = new InMemoryGroupMembershipRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var membership = new GroupMembership(ValidGroupId, scope, ValidProfileId);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetMembershipAsync(ValidGroupId, scope, ValidProfileId, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.ExistsAsync(ValidGroupId, scope, ValidProfileId, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.SaveMembershipAsync(membership, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.RemoveMembershipAsync(ValidGroupId, scope, ValidProfileId, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repo.GetMemberCountAsync(ValidGroupId, scope, cts.Token));
    }

    #endregion

    #region Nested Test-Only Fake

    private sealed class InMemoryGroupMembershipRepositoryFake : IGroupMembershipRepository
    {
        private readonly Dictionary<(Guid GroupId, GroupModuleScope Scope, Guid MemberProfileId), GroupMembership> _memberships = new();

        public Task<GroupMembership?> GetMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || memberProfileId == Guid.Empty)
            {
                return Task.FromResult<GroupMembership?>(null);
            }

            _memberships.TryGetValue((groupId, scope, memberProfileId), out var membership);
            return Task.FromResult(membership);
        }

        public Task<bool> ExistsAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || memberProfileId == Guid.Empty)
            {
                return Task.FromResult(false);
            }

            var exists = _memberships.ContainsKey((groupId, scope, memberProfileId));
            return Task.FromResult(exists);
        }

        public Task SaveMembershipAsync(
            GroupMembership membership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(membership);

            _memberships[(membership.GroupId, membership.Scope, membership.MemberProfileId)] = membership;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || memberProfileId == Guid.Empty)
            {
                return Task.FromResult(false);
            }

            var removed = _memberships.Remove((groupId, scope, memberProfileId));
            return Task.FromResult(removed);
        }

        public Task<int> GetMemberCountAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null)
            {
                return Task.FromResult(0);
            }

            var count = _memberships.Keys.Count(k => k.GroupId == groupId && k.Scope.Equals(scope));
            return Task.FromResult(count);
        }
    }

    #endregion
}
