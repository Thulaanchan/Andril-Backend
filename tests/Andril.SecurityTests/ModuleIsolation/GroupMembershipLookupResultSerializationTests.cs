using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.SecurityTests.ModuleIsolation;

/// <summary>
/// Security tests verifying that <see cref="GroupMembershipLookupResult"/> prevents accidental
/// transport serialization of private membership existence, group/profile identifiers, module scope,
/// internal membership entities, and validation diagnostics while preserving internal C# access.
/// </summary>
public sealed class GroupMembershipLookupResultSerializationTests
{
    private static readonly Guid GroupId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProfileId1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProfileId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly JsonSerializerOptions StringEnumJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class TestMembershipRepositoryDouble : IGroupMembershipRepository
    {
        public Func<Guid, GroupModuleScope, Guid, CancellationToken, Task<GroupMembership?>>? Handler { get; set; }

        public Task<GroupMembership?> GetMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Handler is not null
                ? Handler(groupId, scope, memberProfileId, cancellationToken)
                : Task.FromResult<GroupMembership?>(null);
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

    private static void AssertSerializedPayloadConcealsAllProtectedData(GroupMembershipLookupResult result)
    {
        var defaultJson = JsonSerializer.Serialize(result);
        var enumStringJson = JsonSerializer.Serialize(result, StringEnumJsonOptions);

        Assert.Equal("{}", defaultJson);
        Assert.Equal("{}", enumStringJson);

        foreach (var json in new[] { defaultJson, enumStringJson })
        {
            Assert.DoesNotContain(GroupId1.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(GroupId2.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileId1.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ProfileId2.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupMembershipLookupResult.Membership), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupMembershipLookupResult.Validation), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupMembershipLookupResult.Status), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupMembershipLookupResult.IsValid), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupMembershipLookupResult.HasMembership), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("learning", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hobbies", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("social", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupKind.Classroom), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupKind.Club), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(nameof(GroupKind.Zone), json, StringComparison.OrdinalIgnoreCase);
        }

        var diagnostic = result.ToString();
        Assert.Contains("GroupMembershipLookupResult", diagnostic);
        Assert.DoesNotContain(GroupId1.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(GroupId2.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId1.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ProfileId2.ToString(), diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task Serialize_ExistingMembership_ConcealsMembershipExistenceScopeAndIdsAcrossAllModules(
        string module,
        GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var membership = new GroupMembership(GroupId1, scope, ProfileId1);
        var repo = new TestMembershipRepositoryDouble
        {
            Handler = (_, _, _, _) => Task.FromResult<GroupMembership?>(membership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, scope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.True(result.HasMembership);
        Assert.Equal(membership, result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, result.Status);

        AssertSerializedPayloadConcealsAllProtectedData(result);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task Serialize_MissingMembership_ConcealsAbsenceAndScopeAcrossAllModules(
        string module,
        GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var repo = new TestMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, scope, ProfileId1);

        Assert.True(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, result.Status);

        AssertSerializedPayloadConcealsAllProtectedData(result);
    }

    [Fact]
    public async Task Serialize_ForeignGroupReturned_ConcealsRejectionAndIds()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var foreignMembership = new GroupMembership(GroupId2, scope, ProfileId1);
        var repo = new TestMembershipRepositoryDouble
        {
            Handler = (_, _, _, _) => Task.FromResult<GroupMembership?>(foreignMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, scope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.GroupIdMismatch, result.Status);

        AssertSerializedPayloadConcealsAllProtectedData(result);
    }

    [Fact]
    public async Task Serialize_ForeignMemberReturned_ConcealsRejectionAndIds()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var foreignMember = new GroupMembership(GroupId1, scope, ProfileId2);
        var repo = new TestMembershipRepositoryDouble
        {
            Handler = (_, _, _, _) => Task.FromResult<GroupMembership?>(foreignMember)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, scope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.MemberProfileIdMismatch, result.Status);

        AssertSerializedPayloadConcealsAllProtectedData(result);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "hobbies", GroupKind.Club)]
    [InlineData("learning", GroupKind.Classroom, "social", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Club, "social", GroupKind.Zone)]
    [InlineData("social", GroupKind.Zone, "learning", GroupKind.Classroom)]
    public async Task Serialize_WrongModuleAndGroupKindReturned_ConcealsCrossModuleRejection(
        string requestedModule,
        GroupKind requestedKind,
        string returnedModule,
        GroupKind returnedKind)
    {
        var requestedScope = new GroupModuleScope(requestedModule, requestedKind);
        var returnedScope = new GroupModuleScope(returnedModule, returnedKind);
        var mismatchedMembership = new GroupMembership(GroupId1, returnedScope, ProfileId1);

        var repo = new TestMembershipRepositoryDouble
        {
            Handler = (_, _, _, _) => Task.FromResult<GroupMembership?>(mismatchedMembership)
        };
        var service = new GroupMembershipScopedLookupService(repo);

        var result = await service.LookupMembershipAsync(GroupId1, requestedScope, ProfileId1);

        Assert.False(result.IsValid);
        Assert.False(result.HasMembership);
        Assert.Null(result.Membership);
        Assert.Equal(GroupMembershipReadScopeStatus.ScopeMismatch, result.Status);

        AssertSerializedPayloadConcealsAllProtectedData(result);
    }

    [Fact]
    public async Task Serialize_RejectedInvalidIdentityAndNullScope_ConcealsFailureState()
    {
        var repo = new TestMembershipRepositoryDouble();
        var service = new GroupMembershipScopedLookupService(repo);
        var validScope = new GroupModuleScope("social", GroupKind.Zone);

        var invalidGroupResult = await service.LookupMembershipAsync(Guid.Empty, validScope, ProfileId1);
        var invalidProfileResult = await service.LookupMembershipAsync(GroupId1, validScope, Guid.Empty);
        var nullScopeResult = await service.LookupMembershipAsync(GroupId1, null!, ProfileId1);

        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, invalidGroupResult.Status);
        Assert.Equal(GroupMembershipReadScopeStatus.InvalidIdentity, invalidProfileResult.Status);
        Assert.Equal(GroupMembershipReadScopeStatus.NullInput, nullScopeResult.Status);

        AssertSerializedPayloadConcealsAllProtectedData(invalidGroupResult);
        AssertSerializedPayloadConcealsAllProtectedData(invalidProfileResult);
        AssertSerializedPayloadConcealsAllProtectedData(nullScopeResult);
    }

    [Fact]
    public void ResultType_DoesNotExposeImplicitPermissionOrRoleMembers()
    {
        var propertyNames = typeof(GroupMembershipLookupResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains("Role", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Grant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Admin", StringComparison.OrdinalIgnoreCase));
    }
}
