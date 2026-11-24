using System.Data.Common;
using System.Text.Json;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class MapContributionOwnerStatusServiceTests
{
    [MapCapturePostgresFact]
    public Task StatusReadsAreOwnerScopedMetadataOnlyAndLeavePersistenceUnchanged() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var seed = await Seed(connection);
            await using var context = Context(connection);
            var actor = new Actor(new(seed.OwnerHobbiesModuleProfileId, "Hobbies", true));
            var service = new MapContributionOwnerStatusService(new MapContributionRepository<HobbiesMapDbContext>(context), actor);
            var result = Assert.IsType<MapContributionOwnerStatus>(await service.GetMineAsync(seed.ContributionId));
            Assert.Equal(MapContributionModerationStatus.PendingReview, result.Status);
            Assert.Equal(seed.ContributionId, result.ContributionId);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.Equal(new[] { "ContributionId", "Status" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
            actor.Value = actor.Value! with { ModuleProfileId = Guid.NewGuid() };
            var foreign = await service.GetMineAsync(seed.ContributionId);
            var missing = await service.GetMineAsync(Guid.NewGuid());
            Assert.Null(foreign);
            Assert.Null(missing);
            Assert.Equal(JsonSerializer.Serialize(missing), JsonSerializer.Serialize(foreign));
            actor.Value = new(seed.OwnerHobbiesModuleProfileId, "Learning", true);
            Assert.Null(await service.GetMineAsync(seed.ContributionId));
            Assert.Empty(context.ChangeTracker.Entries());
            await AssertUnchanged(connection, seed);
        });

    [MapCapturePostgresFact]
    public Task StatusReadCancellationAfterDatabaseExecutionCannotReturnMetadata() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var seed = await Seed(connection);
            using var cancellation = new CancellationTokenSource();
            var probe = new ReadProbe(cancellation);
            await using var context = Context(connection, probe);
            var service = new MapContributionOwnerStatusService(new MapContributionRepository<HobbiesMapDbContext>(context),
                new Actor(new(seed.OwnerHobbiesModuleProfileId, "Hobbies", true)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetMineAsync(seed.ContributionId, cancellation.Token));
            Assert.True(probe.Executed);
            await AssertUnchanged(connection, seed);
        });

    [MapCapturePostgresFact]
    public Task StatusDatabaseFailureIsSanitizedWithoutWrites() =>
        MapContributionPersistenceTests.WithDatabaseAsync(async connection =>
        {
            var seed = await Seed(connection);
            await using var context = Context(connection, new ReadProbe());
            var service = new MapContributionOwnerStatusService(new MapContributionRepository<HobbiesMapDbContext>(context),
                new Actor(new(seed.OwnerHobbiesModuleProfileId, "Hobbies", true)));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMineAsync(seed.ContributionId));
            Assert.Equal("Contribution status retrieval is unavailable.", error.Message);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(seed.PrivatePhotoStorageObjectKey, error.ToString());
            await AssertUnchanged(connection, seed);
        });

    private static async Task<MapContribution> Seed(string connection)
    {
        await using var context = Context(connection);
        var session = await context.CaptureSessions.AsNoTracking().SingleAsync();
        var contribution = MapContribution.Create(Guid.NewGuid(), session.OwnerHobbiesModuleProfileId, session.AttractionId,
            session.SessionId, "private/status-photo", "private description", new MapContributionLocationEvidence(
                7, 80, 7, 80, 5, session.StartedAtUtc, session.StartedAtUtc), DateTimeOffset.UtcNow);
        await new MapContributionRepository<HobbiesMapDbContext>(context).AddAsync(contribution);
        return contribution;
    }

    private static async Task AssertUnchanged(string connection, MapContribution seed)
    {
        await using var probe = Context(connection);
        var saved = Assert.Single(await probe.MapContributions.AsNoTracking().ToListAsync());
        Assert.Equal(seed.ContributionId, saved.ContributionId);
        Assert.Equal(seed.PrivatePhotoStorageObjectKey, saved.PrivatePhotoStorageObjectKey);
        Assert.Equal(seed.Description, saved.Description);
        Assert.Equal(MapContributionModerationStatus.PendingReview, saved.ModerationStatus);
        Assert.Empty(await probe.FlightPointsLedgerEntries.ToListAsync());
    }

    private static HobbiesMapDbContext Context(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(connection, HobbiesDatabaseOptions.ConfigureMapHistory);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class Actor(GalleryActor? value) : IGalleryActorContext
    {
        internal GalleryActor? Value { get; set; } = value;
        public GalleryActor? Current => Value;
    }

    private sealed class ReadProbe(CancellationTokenSource? cancellation = null) : DbCommandInterceptor
    {
        internal bool Executed { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (cancellation is null) throw new NpgsqlException("sensitive private/status-photo");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Executed = true;
            cancellation!.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
