using System.Reflection;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Places;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapAttractionDiscoveryServiceTests
{
    [Fact]
    public async Task VisibleCandidatesPreserveRepositoryOrderAndBoundedRequest()
    {
        var f = new Fixture();
        f.Verify = (_, _, _) => Task.FromResult(new MapAttractionVisibilityBatch(f.Owner,
            [new(f.Rows[2].AttractionId, MapAttractionVisibility.Visible),
             new(f.Rows[1].AttractionId, MapAttractionVisibility.Hidden),
             new(f.Rows[0].AttractionId, MapAttractionVisibility.Visible)]));
        var query = new MapAttractionDiscoveryQuery(17, 3);
        using var cts = new CancellationTokenSource();
        var result = await f.Service.DiscoverAsync(query, cts.Token);
        Assert.Equal(MapAttractionDiscoveryOutcome.Success, result.Outcome);
        Assert.Equal(new[] { f.Rows[0], f.Rows[2] }, result.Items);
        Assert.Equal(3, result.ScannedCount);
        Assert.Same(query, f.Query);
        Assert.Equal(cts.Token, f.RepositoryToken);
        Assert.Equal(cts.Token, f.VisibilityToken);
        Assert.Equal(f.Owner, f.RequestedOwner);
        Assert.Equal(f.Rows.Select(x => x.AttractionId), f.RequestedIds!);
        Assert.Equal((1, 1), (f.Reads, f.Verifications));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("undefined")]
    [InlineData("duplicate")]
    [InlineData("foreign-id")]
    [InlineData("wrong-owner")]
    [InlineData("empty-owner")]
    [InlineData("null-batch")]
    [InlineData("null-decision")]
    [InlineData("extra")]
    public async Task ContradictoryOrIncompleteEvidenceReturnsNoMetadata(string shape)
    {
        var f = new Fixture();
        f.Verify = (_, _, _) =>
        {
            var decisions = f.Rows.Select(x => new MapAttractionVisibilityDecision(x.AttractionId, MapAttractionVisibility.Visible)).ToList();
            var owner = f.Owner;
            switch (shape)
            {
                case "missing": decisions.RemoveAt(0); break;
                case "unknown": decisions[1] = new(f.Rows[1].AttractionId, MapAttractionVisibility.Unknown); break;
                case "undefined": decisions[1] = new(f.Rows[1].AttractionId, (MapAttractionVisibility)99); break;
                case "duplicate": decisions[1] = decisions[0]; break;
                case "foreign-id": decisions[1] = new(Guid.NewGuid(), MapAttractionVisibility.Visible); break;
                case "wrong-owner": owner = Guid.NewGuid(); break;
                case "empty-owner": owner = Guid.Empty; break;
                case "null-batch": return Task.FromResult<MapAttractionVisibilityBatch>(null!);
                case "null-decision": decisions[1] = null!; break;
                case "extra": decisions.Add(new(Guid.NewGuid(), MapAttractionVisibility.Visible)); break;
            }
            return Task.FromResult(new MapAttractionVisibilityBatch(owner, decisions));
        };
        AssertSafe(await f.Service.DiscoverAsync(new(0, 3)), MapAttractionDiscoveryOutcome.Unavailable);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("inactive")]
    [InlineData("social")]
    [InlineData("learning")]
    [InlineData("case")]
    [InlineData("empty")]
    public async Task InvalidActorNeverReadsDependencies(string shape)
    {
        var f = new Fixture();
        f.Current = Actor(shape, f.Owner);
        AssertSafe(await f.Service.DiscoverAsync(new(0, 3)), MapAttractionDiscoveryOutcome.Denied);
        Assert.Equal((0, 0), (f.Reads, f.Verifications));
    }

    [Theory]
    [InlineData(false, "inactive")]
    [InlineData(false, "social")]
    [InlineData(false, "foreign")]
    [InlineData(true, "inactive")]
    [InlineData(true, "social")]
    [InlineData(true, "foreign")]
    public async Task ScopeChangesDuringEitherReadDenyTheWholeWindow(bool duringVisibility, string shape)
    {
        var f = new Fixture();
        if (duringVisibility)
            f.Verify = (_, ids, _) => { f.Current = Actor(shape, f.Owner); return Task.FromResult(f.Visible(ids)); };
        else
            f.Read = (_, _) => { f.Current = Actor(shape, f.Owner); return Task.FromResult<IReadOnlyList<MapAttractionDiscoveryItem>>(f.Rows); };
        AssertSafe(await f.Service.DiscoverAsync(new(0, 3)), MapAttractionDiscoveryOutcome.Denied);
        Assert.Equal(duringVisibility ? 1 : 0, f.Verifications);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("visibility")]
    [InlineData("actor")]
    public async Task DependencyFailuresReturnOnlySafeUnavailableOutcome(string dependency)
    {
        var f = new Fixture();
        var error = new InvalidOperationException("private/photo/key account=secret provider=secret");
        if (dependency == "repository") f.Read = (_, _) => throw error;
        if (dependency == "visibility") f.Verify = (_, _, _) => throw error;
        if (dependency == "actor") f.ActorError = error;
        AssertSafe(await f.Service.DiscoverAsync(new(0, 3)), MapAttractionDiscoveryOutcome.Unavailable);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("repository")]
    [InlineData("visibility")]
    [InlineData("failure")]
    public async Task CancellationPropagatesWithoutMetadata(string stage)
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        if (stage == "before") cts.Cancel();
        if (stage == "repository") f.Read = (_, _) => { cts.Cancel(); return Task.FromResult<IReadOnlyList<MapAttractionDiscoveryItem>>(f.Rows); };
        if (stage == "visibility") f.Verify = (_, ids, _) => { cts.Cancel(); return Task.FromResult(f.Visible(ids)); };
        if (stage == "failure") f.Verify = (_, _, _) => { cts.Cancel(); throw new Exception("private dependency failure"); };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.DiscoverAsync(new(0, 3), cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        if (stage == "before") Assert.Equal((0, 0), (f.Reads, f.Verifications));
    }

    [Fact]
    public async Task CancellationInsideAwaitedFakeVerificationPropagates()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Verify = async (_, ids, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return f.Visible(ids); };
        var pending = f.Service.DiscoverAsync(new(0, 3), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty-id")]
    [InlineData("null-item")]
    [InlineData("latitude")]
    [InlineData("longitude")]
    [InlineData("name")]
    [InlineData("unbounded")]
    [InlineData("null-list")]
    public async Task InvalidRepositoryWindowsCannotBeAuthorized(string shape)
    {
        var f = new Fixture();
        switch (shape)
        {
            case "duplicate": f.Rows[1] = f.Rows[0]; break;
            case "empty-id": f.Rows[1] = new(Guid.Empty, "x", 0, 0); break;
            case "null-item": f.Rows[1] = null!; break;
            case "latitude": f.Rows[1] = new(Guid.NewGuid(), "x", double.NaN, 0); break;
            case "longitude": f.Rows[1] = new(Guid.NewGuid(), "x", 0, 181); break;
            case "name": f.Rows[1] = new(Guid.NewGuid(), " ", 0, 0); break;
            case "unbounded": f.Rows.Add(new(Guid.NewGuid(), "x", 0, 0)); break;
            case "null-list": f.Read = (_, _) => Task.FromResult<IReadOnlyList<MapAttractionDiscoveryItem>>(null!); break;
        }
        AssertSafe(await f.Service.DiscoverAsync(new(0, 3)), MapAttractionDiscoveryOutcome.Unavailable);
        Assert.Equal(0, f.Verifications);
    }

    [Fact]
    public async Task EmptyWindowAvoidsVisibilityReadAndDenyAllNeverReturnsMetadata()
    {
        var f = new Fixture();
        var denied = new MapAttractionDiscoveryService(f, new DenyAllMapAttractionDiscoveryVisibilityReader(), f);
        var hidden = await denied.DiscoverAsync(new(0, 3));
        Assert.Equal(MapAttractionDiscoveryOutcome.Success, hidden.Outcome);
        Assert.Empty(hidden.Items);
        Assert.Equal(3, hidden.ScannedCount);
        f.Rows.Clear();
        var empty = await f.Service.DiscoverAsync(new(0, 3));
        Assert.Equal(MapAttractionDiscoveryOutcome.Success, empty.Outcome);
        Assert.Equal(0, empty.ScannedCount);
        Assert.Empty(empty.Items);
        Assert.Equal(0, f.Verifications);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DenyAllMapAttractionDiscoveryVisibilityReader().ReadAsync(f.Owner, [], new(true)));
    }

    [Fact]
    public async Task EvidenceAndResultsDefensivelyCopyCollectionsAndExposeOnlyCanonicalItemFields()
    {
        var f = new Fixture();
        var source = f.Rows.Select(x => new MapAttractionVisibilityDecision(x.AttractionId, MapAttractionVisibility.Visible)).ToList();
        var batch = new MapAttractionVisibilityBatch(f.Owner, source);
        source.Clear();
        Assert.Equal(3, batch.Decisions.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<MapAttractionVisibilityDecision>)batch.Decisions).Clear());
        f.Verify = (_, _, _) => { f.Rows.Clear(); return Task.FromResult(batch); };
        var result = await f.Service.DiscoverAsync(new(0, 3));
        Assert.Equal(3, result.Items.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<MapAttractionDiscoveryItem>)result.Items).Clear());
        Assert.Equal(new[] { "AttractionId", "Latitude", "Longitude", "Name" },
            typeof(MapAttractionDiscoveryItem).GetProperties().Select(p => p.Name).Order());
        foreach (var type in new[] { typeof(MapAttractionVisibilityBatch), typeof(MapAttractionVisibilityDecision), typeof(MapAttractionDiscoveryResult) })
            Assert.All(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public async Task InvalidDependenciesAndNullQueryAreRejectedBeforeReading()
    {
        var f = new Fixture();
        Assert.Throws<ArgumentNullException>(() => new MapAttractionDiscoveryService(null!, f, f));
        Assert.Throws<ArgumentNullException>(() => new MapAttractionDiscoveryService(f, null!, f));
        Assert.Throws<ArgumentNullException>(() => new MapAttractionDiscoveryService(f, f, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Service.DiscoverAsync(null!));
        Assert.Equal((0, 0), (f.Reads, f.Verifications));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringScopeRecheckCannotReturnDeniedOrSuccessfulMetadata(int cancelOnRead)
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        f.ActorRead = () => { if (++calls == cancelOnRead) { cts.Cancel(); f.Current = null; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.DiscoverAsync(new(0, 3), cts.Token));
        Assert.Equal(cancelOnRead == 2 ? 0 : 1, f.Verifications);
    }
    private static GalleryActor? Actor(string shape, Guid owner) => shape switch
    {
        "null" => null,
        "inactive" => new(owner, "Hobbies", false),
        "social" => new(owner, "Social", true),
        "learning" => new(owner, "Learning", true),
        "case" => new(owner, "hobbies", true),
        "empty" => new(Guid.Empty, "Hobbies", true),
        _ => new(Guid.NewGuid(), "Hobbies", true)
    };

    private static void AssertSafe(MapAttractionDiscoveryResult result, MapAttractionDiscoveryOutcome expected)
    {
        Assert.Equal(expected, result.Outcome);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(new[] { "Items", "Outcome", "ScannedCount" }, typeof(MapAttractionDiscoveryResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic).Select(p => p.Name).Order());
    }

    private sealed class Fixture : IMapAttractionRepository, IMapAttractionDiscoveryVisibilityReader, IGalleryActorContext
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public GalleryActor? Current { get; set; }
        public Exception? ActorError { get; set; }
        public Action? ActorRead { get; set; }
        GalleryActor? IGalleryActorContext.Current { get { ActorRead?.Invoke(); return ActorError is null ? Current : throw ActorError; } }
        public List<MapAttractionDiscoveryItem> Rows { get; } = Enumerable.Range(1, 3)
            .Select(i => new MapAttractionDiscoveryItem(new Guid($"00000000-0000-4000-8000-{i:D12}"), "Synthetic canonical", i, i)).ToList();
        public Func<MapAttractionDiscoveryQuery, CancellationToken, Task<IReadOnlyList<MapAttractionDiscoveryItem>>> Read { get; set; }
        public Func<Guid, IReadOnlyList<Guid>, CancellationToken, Task<MapAttractionVisibilityBatch>> Verify { get; set; }
        public int Reads { get; private set; }
        public int Verifications { get; private set; }
        public MapAttractionDiscoveryQuery? Query { get; private set; }
        public CancellationToken RepositoryToken { get; private set; }
        public CancellationToken VisibilityToken { get; private set; }
        public Guid RequestedOwner { get; private set; }
        public IReadOnlyList<Guid>? RequestedIds { get; private set; }
        public MapAttractionDiscoveryService Service => new(this, this, this);
        public Fixture()
        {
            Current = new(Owner, "Hobbies", true);
            Read = (_, _) => Task.FromResult<IReadOnlyList<MapAttractionDiscoveryItem>>(Rows);
            Verify = (_, ids, _) => Task.FromResult(Visible(ids));
        }
        public MapAttractionVisibilityBatch Visible(IEnumerable<Guid> ids) => new(Owner,
            ids.Select(id => new MapAttractionVisibilityDecision(id, MapAttractionVisibility.Visible)));
        public Task<IReadOnlyList<MapAttractionDiscoveryItem>> DiscoverAsync(MapAttractionDiscoveryQuery query, CancellationToken token = default)
        { Reads++; Query = query; RepositoryToken = token; return Read(query, token); }
        public Task<MapAttractionVisibilityBatch> ReadAsync(Guid owner, IReadOnlyList<Guid> ids, CancellationToken token = default)
        { Verifications++; RequestedOwner = owner; RequestedIds = ids; VisibilityToken = token; return Verify(owner, ids, token); }
        public Task AddAsync(MapAttraction attraction, CancellationToken token = default) => throw new NotSupportedException();
        public Task<MapAttraction?> FindByIdAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
    }
}
