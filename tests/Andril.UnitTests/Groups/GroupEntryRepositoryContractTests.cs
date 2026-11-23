using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupEntryRepositoryContractTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    #region A. Interface Contract Shape Tests

    [Fact]
    public void Interface_IsInterfaceType()
    {
        Assert.True(typeof(IGroupEntryRepository).IsInterface);
    }

    [Fact]
    public void Interface_DeclaresExactlyTwoMethods()
    {
        var methods = typeof(IGroupEntryRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(2, methods.Length);
        Assert.Contains(methods, m => m.Name == "GetGroupAsync");
        Assert.Contains(methods, m => m.Name == "ExistsAsync");
    }

    [Fact]
    public void Interface_AllMethods_AcceptOptionalCancellationToken()
    {
        var methods = typeof(IGroupEntryRepository)
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
        var methods = typeof(IGroupEntryRepository)
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
    public void Interface_DoesNotExposeMutationMethods()
    {
        var methods = typeof(IGroupEntryRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var mutationNames = new[] { "Save", "Add", "Update", "Delete", "Remove", "Create", "Set" };

        foreach (var method in methods)
        {
            foreach (var mutation in mutationNames)
            {
                Assert.DoesNotContain(mutation, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interface_DoesNotExposeAuthenticationOrCredentialParameters()
    {
        var methods = typeof(IGroupEntryRepository)
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

    private sealed class InMemoryGroupEntryRepositoryFake : IGroupEntryRepository
    {
        private readonly Dictionary<(Guid GroupId, GroupModuleScope Scope), GroupEntry> _storage = new();

        public void Add(GroupEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _storage[(entry.Id, entry.Scope)] = entry;
        }

        public Task<GroupEntry?> GetGroupAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null)
            {
                return Task.FromResult<GroupEntry?>(null);
            }

            _storage.TryGetValue((groupId, scope), out var entry);
            return Task.FromResult(entry);
        }

        public Task<bool> ExistsAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (groupId == Guid.Empty || scope is null)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_storage.ContainsKey((groupId, scope)));
        }
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task GetGroupAsync_MatchingIdAndScope_ReturnsGroup(string module, GroupKind groupKind)
    {
        var fake = new InMemoryGroupEntryRepositoryFake();
        var scope = new GroupModuleScope(module, groupKind);
        var entry = new GroupEntry(GroupId1, scope, "Scoped Test Group");
        fake.Add(entry);

        var result = await fake.GetGroupAsync(GroupId1, scope);

        Assert.NotNull(result);
        Assert.Equal(GroupId1, result.Id);
        Assert.Equal(scope, result.Scope);
        Assert.Equal("Scoped Test Group", result.Name);

        var exists = await fake.ExistsAsync(GroupId1, scope);
        Assert.True(exists);
    }

    [Fact]
    public async Task GetGroupAsync_DifferentGroupId_ReturnsNull()
    {
        var fake = new InMemoryGroupEntryRepositoryFake();
        var entry = new GroupEntry(GroupId1, LearningScope, "Learning Classroom");
        fake.Add(entry);

        var result = await fake.GetGroupAsync(GroupId2, LearningScope);

        Assert.Null(result);

        var exists = await fake.ExistsAsync(GroupId2, LearningScope);
        Assert.False(exists);
    }

    [Fact]
    public async Task GetGroupAsync_MismatchedScope_ReturnsNull()
    {
        var fake = new InMemoryGroupEntryRepositoryFake();
        var entry = new GroupEntry(GroupId1, LearningScope, "Learning Classroom");
        fake.Add(entry);

        var result = await fake.GetGroupAsync(GroupId1, HobbiesScope);

        Assert.Null(result);

        var exists = await fake.ExistsAsync(GroupId1, HobbiesScope);
        Assert.False(exists);
    }

    [Fact]
    public async Task GetGroupAsync_EmptyGroupIdOrNullScope_ReturnsNull()
    {
        var fake = new InMemoryGroupEntryRepositoryFake();
        var entry = new GroupEntry(GroupId1, LearningScope, "Learning Classroom");
        fake.Add(entry);

        Assert.Null(await fake.GetGroupAsync(Guid.Empty, LearningScope));
        Assert.Null(await fake.GetGroupAsync(GroupId1, null!));

        Assert.False(await fake.ExistsAsync(Guid.Empty, LearningScope));
        Assert.False(await fake.ExistsAsync(GroupId1, null!));
    }

    [Fact]
    public async Task Operations_ObserveCancellation()
    {
        var fake = new InMemoryGroupEntryRepositoryFake();
        var entry = new GroupEntry(GroupId1, LearningScope, "Learning Classroom");
        fake.Add(entry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.GetGroupAsync(GroupId1, LearningScope, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.ExistsAsync(GroupId1, LearningScope, cts.Token));
    }

    #endregion
}
