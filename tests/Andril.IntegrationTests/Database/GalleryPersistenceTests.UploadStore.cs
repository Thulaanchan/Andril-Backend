using Andril.API.Authorization.Hobbies;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Media;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    [MapCapturePostgresFact]
    public Task UploadStore_PostgresUpgradePreservesLegacyAndRollbackDropsReceipts() => WithDatabaseAsync(async connection =>
    {
        await using var db = CreateContext(connection);
        var baseline = db.Database.GetMigrations().First();
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_hobbies."GalleryMedia"
            ("Id","OwnerModuleProfileId","Module","Title","Category","MediaType","StorageObjectKey","IsPrivate","IsDeleted","CreatedAtUtc")
            VALUES ({Older},{Owner},'Hobbies','Original title','Synthetic','Photo','original.jpg',true,false,{CreatedAt})
            """);
        await db.Database.MigrateAsync();
        var existing = await db.GalleryMedia.AsNoTracking().SingleAsync();
        Assert.Equal("Original title", existing.Title);
        Assert.Null(existing.ProviderGeneration);
        var store = new PostgresGalleryUploadOperations(new ReceiptContextFactory(connection), Owner, TimeProvider.System);
        var intent = UploadIntent(Owner);
        var reservation = await store.ReserveAsync(intent, default);
        var evidence = new GalleryUploadObject(reservation.Binding!, "generation-1");
        Assert.True(await store.CompleteAsync(evidence, _ => Task.FromResult(true), default));
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_hobbies."GalleryMedia" SET "ProviderGeneration" = 'replacement' WHERE "Id" = {reservation.Binding!.Object.MediaId}
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, immutable.SqlState);
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        Assert.Equal(2, await db.GalleryMedia.CountAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Set<GalleryUploadOperation>().ToListAsync());
        Assert.Equal(2, await db.GalleryMedia.CountAsync());
        Assert.All(await db.GalleryMedia.AsNoTracking().ToListAsync(), media => Assert.Null(media.ProviderGeneration));
    }, initializeSchema: false);

    [MapCapturePostgresFact]
    public Task UploadStore_PostgresScopesConcurrentReservationAndCompletion() => WithDatabaseAsync(async connection =>
    {
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var factory = new ReceiptContextFactory(connection);
        var first = new PostgresGalleryUploadOperations(factory, Owner, TimeProvider.System);
        var second = new PostgresGalleryUploadOperations(factory, Owner, TimeProvider.System);
        var foreign = new PostgresGalleryUploadOperations(factory, OtherOwner, TimeProvider.System);
        var intent = UploadIntent(Owner);
        var reservations = await Task.WhenAll(first.ReserveAsync(intent, default), second.ReserveAsync(intent, default));
        var created = Assert.Single(reservations, item => item.State == GalleryUploadReservationState.Create);
        var replay = Assert.Single(reservations, item => item.State == GalleryUploadReservationState.Inspect);
        Assert.Equal(created.Binding, replay.Binding);
        Assert.Equal(GalleryUploadReservationState.Conflict,
            (await second.ReserveAsync(intent with { Sha256 = new string('B', 64) }, default)).State);
        Assert.Equal(GalleryUploadReservationState.Conflict, (await foreign.ReserveAsync(intent, default)).State);
        var other = await foreign.ReserveAsync(intent with { OwnerProfileId = OtherOwner }, default);
        Assert.Equal(GalleryUploadReservationState.Create, other.State);
        Assert.NotEqual(created.Binding!.Object, other.Binding!.Object);

        var evidence = new GalleryUploadObject(created.Binding, "generation-1");
        Assert.False(await first.CompleteAsync(evidence with { Binding = created.Binding with {
            Object = created.Binding.Object with { Module = "Learning" } } }, _ => Task.FromResult(true), default));
        var attempts = await Task.WhenAll(first.CompleteAsync(evidence, _ => Task.FromResult(true), default),
            second.CompleteAsync(evidence, _ => Task.FromResult(true), default));
        Assert.Contains(true, attempts);
        Assert.Equal(GalleryUploadReservationState.Completed, (await second.ReserveAsync(intent, default)).State);
        var row = await db.Set<GalleryUploadOperation>().AsNoTracking().SingleAsync(item => item.OwnerId == Owner);
        Assert.Equal(2, row.Revision);
        Assert.Single(await db.GalleryMedia.AsNoTracking().ToListAsync());
        Assert.False(await second.CompleteAsync(evidence with { Version = "replacement" }, _ => Task.FromResult(true), default));
        var repo = new GalleryRepository<HobbiesGalleryDbContext>(db);
        var reader = new ControlledObjectReader { OnFind = item => item with { Version = "replacement" } };
        var boundary = new TestGalleryAuthorization().CreateBoundary(repo, repo);
        Assert.Equal(GalleryMetadataStatus.MediaUnavailable, (await boundary.ReadPrivateMediaAsync(row.MediaId, reader, 3)).Status);
        Assert.Equal(0, reader.Reads);
    }, initializeSchema: false);

    [MapCapturePostgresFact]
    public Task UploadStore_PostgresRollsBackOnRevokeAndReplaysAfterLostCommitReply() => WithDatabaseAsync(async connection =>
    {
        await using var db = CreateContext(connection);
        await db.Database.MigrateAsync();
        var factory = new ReceiptContextFactory(connection);
        var store = new PostgresGalleryUploadOperations(factory, Owner, TimeProvider.System);
        var intent = UploadIntent(Owner);
        var initial = await store.ReserveAsync(intent, default);
        var evidence = new GalleryUploadObject(initial.Binding!, "generation-1");
        var calls = 0;
        Assert.False(await store.CompleteAsync(evidence, _ => Task.FromResult(++calls < 3), default));
        Assert.Empty(await db.GalleryMedia.ToListAsync());
        Assert.Equal(0, (await db.Set<GalleryUploadOperation>().AsNoTracking().SingleAsync()).Revision);
        var retry = new PostgresGalleryUploadOperations(factory, Owner, TimeProvider.System);
        Assert.Equal(GalleryUploadReservationState.Inspect, (await retry.ReserveAsync(intent, default)).State);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.CompleteAsync(evidence, _ => Task.FromResult(true), cancelled.Token));

        async Task LoseReplyAsync()
        {
            Assert.True(await retry.CompleteAsync(evidence, _ => Task.FromResult(true), default));
            throw new TimeoutException("Simulated lost commit acknowledgement");
        }
        await Assert.ThrowsAsync<TimeoutException>(LoseReplyAsync);
        var fresh = new PostgresGalleryUploadOperations(new ReceiptContextFactory(connection), Owner, TimeProvider.System);
        var receipt = await fresh.ReserveAsync(intent, default);
        Assert.Equal(GalleryUploadReservationState.Completed, receipt.State);
        Assert.Equal(evidence, receipt.Completed);
        Assert.Equal(evidence.Version, (await db.GalleryMedia.AsNoTracking().SingleAsync()).ProviderGeneration);
    }, initializeSchema: false);

    private static GalleryUploadIntent UploadIntent(Guid owner) => new(Guid.NewGuid(), owner, new string('A', 64));

    private sealed class ReceiptContextFactory(string connection) : IDbContextFactory<HobbiesGalleryDbContext>
    {
        public HobbiesGalleryDbContext CreateDbContext() => CreateContext(connection);
    }
}
