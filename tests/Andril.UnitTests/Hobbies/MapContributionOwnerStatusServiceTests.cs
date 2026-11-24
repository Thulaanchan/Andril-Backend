using System.Reflection;
using System.Text.Json;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionOwnerStatusServiceTests
{
    [Fact]
    public async Task OwnedPendingContributionReturnsOnlyAllowlistedStatusMetadata()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var result = Assert.IsType<MapContributionOwnerStatus>(await f.Read(cancellation.Token));
        Assert.Equal(f.Stored!.ContributionId, result.ContributionId);
        Assert.Equal(MapContributionModerationStatus.PendingReview, result.Status);
        Assert.Equal((f.Stored.ContributionId, f.Owner), Assert.Single(f.Requests));
        Assert.Equal(cancellation.Token, f.Token);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(new[] { "ContributionId", "Status" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain(Fixture.PrivateKey, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(f.Owner.ToString(), JsonSerializer.Serialize(result));
        Assert.All(typeof(MapContributionOwnerStatus).GetProperties(), p => Assert.DoesNotContain(p.Name,
            new[] { "OwnerHobbiesModuleProfileId", "LocationEvidence", "Description", "PrivatePhotoStorageObjectKey" }));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("empty-owner")]
    [InlineData("Learning")]
    [InlineData("Social")]
    [InlineData("hobbies")]
    [InlineData("")]
    [InlineData("null-module")]
    public async Task InvalidActorCannotQueryContributions(string invalid)
    {
        var f = new Fixture();
        f.Actor = invalid switch
        {
            "missing" => null,
            "inactive" => f.Actor! with { IsActive = false },
            "empty-owner" => f.Actor! with { ModuleProfileId = Guid.Empty },
            "null-module" => f.Actor! with { Module = null! },
            _ => f.Actor! with { Module = invalid }
        };
        Assert.Null(await f.Read());
        Assert.Empty(f.Requests);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign-owner")]
    [InlineData("different-contribution")]
    [InlineData("unsupported-status")]
    public async Task MissingForeignOrUnsupportedRepositoryResultsHaveIdenticalDenial(string invalid)
    {
        var f = new Fixture();
        f.Stored = invalid switch
        {
            "missing" => null,
            "foreign-owner" => Create(f.Id, Guid.NewGuid()),
            "different-contribution" => Create(Guid.NewGuid(), f.Owner),
            _ => f.Stored
        };
        if (invalid == "unsupported-status")
            typeof(MapContribution).GetField("<ModerationStatus>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(f.Stored, (MapContributionModerationStatus)99);
        var result = await f.Read();
        Assert.Null(result);
        Assert.Equal("null", JsonSerializer.Serialize(result));
        Assert.Equal((f.Id, f.Owner), Assert.Single(f.Requests));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task EmptyContributionIdDoesNotReadActorOrRepository()
    {
        var f = new Fixture { FailActor = true };
        Assert.Null(await new MapContributionOwnerStatusService(f, f).GetMineAsync(Guid.Empty));
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("owner-changed")]
    [InlineData("module-changed")]
    public async Task ActorScopeChangeDuringReadDiscardsPreviouslyAuthorizedMetadata(string change)
    {
        var f = new Fixture();
        f.AfterRead = () => f.Actor = change switch
        {
            "revoked" => f.Actor! with { IsActive = false },
            "owner-changed" => f.Actor! with { ModuleProfileId = Guid.NewGuid() },
            _ => f.Actor! with { Module = "Social" }
        };
        Assert.Null(await f.Read());
        Assert.Single(f.Requests);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeOrAfterRepositoryReadPropagates(bool afterRead)
    {
        using var cancellation = new CancellationTokenSource();
        var f = new Fixture();
        if (afterRead) f.AfterRead = cancellation.Cancel;
        else cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Read(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(afterRead ? 1 : 0, f.Requests.Count);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task CancellationDuringReadReachesDependencyWithoutSuccessfulProjection()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reader = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return f.Stored;
        };
        var read = f.Read(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancellation.Token, f.Token);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationDuringActorResolutionWinsOverConcurrentScopeDenial(int cancelOnRead)
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        f.OnActorRead = () =>
        {
            if (++reads != cancelOnRead) return;
            f.Actor = null;
            cancellation.Cancel();
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Read(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancelOnRead - 1, f.Requests.Count);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyFailuresReturnOnlyGenericExceptionWithoutPrivateDetails(bool actorFailure)
    {
        var f = new Fixture { FailActor = actorFailure };
        f.Reader = _ => throw new IOException("sensitive " + Fixture.PrivateKey + f.Owner);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Read());
        Assert.Equal("Contribution status retrieval is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Fixture.PrivateKey, error.ToString());
        Assert.DoesNotContain(f.Owner.ToString(), error.ToString());
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task EveryReadUsesCurrentOwnerScopeRatherThanCachedAuthorization()
    {
        var f = new Fixture();
        var service = new MapContributionOwnerStatusService(f, f);
        Assert.NotNull(await service.GetMineAsync(f.Id));
        f.Actor = f.Actor! with { ModuleProfileId = Guid.NewGuid() };
        // A defective test repository returns the original owner's entity; the service must still deny it.
        Assert.Null(await service.GetMineAsync(f.Id));
        Assert.Equal(f.Actor.ModuleProfileId, f.Requests[1].Owner);
        Assert.Equal(0, f.Writes);
    }

    private static MapContribution Create(Guid id, Guid owner) => MapContribution.Create(id, owner, Guid.NewGuid(),
        Guid.NewGuid(), Fixture.PrivateKey, "private description", new MapContributionLocationEvidence(
            7, 80, 7, 80, 5, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

    private sealed class Fixture : IMapContributionRepository, IGalleryActorContext
    {
        internal const string PrivateKey = "private/status-sensitive-photo";
        internal Guid Owner { get; } = Guid.NewGuid();
        internal Guid Id { get; } = Guid.NewGuid();
        internal MapContribution? Stored { get; set; }
        internal GalleryActor? Actor { get; set; }
        internal bool FailActor { get; init; }
        internal Action? AfterRead { get; set; }
        internal Action? OnActorRead { get; set; }
        internal Func<CancellationToken, Task<MapContribution?>>? Reader { get; set; }
        internal List<(Guid Id, Guid Owner)> Requests { get; } = [];
        internal CancellationToken Token { get; private set; }
        internal int Writes { get; private set; }
        internal Fixture() { Stored = Create(Id, Owner); Actor = new(Owner, "Hobbies", true); }
        public GalleryActor? Current
        {
            get
            {
                OnActorRead?.Invoke();
                return FailActor ? throw new IOException("sensitive " + PrivateKey) : Actor;
            }
        }
        internal Task<MapContributionOwnerStatus?> Read(CancellationToken token = default) =>
            new MapContributionOwnerStatusService(this, this).GetMineAsync(Id, token);
        public async Task<MapContribution?> FindOwnedAsync(Guid contributionId, Guid ownerHobbiesModuleProfileId,
            CancellationToken cancellationToken)
        {
            Requests.Add((contributionId, ownerHobbiesModuleProfileId));
            Token = cancellationToken;
            var found = Reader is null ? Stored : await Reader(cancellationToken);
            AfterRead?.Invoke();
            return found;
        }
        public Task AddAsync(MapContribution contribution, CancellationToken cancellationToken)
        { Writes++; throw new InvalidOperationException("Status reads must never write."); }
    }
}
