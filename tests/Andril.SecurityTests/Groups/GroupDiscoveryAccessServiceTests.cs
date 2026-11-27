using System.Text.Json;
using Andril.API.Authorization.Groups;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.SecurityTests.Groups;

public sealed class GroupDiscoveryAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private static AuthoritativeGroupProfileEvidence Actor(string module) => new(Guid.NewGuid(), module, true, false, 25, Now);
    private static GroupModuleScope Scope(string module) => new(module, module switch
        { "learning" => GroupKind.Classroom, "hobbies" => GroupKind.Club, _ => GroupKind.Zone });
    private static GroupDiscoveryAccessService Service(ActorDouble actors, RepositoryDouble repository)
        => new(actors, new GroupDiscoveryService(repository, new Clock()), new Clock());

    [Theory]
    [InlineData("learning")][InlineData("hobbies")][InlineData("social")]
    public async Task ValidActor_IsTrustedScopedAndRevalidatedBeforeSafeProjection(string module)
    {
        var actor = Actor(module);
        var actors = new ActorDouble(actor);
        var repository = new RepositoryDouble();
        var result = await Service(actors, repository).DiscoverAsync(Scope(module), "explicit", 2);
        Assert.NotNull(result);
        Assert.Equal("Group", Assert.Single(result).Name);
        Assert.Equal(actor.ProfileId, repository.Query!.ActorProfileId);
        Assert.Equal(Scope(module), repository.Query.Scope);
        Assert.Equal(2, actors.Calls);
        Assert.Equal(new[] { module, module }, actors.Modules);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(actor.ProfileId.ToString(), json);
        Assert.DoesNotContain("hidden-revision-marker", json);
        var method = typeof(GroupDiscoveryAccessService).GetMethod(nameof(GroupDiscoveryAccessService.DiscoverAsync))!;
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(Guid));
    }

    [Theory]
    [InlineData("missing")][InlineData("denied")][InlineData("inactive")][InlineData("restricted")]
    [InlineData("stale")][InlineData("future")][InlineData("foreign")][InlineData("underage")][InlineData("unknown-age")]
    public async Task IneligibleActors_NeverReadPrivateGroups(string condition)
    {
        var actor = Actor("social");
        var actors = new ActorDouble(condition switch
        {
            "missing" => null,
            "inactive" => actor with { IsActive = false },
            "restricted" => actor with { IsRestricted = true },
            "stale" => actor with { EvidenceTimestampUtc = Now.AddMinutes(-6) },
            "future" => actor with { EvidenceTimestampUtc = Now.AddSeconds(1) },
            "foreign" => actor with { Module = "learning" },
            "underage" => actor with { VerifiedAge = 17 },
            "unknown-age" => actor with { VerifiedAge = null },
            _ => actor
        }) { Throw = condition == "denied" };
        var repository = new RepositoryDouble();
        Assert.Null(await Service(actors, repository).DiscoverAsync(Scope("social"), "", 1));
        Assert.Null(repository.Query);
    }

    [Theory]
    [InlineData("revoked")][InlineData("substituted")][InlineData("foreign")][InlineData("missing")]
    public async Task ActorChangesDuringDiscovery_SuppressEveryProjectedResult(string condition)
    {
        var actor = Actor("social");
        var actors = new ActorDouble(actor);
        var repository = new RepositoryDouble { AfterRead = () => actors.Current = condition switch
        {
            "revoked" => actor with { IsRestricted = true },
            "substituted" => actor with { ProfileId = Guid.NewGuid() },
            "foreign" => actor with { Module = "learning" },
            _ => null
        }};
        Assert.Null(await Service(actors, repository).DiscoverAsync(Scope("social"), "", 1));
        Assert.Equal(2, actors.Calls);
    }

    [Theory]
    [InlineData("failure")][InlineData("foreign-binding")][InlineData("unknown-current")]
    public async Task RepositoryErrorsAndUnauthorizedBindings_NeverReleaseConfidentialResults(string condition)
    {
        var repository = new RepositoryDouble { Failure = condition == "failure", ForeignBinding = condition == "foreign-binding",
            Decision = condition == "unknown-current" ? GroupDiscoveryDecision.Unknown : GroupDiscoveryDecision.Allowed };
        Assert.Null(await Service(new(Actor("social")), repository).DiscoverAsync(Scope("social"), "", 1));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringDiscovery_PropagatesWithoutPartialCards()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new RepositoryDouble { AfterRead = cancellation.Cancel };
        var service = Service(new(Actor("social")), repository);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(Scope("social"), "", 1, cancellation.Token));
        Assert.Equal(cancellation.Token, repository.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(Scope("social"), "", 1, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => new GroupDiscoveryAccessService(null!, new GroupDiscoveryService(repository)));
        Assert.Throws<ArgumentNullException>(() => new GroupDiscoveryAccessService(new ActorDouble(null), null!));
    }

    private sealed class ActorDouble(AuthoritativeGroupProfileEvidence? actor) : IGroupActorContext
    {
        public AuthoritativeGroupProfileEvidence? Current = actor;
        public bool Throw;
        public int Calls;
        public List<string> Modules = [];
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string expectedModule, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Modules.Add(expectedModule);
            if (Throw) throw new InvalidOperationException("hidden-revision-marker"); return Task.FromResult(Current); }
    }

    private sealed class RepositoryDouble : IGroupDiscoveryReadRepository
    {
        public GroupDiscoveryQuery? Query;
        public CancellationToken Token;
        public bool Failure, ForeignBinding;
        public Action? AfterRead;
        public GroupDiscoveryDecision Decision = GroupDiscoveryDecision.Allowed;
        public Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
            Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class
        {
            Query = query;
            if (Failure) throw new InvalidOperationException("hidden-revision-marker");
            var evidence = new GroupDiscoveryEvidence(new(Guid.NewGuid(), query.Scope, "Group"), GroupDiscoveryLifecycle.Active,
                GroupDiscoveryDecision.Allowed, GroupDiscoveryPrivacy.Public, GroupDiscoveryDecision.Unknown,
                GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed, "hidden-revision-marker", Now);
            return project(new(ForeignBinding ? Guid.NewGuid() : query.ActorProfileId, query.Scope, [evidence], "hidden-revision-marker"), cancellationToken);
        }
        public Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query, GroupDiscoverySnapshot snapshot,
            CancellationToken cancellationToken = default)
        { Token = cancellationToken; AfterRead?.Invoke(); return Task.FromResult(Decision); }
    }
}
