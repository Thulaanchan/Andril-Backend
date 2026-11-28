using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Groups;

public sealed class PostgresGroupMembershipTransactionRepository(GroupsPostgresScope execution) : IGroupMembershipTransactionRepository
{
    public async Task<GroupJoinRequestSubmissionTransactionResult> SubmitJoinRequestAtomicAsync(Guid groupId, GroupModuleScope scope,
        Guid requestingProfileId, DateTime requestedAtUtc, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (requestedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("A UTC timestamp is required.", nameof(requestedAtUtc));
        var result = await MutateAsync(groupId, scope, requestingProfileId, null, "submit", expectedRevision, cancellationToken);
        var request = result.RequestedAt is { } timestamp ? new GroupJoinRequest(groupId, scope, requestingProfileId, timestamp.UtcDateTime) : null;
        return new GroupJoinRequestSubmissionTransactionResult(result.Status == GroupMembershipTransactionStatus.Committed, result.Status, request, result.Revision);
    }

    public async Task<GroupJoinRequestCancellationTransactionResult> CancelJoinRequestAtomicAsync(Guid groupId, GroupModuleScope scope,
        Guid requestingProfileId, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var result = await MutateAsync(groupId, scope, requestingProfileId, null, "cancel", expectedRevision, cancellationToken);
        bool committed = result.Status == GroupMembershipTransactionStatus.Committed;
        return new GroupJoinRequestCancellationTransactionResult(committed, result.Status, committed, result.Revision);
    }

    public async Task<GroupAdmissionTransactionResult> ProcessAdmissionAtomicAsync(Guid groupId, GroupModuleScope scope,
        Guid applicantProfileId, bool approve, Guid actorProfileId, DateTime decisionAtUtc, long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (applicantProfileId == Guid.Empty || decisionAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Valid admission evidence is required.");
        var result = await MutateAsync(groupId, scope, actorProfileId, applicantProfileId, approve ? "approve" : "reject", expectedRevision, cancellationToken);
        bool committed = result.Status == GroupMembershipTransactionStatus.Committed;
        return new GroupAdmissionTransactionResult(committed, result.Status, committed && approve, committed,
            committed && approve ? new GroupMembership(groupId, scope, applicantProfileId) : null, result.Revision);
    }

    public async Task<GroupDepartureTransactionResult> LeaveGroupAtomicAsync(Guid groupId, GroupModuleScope scope,
        Guid memberProfileId, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var result = await MutateAsync(groupId, scope, memberProfileId, null, "leave", expectedRevision, cancellationToken);
        bool committed = result.Status == GroupMembershipTransactionStatus.Committed;
        return new GroupDepartureTransactionResult(committed, result.Status, committed, result.Revision);
    }

    public Task<GroupTransactionReconciliationResult> ReconcileTransactionOutcomeAsync(Guid groupId, GroupModuleScope scope,
        Guid operationCorrelationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The mutation contract has no caller-provided correlation key. An unidentified attempt stays unknown.
        return Task.FromResult(GroupTransactionReconciliationResult.NotApplied());
    }

    private Task<Outcome> MutateAsync(Guid group, GroupModuleScope scope, Guid expectedActor, Guid? applicant, string operation,
        long? revision, CancellationToken token)
    {
        if (group == Guid.Empty || expectedActor == Guid.Empty || revision is < 1) throw new ProfileAccessDeniedException();
        return execution.ExecuteAsync(scope.Module, (db, actor, ct) =>
        {
            if (actor != expectedActor) throw new ProfileAccessDeniedException();
            return db.Database.SqlQuery<Outcome>($"""
                SELECT status AS "Status", revision AS "Revision", requested_at AS "RequestedAt"
                FROM andril_groups.mutate_membership({actor}, {scope.Module}, {group}, {applicant}, {operation}, {revision})
                """).SingleAsync(ct);
        }, token);
    }

    private sealed record Outcome(GroupMembershipTransactionStatus Status, long Revision, DateTimeOffset? RequestedAt);
}
