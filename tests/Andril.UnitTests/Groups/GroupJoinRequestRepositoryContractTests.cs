using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupJoinRequestRepositoryContractTests
{
    private static readonly Guid ValidGroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherGroupId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ValidProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherProfileId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime RequestTimestamp = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    #region A. Interface Contract Shape Tests

    [Fact]
    public void Interface_IsInterfaceType()
    {
        Assert.True(typeof(IGroupJoinRequestRepository).IsInterface);
    }

    [Fact]
    public void Interface_DeclaresExactlyFiveMethods()
    {
        var methods = typeof(IGroupJoinRequestRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(5, methods.Length);
        Assert.Contains(methods, m => m.Name == "GetJoinRequestAsync");
        Assert.Contains(methods, m => m.Name == "ExistsAsync");
        Assert.Contains(methods, m => m.Name == "SaveJoinRequestAsync");
        Assert.Contains(methods, m => m.Name == "RemoveJoinRequestAsync");
        Assert.Contains(methods, m => m.Name == "GetJoinRequestCountAsync");
    }

    [Fact]
    public void Interface_AllMethods_AcceptOptionalCancellationToken()
    {
        var methods = typeof(IGroupJoinRequestRepository)
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
        var methods = typeof(IGroupJoinRequestRepository)
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
        var methods = typeof(IGroupJoinRequestRepository)
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

    #region B. In-Memory Fake Implementation Behavior Tests

    private sealed class InMemoryGroupJoinRequestRepositoryFake : IGroupJoinRequestRepository
    {
        private readonly Dictionary<(Guid GroupId, GroupModuleScope Scope, Guid RequestingProfileId), GroupJoinRequest> _storage = new();

        public Task<GroupJoinRequest?> GetJoinRequestAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || requestingProfileId == Guid.Empty)
            {
                return Task.FromResult<GroupJoinRequest?>(null);
            }

            _storage.TryGetValue((groupId, scope, requestingProfileId), out var request);
            return Task.FromResult(request);
        }

        public Task<bool> ExistsAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || requestingProfileId == Guid.Empty)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_storage.ContainsKey((groupId, scope, requestingProfileId)));
        }

        public Task SaveJoinRequestAsync(
            GroupJoinRequest joinRequest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(joinRequest);

            _storage[(joinRequest.GroupId, joinRequest.Scope, joinRequest.RequestingProfileId)] = joinRequest;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveJoinRequestAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid requestingProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null || requestingProfileId == Guid.Empty)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_storage.Remove((groupId, scope, requestingProfileId)));
        }

        public Task<int> GetJoinRequestCountAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null)
            {
                return Task.FromResult(0);
            }

            var count = _storage.Keys.Count(k => k.GroupId == groupId && k.Scope.Equals(scope));
            return Task.FromResult(count);
        }
    }

    [Fact]
    public async Task SaveAndGet_ExactScope_ReturnsExpectedJoinRequest()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(request);

        var retrieved = await repo.GetJoinRequestAsync(ValidGroupId, scope, ValidProfileId);

        Assert.NotNull(retrieved);
        Assert.Equal(request, retrieved);
        Assert.Equal(ValidGroupId, retrieved.GroupId);
        Assert.Equal(scope, retrieved.Scope);
        Assert.Equal(ValidProfileId, retrieved.RequestingProfileId);
    }

    [Fact]
    public async Task GetJoinRequestAsync_DifferentGroup_ReturnsNull()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(request);

        var result = await repo.GetJoinRequestAsync(OtherGroupId, scope, ValidProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetJoinRequestAsync_DifferentRequester_ReturnsNull()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(request);

        var result = await repo.GetJoinRequestAsync(ValidGroupId, scope, OtherProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetJoinRequestAsync_MismatchedScope_ReturnsNull()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var learningScope = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbiesScope = new GroupModuleScope("hobbies", GroupKind.Club);
        var request = new GroupJoinRequest(ValidGroupId, learningScope, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(request);

        var result = await repo.GetJoinRequestAsync(ValidGroupId, hobbiesScope, ValidProfileId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ExistsAsync_ReflectsStorageState()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        Assert.False(await repo.ExistsAsync(ValidGroupId, scope, ValidProfileId));

        await repo.SaveJoinRequestAsync(request);

        Assert.True(await repo.ExistsAsync(ValidGroupId, scope, ValidProfileId));
        Assert.False(await repo.ExistsAsync(ValidGroupId, scope, OtherProfileId));
        Assert.False(await repo.ExistsAsync(OtherGroupId, scope, ValidProfileId));
    }

    [Fact]
    public async Task RemoveJoinRequestAsync_Existing_RemovesAndReturnsTrue()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(request);

        var removed = await repo.RemoveJoinRequestAsync(ValidGroupId, scope, ValidProfileId);

        Assert.True(removed);
        Assert.False(await repo.ExistsAsync(ValidGroupId, scope, ValidProfileId));
        Assert.Null(await repo.GetJoinRequestAsync(ValidGroupId, scope, ValidProfileId));
    }

    [Fact]
    public async Task RemoveJoinRequestAsync_Nonexistent_ReturnsFalse()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var removed = await repo.RemoveJoinRequestAsync(ValidGroupId, scope, ValidProfileId);

        Assert.False(removed);
    }

    [Fact]
    public async Task GetJoinRequestCountAsync_CountsOnlyMatchingGroupAndScope()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope1 = new GroupModuleScope("learning", GroupKind.Classroom);
        var scope2 = new GroupModuleScope("hobbies", GroupKind.Club);

        var req1 = new GroupJoinRequest(ValidGroupId, scope1, ValidProfileId, RequestTimestamp);
        var req2 = new GroupJoinRequest(ValidGroupId, scope1, OtherProfileId, RequestTimestamp);
        var req3 = new GroupJoinRequest(OtherGroupId, scope1, ValidProfileId, RequestTimestamp);
        var req4 = new GroupJoinRequest(ValidGroupId, scope2, ValidProfileId, RequestTimestamp);

        await repo.SaveJoinRequestAsync(req1);
        await repo.SaveJoinRequestAsync(req2);
        await repo.SaveJoinRequestAsync(req3);
        await repo.SaveJoinRequestAsync(req4);

        Assert.Equal(2, await repo.GetJoinRequestCountAsync(ValidGroupId, scope1));
        Assert.Equal(1, await repo.GetJoinRequestCountAsync(OtherGroupId, scope1));
        Assert.Equal(1, await repo.GetJoinRequestCountAsync(ValidGroupId, scope2));
    }

    [Fact]
    public async Task SaveJoinRequestAsync_NullRequest_ThrowsArgumentNullException()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        await Assert.ThrowsAsync<ArgumentNullException>(() => repo.SaveJoinRequestAsync(null!));
    }

    [Fact]
    public async Task Cancellation_PropagatesAcrossAllMethods()
    {
        var repo = new InMemoryGroupJoinRequestRepositoryFake();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, RequestTimestamp);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetJoinRequestAsync(ValidGroupId, scope, ValidProfileId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ExistsAsync(ValidGroupId, scope, ValidProfileId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.SaveJoinRequestAsync(request, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.RemoveJoinRequestAsync(ValidGroupId, scope, ValidProfileId, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.GetJoinRequestCountAsync(ValidGroupId, scope, cts.Token));
    }

    #endregion
}
