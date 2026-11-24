using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class MapAttractionPersistenceTests
{
    [MapCapturePostgresFact]
    public Task DiscoveryService_PagesOnlyExplicitlyVisibleCanonicalMetadataWithoutReadingPrivateEvidence() => WithDatabaseAsync(async connection =>
    {
        await using var context = CreateContext(connection);
        var owner = Guid.NewGuid();
        var ids = Enumerable.Range(1, 5).Select(i => new Guid($"00000000-0000-4000-8000-{i:D12}")).ToArray();
        context.MapAttractions.AddRange(ids.Reverse().Select(id => MapAttraction.Create(id, "Synthetic canonical", 7, 80)));
        var session = MapContributionCaptureSession.Create(Guid.NewGuid(), owner, ids[0], DateTimeOffset.UtcNow);
        context.Set<MapContributionCaptureSession>().Add(session);
        context.MapContributions.Add(MapContribution.Create(Guid.NewGuid(), owner, ids[0], session.SessionId,
            "private/camera/must-not-leak.jpg", "Private pending description", new MapContributionLocationEvidence(null, null, null, null, null, null, null), DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var actors = new DiscoveryActor(owner);
        // Explicit synthetic decisions: these do not prove production publication eligibility.
        var visibility = new SyntheticDiscoveryVisibility((scope, requested, _) => Task.FromResult(new MapAttractionVisibilityBatch(scope,
            requested.Reverse().Select(id => new MapAttractionVisibilityDecision(id,
                id == ids[1] ? MapAttractionVisibility.Hidden : MapAttractionVisibility.Visible)))));
        var service = new MapAttractionDiscoveryService(new MapAttractionRepository<HobbiesMapDbContext>(context), visibility, actors);
        var first = await service.DiscoverAsync(new(0, 3));
        var second = await service.DiscoverAsync(new(3, 3));
        var final = await service.DiscoverAsync(new(5, 3));
        Assert.Equal(new[] { ids[0], ids[2], ids[3], ids[4] }, first.Items.Concat(second.Items).Select(x => x.AttractionId));
        Assert.Equal((3, 2, 0), (first.ScannedCount, second.ScannedCount, final.ScannedCount));
        Assert.All(new[] { first, second, final }, result => Assert.Equal(MapAttractionDiscoveryOutcome.Success, result.Outcome));
        Assert.Empty(final.Items);
        Assert.Equal(2, visibility.Calls);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal("Synthetic canonical", item.Name));
        Assert.Equal(4, typeof(MapAttractionDiscoveryItem).GetProperties().Length);
        var denyAll = new MapAttractionDiscoveryService(new MapAttractionRepository<HobbiesMapDbContext>(context),
            new DenyAllMapAttractionDiscoveryVisibilityReader(), actors);
        Assert.Empty((await denyAll.DiscoverAsync(new(0, 3))).Items);
        Assert.Equal(MapContributionModerationStatus.PendingReview, (await context.MapContributions.AsNoTracking().SingleAsync()).ModerationStatus);
        Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
    });

    [MapCapturePostgresFact]
    public Task DiscoveryService_RejectsContradictoryVisibilityAfterRealCanonicalRead() => WithDatabaseAsync(async connection =>
    {
        await using var context = CreateContext(connection);
        context.MapAttractions.Add(MapAttraction.Create(AttractionId, Name, 7, 80));
        await context.SaveChangesAsync();
        var owner = Guid.NewGuid();
        foreach (var shape in new[] { "missing", "unknown", "foreign-id", "duplicate", "wrong-owner" })
        {
            var visibility = new SyntheticDiscoveryVisibility((scope, requested, _) =>
            {
                var evidence = new List<MapAttractionVisibilityDecision> { new(requested[0], MapAttractionVisibility.Visible) };
                if (shape == "missing") evidence.Clear();
                if (shape == "unknown") evidence[0] = new(requested[0], MapAttractionVisibility.Unknown);
                if (shape == "foreign-id") evidence[0] = new(Guid.NewGuid(), MapAttractionVisibility.Visible);
                if (shape == "duplicate") evidence.Add(evidence[0]);
                return Task.FromResult(new MapAttractionVisibilityBatch(shape == "wrong-owner" ? Guid.NewGuid() : scope, evidence));
            });
            var service = new MapAttractionDiscoveryService(new MapAttractionRepository<HobbiesMapDbContext>(context), visibility, new DiscoveryActor(owner));
            var result = await service.DiscoverAsync(new(0, 3));
            Assert.Equal(MapAttractionDiscoveryOutcome.Unavailable, result.Outcome);
            Assert.Empty(result.Items);
            Assert.Equal(0, result.ScannedCount);
        }
        Assert.Empty(await context.MapContributions.ToListAsync());
        Assert.Empty(await context.FlightPointsLedgerEntries.ToListAsync());
    });

    [MapCapturePostgresFact]
    public Task DiscoveryService_ScopeChangesAndLateCancellationNeverReturnReadMetadata() => WithDatabaseAsync(async connection =>
    {
        await using var context = CreateContext(connection);
        context.MapAttractions.Add(MapAttraction.Create(AttractionId, Name, 7, 80));
        await context.SaveChangesAsync();
        var actors = new DiscoveryActor(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var cancel = false;
        var visibility = new SyntheticDiscoveryVisibility((scope, ids, _) =>
        {
            if (cancel) cts.Cancel();
            else actors.Current = new(Guid.NewGuid(), "Social", true);
            return Task.FromResult(new MapAttractionVisibilityBatch(scope, ids.Select(id => new MapAttractionVisibilityDecision(id, MapAttractionVisibility.Visible))));
        });
        var service = new MapAttractionDiscoveryService(new MapAttractionRepository<HobbiesMapDbContext>(context), visibility, actors);
        var denied = await service.DiscoverAsync(new(0, 3));
        Assert.Equal(MapAttractionDiscoveryOutcome.Denied, denied.Outcome);
        Assert.Empty(denied.Items);
        actors.Current = new(Guid.NewGuid(), "Hobbies", true);
        cancel = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(new(0, 3), cts.Token));
        Assert.Single(await context.MapAttractions.ToListAsync());
        Assert.Empty(await context.MapContributions.ToListAsync());
    });

    [MapCapturePostgresFact]
    public Task DiscoveryService_PostgresFailureReturnsUnavailableWithoutProviderMetadata() => WithDatabaseAsync(async connection =>
    {
        var invalid = new NpgsqlConnectionStringBuilder(connection) { Database = "andril_m3_missing_" + Guid.NewGuid().ToString("N") };
        await using var broken = CreateContext(invalid.ConnectionString);
        var visibility = new SyntheticDiscoveryVisibility((_, _, _) => throw new InvalidOperationException("Visibility must not be read after database failure"));
        var service = new MapAttractionDiscoveryService(new MapAttractionRepository<HobbiesMapDbContext>(broken), visibility, new DiscoveryActor(Guid.NewGuid()));
        var result = await service.DiscoverAsync(new(0, 3));
        Assert.Equal(MapAttractionDiscoveryOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(0, visibility.Calls);
    });

    private sealed class DiscoveryActor(Guid owner) : IGalleryActorContext
    {
        public GalleryActor? Current { get; set; } = new(owner, "Hobbies", true);
    }

    private sealed class SyntheticDiscoveryVisibility(Func<Guid, IReadOnlyList<Guid>, CancellationToken, Task<MapAttractionVisibilityBatch>> read)
        : IMapAttractionDiscoveryVisibilityReader
    {
        public int Calls { get; private set; }
        public Task<MapAttractionVisibilityBatch> ReadAsync(Guid owner, IReadOnlyList<Guid> ids, CancellationToken token = default)
        { Calls++; return read(owner, ids, token); }
    }
}
