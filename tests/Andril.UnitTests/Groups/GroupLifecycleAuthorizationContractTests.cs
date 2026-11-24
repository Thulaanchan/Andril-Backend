using System;
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

/// <summary>
/// Domain contract and lifecycle authorization tests verifying invariants across
/// <see cref="GroupEntry"/>, <see cref="GroupMembership"/>, and <see cref="GroupJoinRequest"/>.
/// Confirms strict identity validation, cross-module scope rejection, lifecycle transition prerequisites,
/// repository scoped isolation contract compliance, and diagnostic concealment.
/// </summary>
public sealed class GroupLifecycleAuthorizationContractTests
{
    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ForeignGroupId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MemberProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ForeignProfileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedTimestampUtc = new(2026, 11, 24, 14, 0, 0, DateTimeKind.Utc);

    private static GroupModuleScope LearningScope => new("learning", GroupKind.Classroom);
    private static GroupModuleScope HobbiesScope => new("hobbies", GroupKind.Club);
    private static GroupModuleScope SocialScope => new("social", GroupKind.Zone);

    private sealed class InMemoryMembershipRepoDouble : IGroupMembershipRepository
    {
        private GroupMembership? _currentMembership;

        public InMemoryMembershipRepoDouble(GroupMembership? initial = null)
        {
            _currentMembership = initial;
        }

