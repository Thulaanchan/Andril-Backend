using Andril.Groups.Entities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Groups;

public sealed class PostgresGroupHostRequests(GroupsPostgresScope execution)
{
    public Task<List<PendingHostRequest>> ReadAsync(Guid group, GroupModuleScope scope, Guid? requestId, int maximum,
        CancellationToken token = default) =>
        execution.ExecuteAsync(scope.Module, (db, actor, ct) => db.Database.SqlQuery<PendingHostRequest>($"""
            SELECT id AS "Id", applicant_id AS "ApplicantId", requested_at AS "RequestedAt", revision AS "Revision"
            FROM andril_groups.read_host_requests({actor}, {scope.Module}, {group}, {requestId}, {maximum})
            """).ToListAsync(ct), token);
}

public sealed record PendingHostRequest(Guid Id, Guid ApplicantId, DateTimeOffset RequestedAt, long Revision)
{
    public override string ToString() => "Pending Group request [private]";
}
