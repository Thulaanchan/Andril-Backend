using System.Text.Json.Serialization;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;

namespace Andril.Groups.Services;

/// <summary>Server-only authorized result. Creation is limited to this application assembly.</summary>
public sealed class AuthorizedGroupDiscoveryResult
{
    [JsonIgnore] public Guid ActorProfileId { get; }
    [JsonIgnore] public GroupModuleScope Scope { get; }
    [JsonIgnore] public IReadOnlyList<GroupEntry> Groups { get; }
    internal AuthorizedGroupDiscoveryResult(Guid actor, GroupModuleScope scope, IEnumerable<GroupEntry> groups)
        => (ActorProfileId, Scope, Groups) = (actor, scope, Array.AsReadOnly(groups.ToArray()));
    public override string ToString() => "Authorized Group discovery result [private]";
}

/// <summary>Read-only discovery orchestration; does not grant membership or activate endpoints.</summary>
public sealed class GroupDiscoveryService
{
    private readonly IGroupDiscoveryReadRepository _repository;
    private readonly TimeProvider _clock;
    public GroupDiscoveryService(IGroupDiscoveryReadRepository repository, TimeProvider? clock = null)
        => (_repository, _clock) = (repository ?? throw new ArgumentNullException(nameof(repository)), clock ?? TimeProvider.System);

    public async Task<AuthorizedGroupDiscoveryResult?> DiscoverAsync(AuthoritativeGroupProfileEvidence actor,
        GroupModuleScope scope, string searchText, int maximumCandidates, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(searchText);
        if (maximumCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        if (!IsEligibleActor(actor, scope, _clock.GetUtcNow())) return null;
        var query = new GroupDiscoveryQuery(actor.ProfileId, scope, searchText, maximumCandidates);
        var projectionGate = new object();
        var projectionStarted = false;
        var projectionRejected = false;
        var readClosed = false;
        Task<AuthorizedGroupDiscoveryResult?>? projection = null;
        Task<AuthorizedGroupDiscoveryResult?>? read = null;

        try
        {
            var authorizedRead = _repository.WithAuthorizedReadAsync<AuthorizedGroupDiscoveryResult>(
                query, ProjectOnce, cancellationToken);
            lock (projectionGate) read = authorizedRead;

            var result = await authorizedRead;
            cancellationToken.ThrowIfCancellationRequested();
            lock (projectionGate)
            {
                if (projectionRejected || projection is not { IsCompletedSuccessfully: true } ||
                    !ReferenceEquals(result, projection.Result)) return null;
            }

            return IsEligibleActor(actor, scope, _clock.GetUtcNow()) ? result : null;
        }
        finally
        {
            lock (projectionGate) readClosed = true;
        }

        Task<AuthorizedGroupDiscoveryResult?> ProjectOnce(GroupDiscoverySnapshot snapshot, CancellationToken ct)
        {
            lock (projectionGate)
            {
                if (readClosed || projectionStarted || read is { IsCompleted: true })
                {
                    projectionRejected = true;
                    return Task.FromResult<AuthorizedGroupDiscoveryResult?>(null);
                }

                projectionStarted = true;
            }

            var callback = ProjectAsync(snapshot, ct);
            lock (projectionGate) projection = callback;
            return callback;
        }

        async Task<AuthorizedGroupDiscoveryResult?> ProjectAsync(GroupDiscoverySnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshot is null || snapshot.ActorProfileId != actor.ProfileId || snapshot.Scope != scope ||
                snapshot.Groups is null || snapshot.Groups.Count > maximumCandidates ||
                string.IsNullOrWhiteSpace(snapshot.AuthorizationRevision)) return null;
            var evidenceById = new Dictionary<Guid, GroupDiscoveryEvidence>();
            var rejected = new HashSet<Guid>();
            var groups = new Dictionary<Guid, GroupEntry>();
            foreach (var item in snapshot.Groups)
            {
                ct.ThrowIfCancellationRequested();
                if (item?.Group is not { } group) continue;
                if (evidenceById.TryGetValue(group.Id, out var prior))
                {
                    if (!Equivalent(prior, item)) { rejected.Add(group.Id); groups.Remove(group.Id); }
                    continue;
                }
                evidenceById.Add(group.Id, item);
                if (!rejected.Contains(group.Id) && Permitted(item, scope, _clock.GetUtcNow())) groups.Add(group.Id, group);
            }
            if (await _repository.RevalidateAsync(query, snapshot, ct) != GroupDiscoveryDecision.Allowed) return null;
            ct.ThrowIfCancellationRequested();
            if (!IsEligibleActor(actor, scope, _clock.GetUtcNow()) ||
                groups.Keys.Any(id => !Permitted(evidenceById[id], scope, _clock.GetUtcNow()))) return null;
            return new AuthorizedGroupDiscoveryResult(actor.ProfileId, scope, groups.Values.OrderBy(group => group.Id));
        }
    }

    public static bool IsEligibleActor(AuthoritativeGroupProfileEvidence? actor, GroupModuleScope? scope, DateTimeOffset now)
        => actor is not null && scope is not null && actor.ProfileId != Guid.Empty &&
           scope.Module is "learning" or "hobbies" or "social" &&
           GroupModuleScopePolicy.IsCompatible(scope.Module, scope.GroupKind) &&
           string.Equals(actor.Module, scope.Module, StringComparison.Ordinal) && actor.IsActive && !actor.IsRestricted &&
           Fresh(actor.EvidenceTimestampUtc, now) &&
           (scope.GroupKind != GroupKind.Zone || actor.VerifiedAge >= GroupParticipationAuthorizationPolicy.SocialZoneMinimumAge);

    private static bool Fresh(DateTimeOffset timestamp, DateTimeOffset now) => timestamp != default &&
        timestamp <= now && now - timestamp <= GroupParticipationAuthorizationPolicy.MaximumEvidenceAge;

    private static bool Permitted(GroupDiscoveryEvidence item, GroupModuleScope scope, DateTimeOffset now)
        => item.Group.Id != Guid.Empty && item.Group.Scope == scope && !string.IsNullOrWhiteSpace(item.Group.Name) &&
           item.Lifecycle == GroupDiscoveryLifecycle.Active && item.Discoverability == GroupDiscoveryDecision.Allowed &&
           item.Visibility == GroupDiscoveryDecision.Allowed && item.MembershipRestrictions == GroupDiscoveryDecision.Allowed &&
           item.BlockingRestrictions == GroupDiscoveryDecision.Allowed && Fresh(item.EvidenceTimestampUtc, now) &&
           !string.IsNullOrWhiteSpace(item.Revision) &&
           (item.Privacy == GroupDiscoveryPrivacy.Public ||
               (item.Privacy == GroupDiscoveryPrivacy.Private && item.PrivateDiscoveryAuthorization == GroupDiscoveryDecision.Allowed));

    private static bool Equivalent(GroupDiscoveryEvidence a, GroupDiscoveryEvidence b) =>
        a.Group.Id == b.Group.Id && a.Group.Scope == b.Group.Scope &&
        string.Equals(a.Group.Name, b.Group.Name, StringComparison.Ordinal) &&
        (a with { Group = b.Group }) == b;
}
