using System.Text.Json;
using Andril.API.Authorization.Groups;
using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;
using Decision = Andril.Groups.Interfaces.GroupPersonalParticipationDecision;
using Presence = Andril.Groups.Interfaces.GroupPersonalParticipationPresence;

namespace Andril.SecurityTests.Groups;

public sealed class GroupPersonalParticipationAccessServiceTests
{
    private const string PrivateRevision = "private-read-revision";
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly GroupModuleScope Zone = new("social", GroupKind.Zone);

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task SelfStatusUsesTrustedActorAndOnlySerializesImmutablePublicFields(string module, GroupKind kind)
    {
        var scope = new GroupModuleScope(module, kind);
        var actors = new ActorContext((_, _) => Actor(module));
        var repository = new Repository();
        using var cancellation = new CancellationTokenSource();
        var result = await Access(actors, repository).GetStatusAsync(scope, GroupId, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal(GroupId, result.GroupId);
        Assert.Equal(module, result.Module);
        Assert.Equal(kind, result.GroupKind);
        Assert.Equal(GroupPersonalParticipationState.Member, result.State);
        Assert.Equal(new GroupPersonalParticipationQuery(ActorId, GroupId, scope), repository.Query);
        Assert.Equal(new[] { module, module }, actors.Modules);
        Assert.All(actors.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Equal(cancellation.Token, repository.Token);

        var json = JsonSerializer.Serialize(result);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "GroupId", "GroupKind", "Module", "State" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ActorId.ToString(), json);
        Assert.DoesNotContain(PrivateRevision, json);
        Assert.DoesNotContain(ActorId.ToString(), result.ToString());
        Assert.All(typeof(GroupPersonalParticipationDto).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public async Task ProjectionRejectsDifferentGroupOrModule()
    {
        var application = new GroupPersonalParticipationService(new Repository(), new Clock());
        var result = await application.GetStatusAsync(Actor(Zone.Module), Zone, GroupId);

        Assert.NotNull(result);
        Assert.Throws<InvalidOperationException>(() =>
            GroupPersonalParticipationDto.FromAuthorizedResult(result, Zone, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => GroupPersonalParticipationDto.FromAuthorizedResult(result,
            new GroupModuleScope("learning", GroupKind.Classroom), GroupId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cross-module")]
    [InlineData("underage")]
    [InlineData("stale")]
    [InlineData("restricted")]
    public async Task InvalidInitialActorCannotReachTheRepository(string invalid)
    {
        var actor = Actor(Zone.Module);
        var actors = new ActorContext((_, _) => invalid switch
        {
            "missing" => null,
            "cross-module" => actor with { Module = "hobbies" },
            "underage" => actor with { VerifiedAge = 17 },
            "stale" => actor with { EvidenceTimestampUtc = Now.AddMinutes(-10) },
            "restricted" => actor with { IsRestricted = true },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        });
        var repository = new Repository();

        Assert.Null(await Access(actors, repository).GetStatusAsync(Zone, GroupId));
        Assert.Null(repository.Query);
        Assert.Equal(Zone.Module, Assert.Single(actors.Modules));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("substituted")]
    [InlineData("cross-module")]
    [InlineData("stale")]
    [InlineData("inactive")]
    [InlineData("adult-evidence-revoked")]
    public async Task ChangedTrustedActorSuppressesAnAlreadyVerifiedResult(string change)
    {
        var actor = Actor(Zone.Module);
        var actors = new ActorContext((read, _) => read == 1 ? actor : change switch
        {
            "revoked" => null,
            "substituted" => actor with { ProfileId = Guid.NewGuid() },
            "cross-module" => actor with { Module = "learning" },
            "stale" => actor with { EvidenceTimestampUtc = Now.AddMinutes(-10) },
            "inactive" => actor with { IsActive = false },
            "adult-evidence-revoked" => actor with { VerifiedAge = null },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        });
        var repository = new Repository();

        Assert.Null(await Access(actors, repository).GetStatusAsync(Zone, GroupId));
        Assert.Equal(new[] { Zone.Module, Zone.Module }, actors.Modules);
        Assert.Equal(1, repository.Revalidations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOrRevokedRepositoryEvidenceIsNotReleased(bool stale)
    {
        var actors = new ActorContext((_, _) => Actor(Zone.Module));
        var repository = new Repository { Stale = stale, Decision = Decision.Denied };

        Assert.Null(await Access(actors, repository).GetStatusAsync(Zone, GroupId));
        Assert.Equal(Zone.Module, Assert.Single(actors.Modules));
        Assert.Equal(stale ? 0 : 1, repository.Revalidations);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("repository")]
    [InlineData("revalidation")]
    [InlineData("actor-before-release")]
    public async Task UnavailableAuthorityFailsClosed(string unavailable)
    {
        var actors = new ActorContext((read, _) =>
            unavailable == "actor" || (unavailable == "actor-before-release" && read == 2)
                ? throw new InvalidOperationException("private-actor-error") : Actor(Zone.Module));
        var repository = new Repository
        {
            ThrowOnRead = unavailable == "repository",
            ThrowOnRevalidation = unavailable == "revalidation"
        };

        Assert.Null(await Access(actors, repository).GetStatusAsync(Zone, GroupId));
        Assert.Equal(unavailable == "actor-before-release" ? 2 : 1, actors.Modules.Count);
        Assert.Equal(unavailable is "revalidation" or "actor-before-release" ? 1 : 0, repository.Revalidations);
    }

    [Fact]
    public async Task CancellationBeforeReadingAndDuringTheFinalActorCheckPropagates()
    {
        using var before = new CancellationTokenSource();
        using var during = new CancellationTokenSource();
        before.Cancel();
        var actors = new ActorContext((read, _) =>
        {
            if (read == 2) during.Cancel();
            return Actor(Zone.Module);
        });
        var repository = new Repository();
        var service = Access(actors, repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetStatusAsync(Zone, GroupId, before.Token));
        Assert.Empty(actors.Modules);
        Assert.Null(repository.Query);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetStatusAsync(Zone, GroupId, during.Token));
        Assert.Equal(1, repository.Revalidations);
        Assert.Equal(2, actors.Modules.Count);
    }

    private static GroupPersonalParticipationAccessService Access(ActorContext actors, Repository repository)
    {
        var clock = new Clock();
        return new GroupPersonalParticipationAccessService(actors, new GroupPersonalParticipationService(repository, clock), clock);
    }

    private static AuthoritativeGroupProfileEvidence Actor(string module) => new(ActorId, module, true, false, 25, Now);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ActorContext(Func<int, string, AuthoritativeGroupProfileEvidence?> read) : IGroupActorContext
    {
        public List<string> Modules { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string module, CancellationToken cancellationToken = default)
        {
            Modules.Add(module);
            Tokens.Add(cancellationToken);
            return Task.FromResult(read(Modules.Count, module));
        }
    }

    private sealed class Repository : IGroupPersonalParticipationReadRepository
    {
        public GroupPersonalParticipationQuery? Query { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Revalidations { get; private set; }
        public bool Stale { get; init; }
        public bool ThrowOnRead { get; init; }
        public bool ThrowOnRevalidation { get; init; }
        public Decision Decision { get; init; } = Decision.Allowed;

        public Task<T?> WithAuthorizedReadAsync<T>(GroupPersonalParticipationQuery query,
            Func<GroupPersonalParticipationSnapshot, CancellationToken, Task<T?>> project,
            CancellationToken cancellationToken = default) where T : class
        {
            Query = query;
            Token = cancellationToken;
            if (ThrowOnRead) throw new InvalidOperationException("private-read-error");
            return project(new GroupPersonalParticipationSnapshot(query.ActorProfileId, query.GroupId, query.Scope,
                Decision.Allowed, Decision.Allowed, Decision.Allowed, Stale ? Now.AddMinutes(-10) : Now, PrivateRevision,
                new(Presence.Present, new(query.GroupId, query.Scope, query.ActorProfileId), PrivateRevision),
                new(Presence.Absent, null, PrivateRevision)), cancellationToken);
        }

        public Task<Decision> RevalidateAsync(GroupPersonalParticipationQuery query,
            GroupPersonalParticipationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Revalidations++;
            if (ThrowOnRevalidation) throw new InvalidOperationException("private-revalidation-error");
            return Task.FromResult(Decision);
        }
    }
}
