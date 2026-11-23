using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupEntryScopedLookupServiceTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private sealed class TestGroupEntryRepositoryDouble : IGroupEntryRepository
    {
        public int CallCount { get; private set; }
        public Func<Guid, GroupModuleScope, CancellationToken, Task<GroupEntry?>>? GetGroupHandler { get; set; }

        public Task<GroupEntry?> GetGroupAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return GetGroupHandler is not null
                ? GetGroupHandler(groupId, scope, cancellationToken)
                : Task.FromResult<GroupEntry?>(null);
        }

        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    #region 1. Constructor and Parameter Validation

    [Fact]
    public void Constructor_NullRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new GroupEntryScopedLookupService(null!));
    }

    [Fact]
    public async Task LookupGroupAsync_EmptyGroupId_ReturnsInvalidIdentityWithoutCallingRepository()
    {
        var repo = new TestGroupEntryRepositoryDouble();
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(Guid.Empty, LearningScope);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.InvalidIdentity, result.Status);
    }

    [Fact]
    public async Task LookupGroupAsync_NullScope_ReturnsNullInputWithoutCallingRepository()
    {
        var repo = new TestGroupEntryRepositoryDouble();
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, null!);

        Assert.Equal(0, repo.CallCount);
        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.NullInput, result.Status);
    }

    #endregion

    #region 2. Valid Scoped Lookups Across All Three Approved Modules

    [Fact]
    public async Task LookupGroupAsync_ValidLearningClassroom_ReturnsFound()
    {
        var expected = new GroupEntry(GroupId1, LearningScope, "Algorithms Classroom");
        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => Task.FromResult<GroupEntry?>(expected)
        };
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, LearningScope);
        var exists = await service.ExistsInScopeAsync(GroupId1, LearningScope);

        Assert.True(result.IsValid);
        Assert.True(result.HasGroup);
        Assert.True(exists);
        Assert.Equal(GroupEntryLookupStatus.Found, result.Status);
        Assert.Equal(expected, result.Group);
    }

    [Fact]
    public async Task LookupGroupAsync_ValidHobbiesClub_ReturnsFound()
    {
        var expected = new GroupEntry(GroupId1, HobbiesScope, "Astronomy Club");
        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => Task.FromResult<GroupEntry?>(expected)
        };
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, HobbiesScope);
        var exists = await service.ExistsInScopeAsync(GroupId1, HobbiesScope);

        Assert.True(result.IsValid);
        Assert.True(result.HasGroup);
        Assert.True(exists);
        Assert.Equal(GroupEntryLookupStatus.Found, result.Status);
        Assert.Equal(expected, result.Group);
    }

    [Fact]
    public async Task LookupGroupAsync_ValidSocialZone_ReturnsFound()
    {
        var expected = new GroupEntry(GroupId1, SocialScope, "City Center Zone");
        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => Task.FromResult<GroupEntry?>(expected)
        };
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, SocialScope);
        var exists = await service.ExistsInScopeAsync(GroupId1, SocialScope);

        Assert.True(result.IsValid);
        Assert.True(result.HasGroup);
        Assert.True(exists);
        Assert.Equal(GroupEntryLookupStatus.Found, result.Status);
        Assert.Equal(expected, result.Group);
    }

    [Fact]
    public async Task LookupGroupAsync_MissingGroup_ReturnsNonDisclosingNotFound()
    {
        var repo = new TestGroupEntryRepositoryDouble();
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, LearningScope);
        var exists = await service.ExistsInScopeAsync(GroupId1, LearningScope);

        Assert.True(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.False(exists);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.NotFound, result.Status);
    }

    #endregion

    #region 3. Foreign Group and Cross-Module Rejection

    [Fact]
    public async Task LookupGroupAsync_RepositoryReturnsUnrelatedGroupId_RejectsWithGroupIdMismatch()
    {
        var unrelated = new GroupEntry(GroupId2, LearningScope, "Unrelated Classroom");
        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => Task.FromResult<GroupEntry?>(unrelated)
        };
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, LearningScope);
        var exists = await service.ExistsInScopeAsync(GroupId1, LearningScope);

        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.False(exists);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.GroupIdMismatch, result.Status);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "hobbies", GroupKind.Club)]
    [InlineData("learning", GroupKind.Classroom, "social", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Club, "learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club, "social", GroupKind.Zone)]
    [InlineData("social", GroupKind.Zone, "learning", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Zone, "hobbies", GroupKind.Club)]
    public async Task LookupGroupAsync_RepositoryReturnsWrongModuleOrGroupKind_RejectsWithModuleMismatch(
        string requestedModule,
        GroupKind requestedKind,
        string returnedModule,
        GroupKind returnedKind)
    {
        var requestedScope = new GroupModuleScope(requestedModule, requestedKind);
        var returnedScope = new GroupModuleScope(returnedModule, returnedKind);
        var wrongScopeGroup = new GroupEntry(GroupId1, returnedScope, "Cross-Module Group");

        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => Task.FromResult<GroupEntry?>(wrongScopeGroup)
        };
        var service = new GroupEntryScopedLookupService(repo);

        var result = await service.LookupGroupAsync(GroupId1, requestedScope);

        Assert.False(result.IsValid);
        Assert.False(result.HasGroup);
        Assert.Null(result.Group);
        Assert.Equal(GroupEntryLookupStatus.ModuleMismatch, result.Status);
    }

    #endregion

    #region 4. Cancellation and Repository Failure Safety

    [Fact]
    public async Task LookupGroupAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var repo = new TestGroupEntryRepositoryDouble();
        var service = new GroupEntryScopedLookupService(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LookupGroupAsync(GroupId1, LearningScope, cts.Token));
        Assert.Equal(0, repo.CallCount);
    }

    [Fact]
    public async Task LookupGroupAsync_RepositoryException_PropagatesWithoutGrantingAccess()
    {
        var repo = new TestGroupEntryRepositoryDouble
        {
            GetGroupHandler = (_, _, _) => throw new InvalidOperationException("Simulated storage outage")
        };
        var service = new GroupEntryScopedLookupService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LookupGroupAsync(GroupId1, HobbiesScope));
    }

    #endregion

    #region 5. Private Group Non-Disclosure and Non-Authorization Invariants

    [Fact]
    public void Result_SerializationAndDiagnostics_ConcealPrivateGroupExistenceAndMetadata()
    {
        var group = new GroupEntry(GroupId1, SocialScope, "Confidential Zone");
        var found = GroupEntryLookupResult.Found(group);
        var notFound = GroupEntryLookupResult.NotFound();
        var rejected = GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.GroupIdMismatch);

        foreach (var result in new[] { found, notFound, rejected })
        {
            var json = JsonSerializer.Serialize(result);
            Assert.Equal("{}", json);
            Assert.DoesNotContain(GroupId1.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Confidential Zone", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("social", json, StringComparison.OrdinalIgnoreCase);

            var diagnostic = result.ToString();
            Assert.Equal("GroupEntryLookupResult [private]", diagnostic);
            Assert.DoesNotContain(GroupId1.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Confidential Zone", diagnostic, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Result_NoImplicitAuthorizationMembers_AndDeterministicEquality()
    {
        var propertyNames = typeof(GroupEntryLookupResult)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Member", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Role", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Grant", StringComparison.OrdinalIgnoreCase));

        var group = new GroupEntry(GroupId1, LearningScope, "Discrete Math");
        var r1 = GroupEntryLookupResult.Found(group);
        var r2 = GroupEntryLookupResult.Found(group);
        var r3 = GroupEntryLookupResult.NotFound();

        Assert.Equal(r1, r2);
        Assert.Equal(r1.GetHashCode(), r2.GetHashCode());
        Assert.NotEqual(r1, r3);

        Assert.Throws<ArgumentException>(() => GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.Found));
        Assert.Throws<ArgumentException>(() => GroupEntryLookupResult.Rejected(GroupEntryLookupStatus.NotFound));
    }

    #endregion
}
