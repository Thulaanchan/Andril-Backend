using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Groups;

public sealed class PostgresGroupEvidenceProvider(GroupsPostgresScope execution) : IGroupAuthoritativeEvidenceProvider
{
    public Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(Guid profileId, string module, CancellationToken cancellationToken = default) =>
        execution.ExecuteAsync<AuthoritativeGroupProfileEvidence?>(module, async (db, actor, ct) =>
        {
            var value = await db.Database.SqlQuery<ProfileFacts>($"""
                SELECT age AS "Age", observed_at AS "ObservedAt"
                FROM andril_groups.read_profile_evidence({actor}, {module}, {profileId})
                """).SingleOrDefaultAsync(ct);
            return value is null ? null : new AuthoritativeGroupProfileEvidence(profileId, module, true, false, value.Age, value.ObservedAt);
        }, cancellationToken);

    public async Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
    {
        var facts = await execution.ExecuteAsync(scope.Module, (db, actor, ct) => ReadGroupAsync(db, actor, scope.Module, groupId, ct), cancellationToken);
        return facts is null ? null : new AuthoritativeGroupStateEvidence(groupId, scope, true, facts.IsOpen, facts.ObservedAt);
    }

    public Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default) =>
        execution.ExecuteForActorAsync<AuthoritativeGroupMembershipEvidence?>(profileId, async (db, module, ct) =>
        {
            var facts = await ReadGroupAsync(db, profileId, module, groupId, ct);
            return facts is null ? null : new AuthoritativeGroupMembershipEvidence(groupId, profileId, facts.IsMember,
                (GroupPrivilegedCapability)facts.Capabilities, facts.ObservedAt);
        }, cancellationToken);

    public Task<bool> IsGroupOwnerAsync(Guid groupId, Guid profileId, CancellationToken cancellationToken = default) =>
        execution.ExecuteForActorAsync(profileId, async (db, module, ct) =>
            (await ReadGroupAsync(db, profileId, module, groupId, ct))?.IsOwner == true, cancellationToken);

    public Task<int> GetCurrentMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
        execution.ExecuteAsync(scope.Module, async (db, actor, ct) =>
            (await ReadGroupAsync(db, actor, scope.Module, groupId, ct))?.MemberCount ?? throw new GroupAuthorityUnavailableException(), cancellationToken);

    public Task<int?> GetGroupCapacityAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
        execution.ExecuteAsync(scope.Module,
            (db, actor, ct) => db.Database.SqlQuery<int?>($"SELECT andril_groups.read_group_capacity({actor}, {groupId}) AS \"Value\"").SingleAsync(ct), cancellationToken);

    private static Task<GroupFacts?> ReadGroupAsync(Andril.Persistence.DbContexts.GroupsDbContext db, Guid actor, string module, Guid group, CancellationToken ct) =>
        db.Database.SqlQuery<GroupFacts>($"""
            SELECT is_open AS "IsOpen", is_owner AS "IsOwner", is_member AS "IsMember", capabilities AS "Capabilities", member_count AS "MemberCount",
                observed_at AS "ObservedAt" FROM andril_groups.read_participation_evidence({actor}, {module}, {group})
            """).SingleOrDefaultAsync(ct);

    private sealed record ProfileFacts(int Age, DateTimeOffset ObservedAt);
    private sealed record GroupFacts(bool IsOpen, bool IsOwner, bool IsMember, int Capabilities, int? MemberCount, DateTimeOffset ObservedAt);
}
