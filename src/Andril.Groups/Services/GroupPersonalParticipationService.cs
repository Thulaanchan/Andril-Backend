using System.Text.Json.Serialization;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>The caller's participation observed in an authorized read.</summary>
public enum GroupPersonalParticipationState { None, Member, OutstandingJoinRequest }

/// <summary>Internal application result for one authorized caller and group.</summary>
public sealed class AuthorizedGroupPersonalParticipationResult
{
    [JsonIgnore] public Guid ActorProfileId { get; }
    [JsonIgnore] public Guid GroupId { get; }
    [JsonIgnore] public GroupModuleScope Scope { get; }
    [JsonIgnore] public GroupPersonalParticipationState State { get; }
    internal DateTimeOffset EvidenceTimestampUtc { get; }

    internal AuthorizedGroupPersonalParticipationResult(GroupPersonalParticipationQuery query,
        GroupPersonalParticipationState state, DateTimeOffset evidenceTimestampUtc)
        => (ActorProfileId, GroupId, Scope, State, EvidenceTimestampUtc) =
            (query.ActorProfileId, query.GroupId, query.Scope, state, evidenceTimestampUtc);

    public override string ToString() => "Authorized personal Group participation result [private]";
}

/// <summary>Verifies the caller's current participation in one visible, module-scoped group.</summary>
public sealed class GroupPersonalParticipationService
{
    private readonly IGroupPersonalParticipationReadRepository _repository;
    private readonly TimeProvider _clock;

    public GroupPersonalParticipationService(IGroupPersonalParticipationReadRepository repository, TimeProvider? clock = null)
        => (_repository, _clock) = (repository ?? throw new ArgumentNullException(nameof(repository)),
            clock ?? TimeProvider.System);

    public async Task<AuthorizedGroupPersonalParticipationResult?> GetStatusAsync(AuthoritativeGroupProfileEvidence actor,
        GroupModuleScope scope, Guid groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (groupId == Guid.Empty || !GroupDiscoveryService.IsEligibleActor(actor, scope, _clock.GetUtcNow())) return null;

        var query = new GroupPersonalParticipationQuery(actor.ProfileId, groupId, scope);
        var projectionGate = new object();
        var projectionStarted = false;
        var projectionRejected = false;
        var readClosed = false;
        Task<AuthorizedGroupPersonalParticipationResult?>? projection = null;
        Task<AuthorizedGroupPersonalParticipationResult?>? read = null;

        try
        {
            var authorizedRead = _repository.WithAuthorizedReadAsync<AuthorizedGroupPersonalParticipationResult>(
                query, ProjectOnce, cancellationToken);
            lock (projectionGate) read = authorizedRead;

            var result = await authorizedRead;
            cancellationToken.ThrowIfCancellationRequested();
            lock (projectionGate)
            {
                if (result is null || projectionRejected || projection is not { IsCompletedSuccessfully: true } ||
                    !ReferenceEquals(result, projection.Result)) return null;
            }

            return IsCurrent(actor, scope, result.EvidenceTimestampUtc, _clock.GetUtcNow()) ? result : null;
        }
        finally
        {
            lock (projectionGate) readClosed = true;
        }

        Task<AuthorizedGroupPersonalParticipationResult?> ProjectOnce(GroupPersonalParticipationSnapshot snapshot,
            CancellationToken token)
        {
            lock (projectionGate)
            {
                if (readClosed || projectionStarted || read is { IsCompleted: true })
                {
                    projectionRejected = true;
                    return Task.FromResult<AuthorizedGroupPersonalParticipationResult?>(null);
                }

                projectionStarted = true;
            }

            var callback = ProjectAsync(snapshot, token);
            lock (projectionGate) projection = callback;
            return callback;
        }

        async Task<AuthorizedGroupPersonalParticipationResult?> ProjectAsync(GroupPersonalParticipationSnapshot snapshot,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!TryGetState(query, snapshot, out var state) ||
                !IsCurrent(actor, scope, snapshot.EvidenceTimestampUtc, _clock.GetUtcNow())) return null;

            var decision = await _repository.RevalidateAsync(query, snapshot, token);
            token.ThrowIfCancellationRequested();
            if (decision != GroupPersonalParticipationDecision.Allowed ||
                !IsCurrent(actor, scope, snapshot.EvidenceTimestampUtc, _clock.GetUtcNow())) return null;

            return new AuthorizedGroupPersonalParticipationResult(query, state, snapshot.EvidenceTimestampUtc);
        }
    }

    private static bool TryGetState(GroupPersonalParticipationQuery query, GroupPersonalParticipationSnapshot? snapshot,
        out GroupPersonalParticipationState state)
    {
        state = GroupPersonalParticipationState.None;
        if (snapshot is null || snapshot.ActorProfileId != query.ActorProfileId || snapshot.GroupId != query.GroupId ||
            snapshot.Scope != query.Scope || snapshot.Visibility != GroupPersonalParticipationDecision.Allowed ||
            snapshot.Access != GroupPersonalParticipationDecision.Allowed ||
            snapshot.BlockingRestrictions != GroupPersonalParticipationDecision.Allowed ||
            string.IsNullOrWhiteSpace(snapshot.ReadRevision) ||
            !IsConsistent(snapshot.Membership, snapshot.ReadRevision) ||
            !IsConsistent(snapshot.OutstandingJoinRequest, snapshot.ReadRevision)) return false;

        var membership = snapshot.Membership.Entity;
        var request = snapshot.OutstandingJoinRequest.Entity;
        if (membership is not null)
        {
            if (request is not null || membership.GroupId != query.GroupId || membership.Scope != query.Scope ||
                membership.MemberProfileId != query.ActorProfileId) return false;
            state = GroupPersonalParticipationState.Member;
        }
        else if (request is not null)
        {
            if (request.GroupId != query.GroupId || request.Scope != query.Scope ||
                request.RequestingProfileId != query.ActorProfileId ||
                request.RequestedAtUtc > snapshot.EvidenceTimestampUtc.UtcDateTime) return false;
            state = GroupPersonalParticipationState.OutstandingJoinRequest;
        }

        return true;
    }

    private static bool IsConsistent<T>(GroupPersonalParticipationObservation<T>? observation, string readRevision)
        where T : class
        => observation is not null && string.Equals(observation.ReadRevision, readRevision, StringComparison.Ordinal) &&
           ((observation.Presence == GroupPersonalParticipationPresence.Present && observation.Entity is not null) ||
            (observation.Presence == GroupPersonalParticipationPresence.Absent && observation.Entity is null));

    private static bool IsCurrent(AuthoritativeGroupProfileEvidence actor, GroupModuleScope scope,
        DateTimeOffset timestamp, DateTimeOffset now)
        => timestamp != default && timestamp <= now && now - timestamp <= GroupParticipationAuthorizationPolicy.MaximumEvidenceAge &&
           GroupDiscoveryService.IsEligibleActor(actor, scope, now);
}
