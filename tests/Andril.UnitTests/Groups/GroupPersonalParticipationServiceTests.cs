using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.JoinRequests;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;
using Decision = Andril.Groups.Interfaces.GroupPersonalParticipationDecision;
using Presence = Andril.Groups.Interfaces.GroupPersonalParticipationPresence;
using State = Andril.Groups.Services.GroupPersonalParticipationState;

namespace Andril.UnitTests.Groups;

public sealed class GroupPersonalParticipationServiceTests
{
    private const string Revision = "consistent-read";
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly GroupModuleScope Learning = new("learning", GroupKind.Classroom);
    private static readonly GroupModuleScope Hobbies = new("hobbies", GroupKind.Club);
    private static readonly GroupModuleScope Social = new("social", GroupKind.Zone);

    [Theory]
    [InlineData("learning", GroupKind.Classroom, State.Member)]
    [InlineData("learning", GroupKind.Classroom, State.OutstandingJoinRequest)]
    [InlineData("learning", GroupKind.Classroom, State.None)]
    [InlineData("hobbies", GroupKind.Club, State.Member)]
    [InlineData("hobbies", GroupKind.Club, State.OutstandingJoinRequest)]
    [InlineData("hobbies", GroupKind.Club, State.None)]
    [InlineData("social", GroupKind.Zone, State.Member)]
    [InlineData("social", GroupKind.Zone, State.OutstandingJoinRequest)]
    [InlineData("social", GroupKind.Zone, State.None)]
    public async Task ExactAuthorizedRead_ReturnsCallerParticipation(string module, GroupKind kind, State state)
    {
        var scope = new GroupModuleScope(module, kind);
        var snapshot = Evidence(scope, state);
        var repository = new Repository(snapshot);
        using var cancellation = new CancellationTokenSource();
        var result = await Read(repository, scope, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(state, result.State);
        Assert.Equal(ActorId, result.ActorProfileId);
        Assert.Equal(GroupId, result.GroupId);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(new GroupPersonalParticipationQuery(ActorId, GroupId, scope), repository.Query);
        Assert.Same(snapshot, repository.RevalidatedSnapshot);
        Assert.Equal(cancellation.Token, repository.RevalidationToken);
        Assert.Equal(1, repository.RevalidationCount);
    }

    [Theory]
    [InlineData("both-records")]
    [InlineData("unknown-membership")]
    [InlineData("unknown-request")]
    [InlineData("missing-observation")]
    [InlineData("missing-entity")]
    [InlineData("entity-marked-absent")]
    [InlineData("revision-mismatch")]
    [InlineData("missing-revision")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("hidden")]
    [InlineData("access-unknown")]
    [InlineData("blocked")]
    [InlineData("foreign-actor")]
    [InlineData("foreign-group")]
    [InlineData("foreign-module")]
    [InlineData("foreign-member")]
    [InlineData("foreign-requester")]
    [InlineData("request-for-other-group")]
    [InlineData("membership-in-other-module")]
    [InlineData("request-after-read")]
    public async Task IncompleteContradictoryOrForeignEvidence_IsRejected(string change)
    {
        var snapshot = Evidence(Learning, State.None);
        snapshot = change switch
        {
            "both-records" => Evidence(Learning, State.Member) with { OutstandingJoinRequest = Request(Learning) },
            "unknown-membership" => snapshot with { Membership = new(Presence.Unknown, null, Revision) },
            "unknown-request" => snapshot with { OutstandingJoinRequest = new((Presence)99, null, Revision) },
            "missing-observation" => snapshot with { Membership = null! },
            "missing-entity" => snapshot with { Membership = new(Presence.Present, null, Revision) },
            "entity-marked-absent" => snapshot with { Membership = new(Presence.Absent, new(GroupId, Learning, ActorId), Revision) },
            "revision-mismatch" => snapshot with { Membership = snapshot.Membership with { ReadRevision = "older-read" } },
            "missing-revision" => snapshot with { ReadRevision = " " },
            "stale" => snapshot with { EvidenceTimestampUtc = Now - GroupParticipationAuthorizationPolicy.MaximumEvidenceAge - TimeSpan.FromTicks(1) },
            "future" => snapshot with { EvidenceTimestampUtc = Now.AddTicks(1) },
            "hidden" => snapshot with { Visibility = Decision.Denied },
            "access-unknown" => snapshot with { Access = Decision.Unknown },
            "blocked" => snapshot with { BlockingRestrictions = Decision.Denied },
            "foreign-actor" => snapshot with { ActorProfileId = Guid.NewGuid() },
            "foreign-group" => snapshot with { GroupId = Guid.NewGuid() },
            "foreign-module" => snapshot with { Scope = Hobbies },
            "foreign-member" => snapshot with { Membership = new(Presence.Present, new(GroupId, Learning, Guid.NewGuid()), Revision) },
            "foreign-requester" => snapshot with { OutstandingJoinRequest = Request(Learning, actor: Guid.NewGuid()) },
            "request-for-other-group" => snapshot with { OutstandingJoinRequest = Request(Learning, group: Guid.NewGuid()) },
            "membership-in-other-module" => snapshot with { Membership = new(Presence.Present, new(GroupId, Hobbies, ActorId), Revision) },
            "request-after-read" => snapshot with { OutstandingJoinRequest = Request(Learning, requestedAt: Now.UtcDateTime.AddTicks(1)) },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var repository = new Repository(snapshot);

        Assert.Null(await Read(repository, Learning));
        Assert.Equal(0, repository.RevalidationCount);
    }

    [Fact]
    public async Task IneligibleActorsAndInvalidTargets_DoNotReachRepository()
    {
        var repository = new Repository(Evidence(Social, State.Member));
        var service = new GroupPersonalParticipationService(repository, new Clock());
        var actor = Actor(Social.Module);
        foreach (var ineligible in new[]
        {
            actor with { IsActive = false }, actor with { IsRestricted = true }, actor with { VerifiedAge = null },
            actor with { VerifiedAge = 17 }, actor with { Module = Learning.Module },
            actor with { EvidenceTimestampUtc = Now.AddMinutes(-10) }
        })
            Assert.Null(await service.GetStatusAsync(ineligible, Social, GroupId));
        Assert.Null(await service.GetStatusAsync(actor, Social, Guid.Empty));
        Assert.Null(repository.Query);
    }

    [Theory]
    [InlineData(Decision.Unknown)]
    [InlineData(Decision.Denied)]
    [InlineData((Decision)99)]
    public async Task CurrentAuthorityMustExplicitlyAllowRelease(Decision decision)
    {
        var repository = new Repository(Evidence(Hobbies, State.Member)) { Decision = decision };
        Assert.Null(await Read(repository, Hobbies));
        Assert.Equal(1, repository.RevalidationCount);
    }

    [Fact]
    public async Task OuterBoundaryCanRevokeAndEvidenceCanExpireBeforeRelease()
    {
        var repository = new Repository(Evidence(Hobbies, State.Member));
        var clock = new Clock();
        var service = new GroupPersonalParticipationService(repository, clock);
        repository.Release = async project =>
        {
            Assert.NotNull(await project());
            return null;
        };
        Assert.Null(await service.GetStatusAsync(Actor(Hobbies.Module), Hobbies, GroupId));

        repository.Release = async project =>
        {
            var projected = await project();
            Assert.NotNull(projected);
            clock.Current = Now.AddTicks(1);
            return projected;
        };
        repository.Snapshot = repository.Snapshot with
        {
            EvidenceTimestampUtc = Now - GroupParticipationAuthorizationPolicy.MaximumEvidenceAge
        };
        Assert.Null(await service.GetStatusAsync(Actor(Hobbies.Module), Hobbies, GroupId));
    }

    [Fact]
    public async Task CachedSubstitutedAndRepeatedResults_DoNotRelease()
    {
        var repository = new Repository(Evidence(Learning, State.Member));
        var previous = await Read(repository, Learning);
        Assert.NotNull(previous);

        repository.Release = _ => Task.FromResult<object?>(previous);
        Assert.Null(await Read(repository, Learning));
        repository.Release = async project =>
        {
            Assert.NotNull(await project());
            return previous;
        };
        Assert.Null(await Read(repository, Learning));
        repository.Release = async project =>
        {
            var first = await project();
            Assert.NotNull(first);
            Assert.Null(await project());
            return first;
        };
        Assert.Null(await Read(repository, Learning));
    }

    [Fact]
    public async Task CancellationIsObservedBeforeReadingAndAfterVerifiedProjection()
    {
        var repository = new Repository(Evidence(Social, State.Member));
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(repository, Social, before.Token));
        Assert.Null(repository.Query);

        using var during = new CancellationTokenSource();
        repository.Release = async project =>
        {
            var result = await project();
            Assert.NotNull(result);
            during.Cancel();
            return result;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(repository, Social, during.Token));
    }

    private static Task<AuthorizedGroupPersonalParticipationResult?> Read(Repository repository, GroupModuleScope scope,
        CancellationToken cancellationToken = default) => new GroupPersonalParticipationService(repository, new Clock())
        .GetStatusAsync(Actor(scope.Module), scope, GroupId, cancellationToken);

    private static AuthoritativeGroupProfileEvidence Actor(string module) => new(ActorId, module, true, false, 25, Now);

    private static GroupPersonalParticipationSnapshot Evidence(GroupModuleScope scope, State state) =>
        new(ActorId, GroupId, scope, Decision.Allowed, Decision.Allowed, Decision.Allowed, Now, Revision,
            state == State.Member ? new(Presence.Present, new(GroupId, scope, ActorId), Revision) : new(Presence.Absent, null, Revision),
            state == State.OutstandingJoinRequest ? Request(scope) : new(Presence.Absent, null, Revision));

    private static GroupPersonalParticipationObservation<GroupJoinRequest> Request(GroupModuleScope scope,
        Guid? actor = null, Guid? group = null, DateTime? requestedAt = null) =>
        new(Presence.Present, new(group ?? GroupId, scope, actor ?? ActorId, requestedAt ?? Now.UtcDateTime.AddDays(-1)), Revision);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class Repository(GroupPersonalParticipationSnapshot snapshot) : IGroupPersonalParticipationReadRepository
    {
        public GroupPersonalParticipationSnapshot Snapshot { get; set; } = snapshot;
        public GroupPersonalParticipationQuery? Query { get; private set; }
        public GroupPersonalParticipationSnapshot? RevalidatedSnapshot { get; private set; }
        public CancellationToken RevalidationToken { get; private set; }
        public int RevalidationCount { get; private set; }
        public Decision Decision { get; init; } = Decision.Allowed;
        public Func<Func<Task<object?>>, Task<object?>>? Release { get; set; }

        public async Task<T?> WithAuthorizedReadAsync<T>(GroupPersonalParticipationQuery query,
            Func<GroupPersonalParticipationSnapshot, CancellationToken, Task<T?>> project,
            CancellationToken cancellationToken = default) where T : class
        {
            Query = query;
            if (Release is { } release) return (T?)await release(async () => await project(Snapshot, cancellationToken));
            return await project(Snapshot, cancellationToken);
        }

        public Task<Decision> RevalidateAsync(GroupPersonalParticipationQuery query,
            GroupPersonalParticipationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Query, query);
            RevalidatedSnapshot = snapshot;
            RevalidationToken = cancellationToken;
            RevalidationCount++;
            return Task.FromResult(Decision);
        }
    }
}
