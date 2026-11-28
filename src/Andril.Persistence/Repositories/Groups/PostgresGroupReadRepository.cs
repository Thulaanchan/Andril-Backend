using System.Globalization;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Groups;

public sealed class PostgresGroupReadRepository(GroupsPostgresScope execution) :
    IGroupDiscoveryReadRepository, IGroupPersonalParticipationReadRepository
{
    private readonly AsyncLocal<ReadBoundary?> currentRead = new();

    public Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
        Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class =>
        execution.ExecuteAsync<T?>(query.Scope.Module, async (db, actor, ct) =>
        {
            if (actor != query.ActorProfileId) throw new ProfileAccessDeniedException();
            var rows = await db.Database.SqlQuery<DiscoveryRow>($"""
                SELECT id AS "Id", name AS "Name", visibility AS "Visibility", revision AS "Revision", observed_at AS "ObservedAt"
                FROM andril_groups.read_discovery({actor}, {query.Scope.Module}, {(int)query.Scope.GroupKind},
                    {query.SearchText}, {query.MaximumCandidates})
                """).ToListAsync(ct);
            const GroupDiscoveryDecision allowed = GroupDiscoveryDecision.Allowed;
            var revision = Guid.NewGuid().ToString("N");
            var evidence = rows.Select(row => new GroupDiscoveryEvidence(new GroupEntry(row.Id, query.Scope, row.Name),
                GroupDiscoveryLifecycle.Active, allowed,
                row.Visibility == "private" ? GroupDiscoveryPrivacy.Private : GroupDiscoveryPrivacy.Public,
                row.Visibility == "private" ? allowed : GroupDiscoveryDecision.Denied, allowed, allowed, allowed,
                FormatRevision(revision, row.Revision), row.ObservedAt)).ToArray();
            var snapshot = new GroupDiscoverySnapshot(actor, query.Scope, Array.AsReadOnly(evidence), revision);
            return await ProjectAsync(query, snapshot, project, ct);
        }, cancellationToken);

    public Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query, GroupDiscoverySnapshot snapshot,
        CancellationToken cancellationToken = default) => Task.FromResult(
        Revalidate(query, snapshot, cancellationToken) ? GroupDiscoveryDecision.Allowed : GroupDiscoveryDecision.Denied);

    public Task<T?> WithAuthorizedReadAsync<T>(GroupPersonalParticipationQuery query,
        Func<GroupPersonalParticipationSnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class =>
        execution.ExecuteAsync<T?>(query.Scope.Module, async (db, actor, ct) =>
        {
            if (actor != query.ActorProfileId) throw new ProfileAccessDeniedException();
            var row = await db.Database.SqlQuery<ParticipationRow>($"""
                SELECT is_member AS "IsMember", requested_at AS "RequestedAt", revision AS "Revision", observed_at AS "ObservedAt"
                FROM andril_groups.read_own_participation({actor}, {query.Scope.Module}, {query.GroupId})
                """).SingleOrDefaultAsync(ct);
            if (row is null) return null;

            var revision = FormatRevision(Guid.NewGuid().ToString("N"), row.Revision);
            var membership = row.IsMember ? new GroupMembership(query.GroupId, query.Scope, actor) : null;
            var request = row.RequestedAt is { } time ? new GroupJoinRequest(query.GroupId, query.Scope, actor, time.UtcDateTime) : null;
            const GroupPersonalParticipationDecision allowed = GroupPersonalParticipationDecision.Allowed;
            var snapshot = new GroupPersonalParticipationSnapshot(actor, query.GroupId, query.Scope, allowed, allowed, allowed,
                row.ObservedAt, revision, Observe(membership, revision), Observe(request, revision));
            return await ProjectAsync(query, snapshot, project, ct);
        }, cancellationToken);

    public Task<GroupPersonalParticipationDecision> RevalidateAsync(GroupPersonalParticipationQuery query,
        GroupPersonalParticipationSnapshot snapshot, CancellationToken cancellationToken = default) => Task.FromResult(
        Revalidate(query, snapshot, cancellationToken) ? GroupPersonalParticipationDecision.Allowed : GroupPersonalParticipationDecision.Denied);

    private async Task<T?> ProjectAsync<TSnapshot, T>(object query, TSnapshot snapshot,
        Func<TSnapshot, CancellationToken, Task<T?>> project, CancellationToken token) where TSnapshot : class where T : class
    {
        // The SQL read acquired the transaction's Group and applicable Social authorization locks.
        // The Accounts transaction retains those locks through the callback and return to the caller.
        var previous = currentRead.Value;
        var boundary = new ReadBoundary(query, snapshot);
        currentRead.Value = boundary;
        try
        {
            var result = await project(snapshot, token);
            token.ThrowIfCancellationRequested();
            return Interlocked.Exchange(ref boundary.State, 2) == 1 ? result : null;
        }
        finally
        {
            Interlocked.Exchange(ref boundary.State, 2);
            currentRead.Value = previous;
        }
    }

    private bool Revalidate(object query, object snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return currentRead.Value is { } boundary && ReferenceEquals(query, boundary.Query)
            && ReferenceEquals(snapshot, boundary.Snapshot) && Interlocked.CompareExchange(ref boundary.State, 1, 0) == 0;
    }

    private static string FormatRevision(string boundary, long revision) => boundary + ":" + revision.ToString(CultureInfo.InvariantCulture);

    private static GroupPersonalParticipationObservation<T> Observe<T>(T? entity, string revision) where T : class =>
        new(entity is null ? GroupPersonalParticipationPresence.Absent : GroupPersonalParticipationPresence.Present, entity, revision);

    private sealed class ReadBoundary(object query, object snapshot)
    {
        internal object Query { get; } = query;
        internal object Snapshot { get; } = snapshot;
        internal int State;
    }

    private sealed record DiscoveryRow(Guid Id, string Name, string Visibility, long Revision, DateTimeOffset ObservedAt);
    private sealed record ParticipationRow(bool IsMember, DateTimeOffset? RequestedAt, long Revision, DateTimeOffset ObservedAt);
}
