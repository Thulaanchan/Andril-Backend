using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupDiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActorId = Guid.NewGuid();
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static GroupModuleScope Scope(string module) => new(module, module switch
        { "learning" => GroupKind.Classroom, "hobbies" => GroupKind.Club, _ => GroupKind.Zone });
    private static AuthoritativeGroupProfileEvidence Actor(string module) => new(ActorId, module, true, false, 25, Now);
    private static GroupDiscoveryEvidence Evidence(GroupModuleScope scope) => new(new(Guid.NewGuid(), scope, "Group"),
        GroupDiscoveryLifecycle.Active, GroupDiscoveryDecision.Allowed, GroupDiscoveryPrivacy.Public,
        GroupDiscoveryDecision.Unknown, GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed,
        GroupDiscoveryDecision.Allowed, "r1", Now);

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task AuthorizedModules_ReturnExactScopedResultsAndForwardBoundQuery(string module)
    {
        var scope = Scope(module);
        var row = Evidence(scope);
        var repository = new RepositoryDouble([row]);
        var result = await new GroupDiscoveryService(repository, new Clock()).DiscoverAsync(Actor(module), scope, "explicit search", 3);
        Assert.NotNull(result);
        Assert.Same(row.Group, Assert.Single(result.Groups));
        Assert.Equal(scope, result.Scope);
        Assert.Equal(ActorId, result.ActorProfileId);
        Assert.Equal(new(ActorId, scope, "explicit search", 3), repository.Query);
        Assert.Equal(1, repository.Rechecks);
        Assert.Equal("{}", JsonSerializer.Serialize(result));
        Assert.Throws<NotSupportedException>(() => ((IList<GroupEntry>)result.Groups).Clear());
    }

    [Theory]
    [InlineData("inactive")][InlineData("closed")][InlineData("unknown-lifecycle")]
    [InlineData("discoverability")][InlineData("visibility")][InlineData("membership")]
    [InlineData("blocked")][InlineData("unknown-privacy")][InlineData("private")]
    [InlineData("stale")][InlineData("future")][InlineData("revision")][InlineData("foreign")]
    public async Task MissingOrDeniedEvidence_DoesNotExposeGroup(string change)
    {
        var scope = Scope("social");
        var row = Evidence(scope);
        row = change switch
        {
            "inactive" => row with { Lifecycle = GroupDiscoveryLifecycle.Inactive },
            "closed" => row with { Lifecycle = GroupDiscoveryLifecycle.Closed },
            "unknown-lifecycle" => row with { Lifecycle = (GroupDiscoveryLifecycle)99 },
            "discoverability" => row with { Discoverability = GroupDiscoveryDecision.Unknown },
            "visibility" => row with { Visibility = GroupDiscoveryDecision.Denied },
            "membership" => row with { MembershipRestrictions = GroupDiscoveryDecision.Unknown },
            "blocked" => row with { BlockingRestrictions = GroupDiscoveryDecision.Denied },
            "unknown-privacy" => row with { Privacy = GroupDiscoveryPrivacy.Unknown },
            "private" => row with { Privacy = GroupDiscoveryPrivacy.Private },
            "stale" => row with { EvidenceTimestampUtc = Now.AddMinutes(-6) },
            "future" => row with { EvidenceTimestampUtc = Now.AddSeconds(1) },
            "revision" => row with { Revision = "" },
            _ => row with { Group = new(row.Group.Id, Scope("learning"), "Group") }
        };
        var result = await new GroupDiscoveryService(new RepositoryDouble([row]), new Clock())
            .DiscoverAsync(Actor("social"), scope, "", 1);
        Assert.NotNull(result);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task ExplicitPrivatePermissionAndEquivalentDuplicates_AreSupported()
    {
        var scope = Scope("hobbies");
        var row = Evidence(scope) with { Privacy = GroupDiscoveryPrivacy.Private, PrivateDiscoveryAuthorization = GroupDiscoveryDecision.Allowed };
        var clone = row with { Group = new(row.Group.Id, new("hobbies", GroupKind.Club), row.Group.Name) };
        var result = await new GroupDiscoveryService(new RepositoryDouble([row, clone]), new Clock()).DiscoverAsync(Actor("hobbies"), scope, "", 2);
        Assert.Single(result!.Groups);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Contradictions_AreOrderIndependentAndCannotRestoreEligibility(bool reverse)
    {
        var scope = Scope("social");
        var row = Evidence(scope);
        var denied = row with { BlockingRestrictions = GroupDiscoveryDecision.Denied };
        var rows = reverse ? new[] { denied, row, denied } : new[] { row, denied, row };
        var result = await new GroupDiscoveryService(new RepositoryDouble(rows), new Clock()).DiscoverAsync(Actor("social"), scope, "", 3);
        Assert.Empty(result!.Groups);
    }

    [Fact]
    public async Task BoundsBindingsAndCurrentAuthorization_FailClosed()
    {
        var scope = Scope("social");
        var repository = new RepositoryDouble([Evidence(scope), Evidence(scope)]);
        var service = new GroupDiscoveryService(repository, new Clock());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DiscoverAsync(Actor("social"), scope, "", 0));
        Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));
        repository.Rows = [Evidence(scope)];
        foreach (var decision in new[] { GroupDiscoveryDecision.Unknown, GroupDiscoveryDecision.Denied, (GroupDiscoveryDecision)99 })
        {
            repository.Decision = decision;
            Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));
        }
        repository.Decision = GroupDiscoveryDecision.Allowed;
        repository.ForeignActor = true;
        Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));
        repository.ForeignActor = false;
        repository.SuppressRelease = true;
        Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));
    }

    [Fact]
    public async Task IneligibleActors_NeverInvokeRepositoryAndCancellationPropagates()
    {
        var scope = Scope("social");
        var repository = new RepositoryDouble([Evidence(scope)]);
        var service = new GroupDiscoveryService(repository, new Clock());
        foreach (var actor in new[] { Actor("learning"), Actor("social") with { ProfileId = Guid.Empty },
                     Actor("social") with { IsActive = false }, Actor("social") with { IsRestricted = true },
                     Actor("social") with { VerifiedAge = null }, Actor("social") with { VerifiedAge = 17 },
                     Actor("social") with { EvidenceTimestampUtc = Now.AddMinutes(-6) } })
            Assert.Null(await service.DiscoverAsync(actor, scope, "", 1));
        Assert.Null(repository.Query);
        using var cancellation = new CancellationTokenSource();
        repository.OnRecheck = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(Actor("social"), scope, "", 1, cancellation.Token));
        Assert.Equal(cancellation.Token, repository.Token);
        repository.OnRecheck = () => throw new InvalidOperationException("private-marker");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(Actor("social"), scope, "", 1));
    }

    [Fact]
    public async Task MissingOrLateProjection_CannotReleasePreviouslyAuthorizedResult()
    {
        var scope = Scope("learning");
        var repository = new RepositoryDouble([Evidence(scope)]);
        var service = new GroupDiscoveryService(repository, new Clock());
        var previous = await service.DiscoverAsync(Actor("learning"), scope, "", 1);
        Assert.NotNull(previous);

        Func<Task<object?>>? invokeLate = null;
        repository.Release = project =>
        {
            invokeLate = project;
            return Task.FromResult<object?>(previous);
        };

        Assert.Null(await service.DiscoverAsync(Actor("learning"), scope, "", 1));
        Assert.NotNull(invokeLate);
        Assert.Null(await invokeLate());
        Assert.Equal(1, repository.Rechecks);
    }

    [Fact]
    public async Task SubstitutedProjection_IsRejectedEvenWhenActorScopeAndGroupsMatch()
    {
        var scope = Scope("hobbies");
        var repository = new RepositoryDouble([Evidence(scope)]);
        var service = new GroupDiscoveryService(repository, new Clock());
        var previous = await service.DiscoverAsync(Actor("hobbies"), scope, "", 1);
        Assert.NotNull(previous);
        repository.Release = async project =>
        {
            var current = Assert.IsType<AuthorizedGroupDiscoveryResult>(await project());
            Assert.NotSame(previous, current);
            Assert.Equal(previous.ActorProfileId, current.ActorProfileId);
            Assert.Equal(previous.Scope, current.Scope);
            Assert.Equal(previous.Groups, current.Groups);
            return previous;
        };

        Assert.Null(await service.DiscoverAsync(Actor("hobbies"), scope, "", 1));
        Assert.Equal(2, repository.Rechecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedProjection_InvalidatesFirstResult(bool concurrent)
    {
        var scope = Scope("social");
        var repository = new RepositoryDouble([Evidence(scope)]);
        var finishRecheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (concurrent) repository.WaitDuringRecheck = () => finishRecheck.Task;
        repository.Release = async project =>
        {
            var first = project();
            Assert.Equal(!concurrent, first.IsCompleted);
            try
            {
                Assert.Null(await project().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                finishRecheck.SetResult();
            }

            return Assert.IsType<AuthorizedGroupDiscoveryResult>(await first);
        };

        var service = new GroupDiscoveryService(repository, new Clock());
        Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));
        Assert.Equal(1, repository.Rechecks);
    }

    [Fact]
    public async Task RepositoryRevocationSuppressesVerifiedProjectionAndCallerCancellationTakesPriority()
    {
        var scope = Scope("social");
        var repository = new RepositoryDouble([Evidence(scope)]);
        var service = new GroupDiscoveryService(repository, new Clock());
        using var cancellation = new CancellationTokenSource();
        repository.Release = async project =>
        {
            Assert.IsType<AuthorizedGroupDiscoveryResult>(await project());
            return null;
        };
        Assert.Null(await service.DiscoverAsync(Actor("social"), scope, "", 1));

        repository.Release = async project =>
        {
            var result = Assert.IsType<AuthorizedGroupDiscoveryResult>(await project());
            cancellation.Cancel();
            return result;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DiscoverAsync(Actor("social"), scope, "", 1, cancellation.Token));
    }

    private sealed class RepositoryDouble(IReadOnlyList<GroupDiscoveryEvidence> rows) : IGroupDiscoveryReadRepository
    {
        public IReadOnlyList<GroupDiscoveryEvidence> Rows = rows;
        public GroupDiscoveryQuery? Query;
        public CancellationToken Token;
        public int Rechecks;
        public bool ForeignActor, SuppressRelease;
        public Action? OnRecheck;
        public Func<Task>? WaitDuringRecheck;
        public Func<Func<Task<object?>>, Task<object?>>? Release;
        public GroupDiscoveryDecision Decision = GroupDiscoveryDecision.Allowed;
        public async Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
            Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class
        {
            Query = query;
            var snapshot = new GroupDiscoverySnapshot(
                ForeignActor ? Guid.NewGuid() : query.ActorProfileId, query.Scope, Rows, "a1");
            if (Release is { } release) return (T?)await release(async () => await project(snapshot, cancellationToken));
            var result = await project(snapshot, cancellationToken);
            return SuppressRelease ? null : result;
        }

        public async Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query,
            GroupDiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Rechecks++;
            Token = cancellationToken;
            OnRecheck?.Invoke();
            if (WaitDuringRecheck is { } wait) await wait();
            return Decision;
        }
    }
}