        public Task<GroupMembership?> GetMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_currentMembership is not null &&
                _currentMembership.GroupId == groupId &&
                _currentMembership.Scope.Equals(scope) &&
                _currentMembership.MemberProfileId == memberProfileId)
            {
                return Task.FromResult<GroupMembership?>(_currentMembership);
            }
            return Task.FromResult<GroupMembership?>(null);
        }

        public Task<bool> ExistsAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_currentMembership is not null &&
                _currentMembership.GroupId == groupId &&
                _currentMembership.Scope.Equals(scope) &&
                _currentMembership.MemberProfileId == memberProfileId);
        }

        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _currentMembership = membership ?? throw new ArgumentNullException(nameof(membership));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveMembershipAsync(
            Guid groupId,
            GroupModuleScope scope,
            Guid memberProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_currentMembership is not null &&
                _currentMembership.GroupId == groupId &&
                _currentMembership.Scope.Equals(scope) &&
                _currentMembership.MemberProfileId == memberProfileId)
            {
                _currentMembership = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_currentMembership is not null &&
                _currentMembership.GroupId == groupId &&
                _currentMembership.Scope.Equals(scope) ? 1 : 0);
        }
    }

    #region 1. Construction and Identity Invariants

    [Fact]
    public void GroupEntry_Constructor_ValidatesParametersFailClosed()
    {
        // Valid
        var entry = new GroupEntry(GroupId, SocialScope, "Valid Zone Name");
        Assert.Equal(GroupId, entry.Id);
        Assert.Equal(SocialScope, entry.Scope);
        Assert.Equal("Valid Zone Name", entry.Name);

        // Empty ID throws
        Assert.Throws<ArgumentException>(() => new GroupEntry(Guid.Empty, SocialScope, "Name"));

        // Null scope throws
        Assert.Throws<ArgumentNullException>(() => new GroupEntry(GroupId, null!, "Name"));

        // Whitespace name throws
        Assert.Throws<ArgumentException>(() => new GroupEntry(GroupId, SocialScope, "   "));
    }

    [Fact]
    public void GroupMembership_Constructor_ValidatesParametersFailClosed()
    {
        // Valid
        var membership = new GroupMembership(GroupId, HobbiesScope, MemberProfileId);
        Assert.Equal(GroupId, membership.GroupId);
        Assert.Equal(HobbiesScope, membership.Scope);
        Assert.Equal(MemberProfileId, membership.MemberProfileId);

        // Empty GroupId throws
        Assert.Throws<ArgumentException>(() => new GroupMembership(Guid.Empty, HobbiesScope, MemberProfileId));

        // Null scope throws
        Assert.Throws<ArgumentNullException>(() => new GroupMembership(GroupId, null!, MemberProfileId));

        // Empty MemberProfileId throws
        Assert.Throws<ArgumentException>(() => new GroupMembership(GroupId, HobbiesScope, Guid.Empty));
    }

    [Fact]
    public void GroupJoinRequest_Constructor_ValidatesParametersFailClosed()
    {
        // Valid
        var request = new GroupJoinRequest(GroupId, LearningScope, MemberProfileId, FixedTimestampUtc);
        Assert.Equal(GroupId, request.GroupId);
        Assert.Equal(LearningScope, request.Scope);
        Assert.Equal(MemberProfileId, request.RequestingProfileId);
        Assert.Equal(FixedTimestampUtc, request.RequestedAtUtc);

        // Non-UTC timestamp throws
        var localTime = new DateTime(2026, 11, 24, 14, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => new GroupJoinRequest(GroupId, LearningScope, MemberProfileId, localTime));

        // Empty identities throw
        Assert.Throws<ArgumentException>(() => new GroupJoinRequest(Guid.Empty, LearningScope, MemberProfileId, FixedTimestampUtc));
        Assert.Throws<ArgumentException>(() => new GroupJoinRequest(GroupId, LearningScope, Guid.Empty, FixedTimestampUtc));
    }

    #endregion

    #region 2. Lifecycle Transition: Join Request -> Membership -> Removal

    [Fact]
    public void JoinRequest_DoesNotConstituteMembership()
    {
        var request = new GroupJoinRequest(GroupId, SocialScope, MemberProfileId, FixedTimestampUtc);

        // Structural separation: Join request cannot be treated as membership
        Assert.False(typeof(GroupMembership).IsAssignableFrom(typeof(GroupJoinRequest)));
        Assert.False(typeof(GroupJoinRequest).IsAssignableFrom(typeof(GroupMembership)));
        Assert.NotEqual(typeof(GroupMembership), request.GetType());
    }

    [Fact]
    public async Task MembershipLifecycle_SaveAndRemove_EnforcesExactScopeAndIdentity()
    {
        var repo = new InMemoryMembershipRepoDouble();
        var lookupService = new GroupMembershipScopedLookupService(repo);

        // 1. Initial state: absent membership
        var initialLookup = await lookupService.LookupMembershipAsync(GroupId, SocialScope, MemberProfileId);
        Assert.True(initialLookup.IsValid);
        Assert.False(initialLookup.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, initialLookup.Status);

        // 2. Add membership
        var membership = new GroupMembership(GroupId, SocialScope, MemberProfileId);
        await repo.SaveMembershipAsync(membership);

        // 3. Lookup with matching scope: found
        var foundLookup = await lookupService.LookupMembershipAsync(GroupId, SocialScope, MemberProfileId);
        Assert.True(foundLookup.IsValid);
        Assert.True(foundLookup.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.Valid, foundLookup.Status);
        Assert.NotNull(foundLookup.Membership);

        // 4. Lookup with foreign profile: absent
        var foreignProfileLookup = await lookupService.LookupMembershipAsync(GroupId, SocialScope, ForeignProfileId);
        Assert.True(foreignProfileLookup.IsValid);
        Assert.False(foreignProfileLookup.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, foreignProfileLookup.Status);

        // 5. Lookup with foreign group: absent
        var foreignGroupLookup = await lookupService.LookupMembershipAsync(ForeignGroupId, SocialScope, MemberProfileId);
        Assert.True(foreignGroupLookup.IsValid);
        Assert.False(foreignGroupLookup.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, foreignGroupLookup.Status);

        // 6. Remove membership with exact scope
        var removed = await repo.RemoveMembershipAsync(GroupId, SocialScope, MemberProfileId);
        Assert.True(removed);

        // 7. Post-removal lookup: absent
        var postRemovalLookup = await lookupService.LookupMembershipAsync(GroupId, SocialScope, MemberProfileId);
        Assert.True(postRemovalLookup.IsValid);
        Assert.False(postRemovalLookup.HasMembership);
        Assert.Equal(GroupMembershipReadScopeStatus.NullResult, postRemovalLookup.Status);
    }

    #endregion

    #region 3. Cross-Module Scope Enforcement Across Lifecycle Artifacts

    [Fact]
    public void GroupEntryAndMembership_CrossModuleCompatibility_StrictlyDenied()
    {
        var learningGroup = new GroupEntry(GroupId, LearningScope, "Learning Classroom");
        var socialMembership = new GroupMembership(GroupId, SocialScope, MemberProfileId);

        var isCompatible = GroupMembershipScopePolicy.IsCompatible(learningGroup, socialMembership);
        Assert.False(isCompatible);
    }

    [Fact]
    public void GroupEntryAndJoinRequest_CrossModuleCompatibility_StrictlyDenied()
    {
        var hobbiesGroup = new GroupEntry(GroupId, HobbiesScope, "Photography Club");
        var socialRequest = new GroupJoinRequest(GroupId, SocialScope, MemberProfileId, FixedTimestampUtc);

        var validation = GroupJoinRequestScopePolicy.Validate(hobbiesGroup, socialRequest);
        Assert.False(validation.IsValid);
        Assert.Equal(GroupJoinRequestScopeStatus.ModuleMismatch, validation.Status);
    }

    #endregion

    #region 4. Privacy and Concealment Invariants

    [Fact]
    public void DomainEntities_ToStringAndJsonSerialization_ConcealConfidentialState()
    {
        var entry = new GroupEntry(GroupId, SocialScope, "Confidential Zone");
        var membership = new GroupMembership(GroupId, SocialScope, MemberProfileId);
        var request = new GroupJoinRequest(GroupId, SocialScope, MemberProfileId, FixedTimestampUtc);

        // Assert ToString non-disclosing representation
        Assert.Equal("GroupEntry [private]", entry.ToString());
        Assert.Equal("GroupMembership [social:Zone]", membership.ToString());
        Assert.DoesNotContain(GroupId.ToString(), membership.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MemberProfileId.ToString(), membership.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("GroupJoinRequest [social:Zone]", request.ToString());
        Assert.DoesNotContain(GroupId.ToString(), request.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MemberProfileId.ToString(), request.ToString(), StringComparison.OrdinalIgnoreCase);

        // Assert JSON serialization conceals confidential IDs
        var entryJson = JsonSerializer.Serialize(entry);
        var membershipJson = JsonSerializer.Serialize(membership);
        var requestJson = JsonSerializer.Serialize(request);

        Assert.Equal("{}", entryJson);
        Assert.Equal("{}", requestJson);

        Assert.DoesNotContain(GroupId.ToString(), entryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confidential Zone", entryJson, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(GroupId.ToString(), membershipJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MemberProfileId.ToString(), membershipJson, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(GroupId.ToString(), requestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MemberProfileId.ToString(), requestJson, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
