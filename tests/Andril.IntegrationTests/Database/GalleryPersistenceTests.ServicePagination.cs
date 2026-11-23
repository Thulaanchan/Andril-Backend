using System.Data.Common;
using System.Text.Json;
using Andril.Hobbies.DTOs;
using Andril.Hobbies.Interfaces;
using Andril.Hobbies.Services;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    [MapCapturePostgresFact]
    public Task ServicePagination_TraversesScopedQueryCWithTiesReplayAndMetadataOnly() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [.. KeysetRows(), KeysetMedia(10, CreatedAt, OtherOwner),
            KeysetMedia(11, CreatedAt, module: "Learning"), KeysetMedia(12, CreatedAt, deleted: true)]);
        var before = await SnapshotKeysetAsync(connection);
        var capture = new ServicePageCommands();
        await using var context = CreateContext(connection, capture);
        var repository = new GalleryRepository<Andril.Persistence.DbContexts.HobbiesGalleryDbContext>(context);
        IGalleryPageRepository port = repository;
        var service = new GalleryService(repository, new ServicePageActor(), port);
        var first = await service.GetMinePageAsync(2, 3);
        Assert.Equal(new[] { KeysetId(1), KeysetId(2) }, first.Items.Select(row => row.Id));
        Assert.NotNull(first.Next);
        capture.Commands.Clear();
        var second = await service.GetMinePageAsync(2, 3, first.Next);
        var union = Assert.Single(capture.Commands, command => command.Sql.Contains("UNION ALL", StringComparison.Ordinal));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(union.Sql, @"\bLIMIT\b").Count);
        Assert.DoesNotContain("OFFSET", union.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(union.Values, value => Equals(value, Owner));
        Assert.Contains(union.Values, value => Equals(value, first.Next.Id));
        Assert.DoesNotContain(Owner.ToString(), union.Sql, StringComparison.OrdinalIgnoreCase);
        var repeated = await service.GetMinePageAsync(2, 3, first.Next);
        Assert.Equal(second.Items, repeated.Items); Assert.Equal(second.Next, repeated.Next);
        var last = await service.GetMinePageAsync(2, 3, second.Next);
        Assert.Null(last.Next);
        Assert.Equal(Enumerable.Range(1, 6).Select(KeysetId), first.Items.Concat(second.Items).Concat(last.Items).Select(row => row.Id));
        var json = JsonSerializer.Serialize(first.Items.Concat(second.Items).Concat(last.Items));
        Assert.DoesNotContain("StorageObjectKey", json); Assert.DoesNotContain(Owner.ToString(), json);
        Assert.DoesNotContain(OtherOwner.ToString(), json); Assert.DoesNotContain("private/", json);
        var empty = await service.GetMinePageAsync(2, 3, new(Owner, "Hobbies", CreatedAt.AddDays(-1), KeysetId(99)));
        Assert.Empty(empty.Items); Assert.Null(empty.Next);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(before, await SnapshotKeysetAsync(connection));
    });

    [MapCapturePostgresFact]
    public Task ServicePagination_DatabaseFailureReturnsNoMetadataOrProviderDetails() => WithDatabaseAsync(async connection =>
    {
        await using var context = CreateContext(connection);
        await context.Database.ExecuteSqlRawAsync("DROP TABLE andril_hobbies.\"GalleryMedia\"");
        var repository = new GalleryRepository<Andril.Persistence.DbContexts.HobbiesGalleryDbContext>(context);
        var service = new GalleryService(repository, new ServicePageActor(), repository);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMinePageAsync(2, 3));
        Assert.Equal("Gallery page retrieval is unavailable.", error.Message);
        Assert.Null(error.InnerException);
    });

    private sealed class ServicePageActor : IGalleryActorContext
    {
        public GalleryActor Current => new(Owner, "Hobbies", true);
    }

    private sealed class ServicePageCommands : DbCommandInterceptor
    {
        internal List<(string Sql, object?[] Values)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
