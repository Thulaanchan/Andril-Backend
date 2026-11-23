using System.Data.Common;
using System.Text.Json;
using Andril.Hobbies.Entities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Andril.IntegrationTests.Database;

// Test-only candidate. No repository/service/API contract, public cursor or Product limit is introduced.
public sealed partial class GalleryPersistenceTests
{
    private readonly ITestOutputHelper _keysetOutput;
    private bool _exerciseRepositoryKeyset;
    public GalleryPersistenceTests(ITestOutputHelper output) => _keysetOutput = output;

    [MapCapturePostgresFact]
    public Task Keyset_EqualTimestampsCrossPageBoundariesInIdOrder() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, new[] { 6, 2, 4, 1, 5, 3 }.Select(id => KeysetMedia(id, CreatedAt)).ToArray());
        await using var context = CreateContext(connection);
        Assert.Equal(Enumerable.Range(1, 6).Select(KeysetId), await TraverseKeysetAsync(context, Owner, 2));
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [MapCapturePostgresFact]
    public Task Keyset_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection);
        var ids = await TraverseKeysetAsync(context, Owner, 2);
        Assert.Equal(Enumerable.Range(1, 6).Select(KeysetId), ids);
        Assert.Equal(6, ids.Distinct().Count());
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [MapCapturePostgresFact]
    public Task Keyset_OwnerFilterCannotBeReplacedByACursorPosition() => WithDatabaseAsync(async connection =>
    {
        var foreign = KeysetMedia(10, CreatedAt.AddMinutes(3), OtherOwner);
        await SeedKeysetAsync(connection, [.. KeysetRows(), foreign]);
        await using var context = CreateContext(connection);
        Assert.Equal(Enumerable.Range(1, 6).Select(KeysetId), await TraverseKeysetAsync(context, Owner, 1));
        // Positions are navigation data, not authenticated owner identities.
        var fromForeignPosition = await ReadKeysetAsync(context, Owner, 2, new(foreign.CreatedAtUtc, foreign.Id));
        Assert.All(fromForeignPosition.Items, row => Assert.Equal(Owner, row.OwnerModuleProfileId));
        Assert.DoesNotContain(fromForeignPosition.Items, row => row.Id == foreign.Id);
        Assert.Equal(new[] { foreign.Id }, await TraverseKeysetAsync(context, OtherOwner, 1));
    });

    [MapCapturePostgresFact]
    public Task Keyset_WrongModuleRowsCannotFillThePageOrItsLookahead() => WithDatabaseAsync(async connection =>
    {
        var foreign = new[] { "Learning", "Social", "social", "Dating" }
            .Select((module, i) => KeysetMedia(10 + i, CreatedAt.AddMinutes(3), module: module));
        await SeedKeysetAsync(connection, [.. KeysetRows(), .. foreign]);
        await using var context = CreateContext(connection);
        Assert.Equal(Enumerable.Range(1, 6).Select(KeysetId), await TraverseKeysetAsync(context, Owner, 2));
        var page = await ReadKeysetAsync(context, Owner, 1);
        Assert.Equal(2, page.FetchedCount);
        Assert.All(page.Items, row => Assert.Equal("Hobbies", row.Module));
    });

    [MapCapturePostgresFact]
    public Task Keyset_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, [.. KeysetRows(), KeysetMedia(10, CreatedAt.AddMinutes(3), deleted: true)]);
        await using var reader = CreateContext(connection);
        var first = await ReadKeysetAsync(reader, Owner, 1);
        Assert.Equal(KeysetId(1), Assert.Single(first.Items).Id);
        await using (var writer = CreateContext(connection))
            Assert.Equal(1, await writer.GalleryMedia.Where(row => row.Id == KeysetId(3))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsDeleted, true)));
        var remaining = await TraverseKeysetAsync(reader, Owner, 1, first.Next);
        Assert.Equal(new[] { 2, 4, 5, 6 }.Select(KeysetId), remaining);
        Assert.DoesNotContain(KeysetId(10), remaining);
    });

    [MapCapturePostgresFact]
    public Task Keyset_ContinuationDoesNotRequireTheAnchorRowToExist() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var reader = CreateContext(connection);
        var first = await ReadKeysetAsync(reader, Owner, 1);
        Assert.NotNull(first.Next);
        await using (var writer = CreateContext(connection))
            Assert.Equal(1, await writer.GalleryMedia.Where(row => row.Id == first.Next.Id).ExecuteDeleteAsync());
        Assert.Equal(Enumerable.Range(2, 5).Select(KeysetId), await TraverseKeysetAsync(reader, Owner, 1, first.Next));
    });

    [MapCapturePostgresFact]
    public Task Keyset_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var reader = CreateContext(connection);
        var first = await ReadKeysetAsync(reader, Owner, 1);
        await SeedKeysetAsync(connection, KeysetMedia(10, CreatedAt.AddMinutes(3)));
        Assert.Equal(Enumerable.Range(2, 5).Select(KeysetId), await TraverseKeysetAsync(reader, Owner, 2, first.Next));
        var refreshed = await ReadKeysetAsync(reader, Owner, 1);
        Assert.Equal(KeysetId(10), Assert.Single(refreshed.Items).Id);
    });

    [MapCapturePostgresFact]
    public Task Keyset_InsertAfterCursorAppearsInTheLiveContinuation() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var reader = CreateContext(connection);
        var first = await ReadKeysetAsync(reader, Owner, 1);
        await SeedKeysetAsync(connection, KeysetMedia(10, CreatedAt.AddMinutes(2)));
        Assert.Equal(new[] { 2, 10, 3, 4, 5, 6 }.Select(KeysetId), await TraverseKeysetAsync(reader, Owner, 2, first.Next));
    });

    [MapCapturePostgresFact]
    public Task Keyset_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks() => WithDatabaseAsync(async connection =>
    {
        var inputs = new[] { KeysetMedia(1, CreatedAt.AddTicks(11)), KeysetMedia(2, CreatedAt.AddTicks(19)), KeysetMedia(3, CreatedAt.AddTicks(7)) };
        await SeedKeysetAsync(connection, inputs);
        await using var context = CreateContext(connection);
        var first = await ReadKeysetAsync(context, Owner, 1);
        Assert.NotNull(first.Next);
        Assert.NotEqual(Assert.Single(inputs, row => row.Id == first.Next.Id).CreatedAtUtc, first.Next.Timestamp);
        Assert.Equal(0, first.Next.Timestamp.Ticks % 10);
        Assert.Equal(TimeSpan.Zero, first.Next.Timestamp.Offset);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT \"Id\", \"CreatedAtUtc\" FROM andril_hobbies.\"GalleryMedia\" ORDER BY \"CreatedAtUtc\" DESC, \"Id\"", database);
        var stored = new List<(Guid Id, DateTimeOffset Timestamp)>();
        await using (var rows = await query.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync()) stored.Add((rows.GetGuid(0), new DateTimeOffset(rows.GetDateTime(1))));
        }
        Assert.Equal((first.Next.Id, first.Next.Timestamp), stored[0]);
        Assert.Equal(stored.Skip(1).Select(row => row.Id), await TraverseKeysetAsync(context, Owner, 1, first.Next));
    });

    [MapCapturePostgresFact]
    public Task Keyset_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering() => WithDatabaseAsync(async connection =>
    {
        var ids = new[]
        {
            "ffffffff-ffff-ffff-ffff-ffffffffffff", "80000000-0000-0000-0000-000000000000",
            "00000000-0000-0000-0000-000000000001", "00000001-0000-0000-0000-000000000000",
            "7fffffff-ffff-ffff-ffff-ffffffffffff", "00000000-8000-0000-0000-000000000000",
            "00000000-0000-8000-0000-000000000000", "00000000-0000-0000-8000-000000000000"
        }.Select(Guid.Parse).ToArray();
        await SeedKeysetAsync(connection, ids.Select(id => Media(id, Owner, CreatedAt)).ToArray());
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await using var reference = new NpgsqlCommand("SELECT \"Id\" FROM andril_hobbies.\"GalleryMedia\" ORDER BY \"Id\"", database);
        var expected = new List<Guid>();
        await using (var rows = await reference.ExecuteReaderAsync())
            while (await rows.ReadAsync()) expected.Add(rows.GetGuid(0));
        var capture = new KeysetCommandCapture();
        await using var context = CreateContext(connection, capture);
        Assert.Equal(expected, await TraverseKeysetAsync(context, Owner, 1));
        Assert.NotNull(capture.Sql);
        Assert.Contains("\"OwnerModuleProfileId\"", capture.Sql);
        Assert.Contains("\"Module\"", capture.Sql);
        Assert.Contains("\"IsDeleted\"", capture.Sql);
        Assert.Contains("ORDER BY", capture.Sql);
        Assert.Contains("\"CreatedAtUtc\" DESC", capture.Sql);
        Assert.Contains("LIMIT", capture.Sql);
        Assert.DoesNotContain("OFFSET", capture.Sql);
        Assert.Contains(">", capture.Sql);
        Assert.True(capture.Sql.IndexOf("WHERE", StringComparison.Ordinal) < capture.Sql.IndexOf("ORDER BY", StringComparison.Ordinal));
        Assert.True(capture.Sql.IndexOf("ORDER BY", StringComparison.Ordinal) < capture.Sql.IndexOf("LIMIT", StringComparison.Ordinal));
        Assert.Contains(capture.Parameters, parameter => parameter.Value is Guid);
        Assert.Contains(capture.Parameters, parameter => parameter.Value is DateTimeOffset);
        Assert.Contains(capture.Parameters, parameter => Equals(parameter.Value, 2)); // Local limit 1 + lookahead.
        _keysetOutput.WriteLine(capture.Sql);
        _keysetOutput.WriteLine(string.Join(", ", capture.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}")));
    });

    [MapCapturePostgresFact]
    public Task Keyset_InFlightDatabaseCancellationPropagatesTheSuppliedToken() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync(deadline.Token);
        await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
        await using (var locked = new NpgsqlCommand("LOCK TABLE andril_hobbies.\"GalleryMedia\" IN ACCESS EXCLUSIVE MODE", blocker, transaction))
            await locked.ExecuteNonQueryAsync(deadline.Token);
        var capture = new KeysetCommandCapture();
        await using var context = CreateContext(connection, capture);
        await context.Database.OpenConnectionAsync(deadline.Token);
        var waitingPid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        // Exercise bounded UNION branches as well as the first-page form in compatibility tests.
        KeysetPosition? after = _experimentalEfShape is null ? null : new(CreatedAt.AddMinutes(2), KeysetId(1));
        var pending = ReadKeysetAsync(context, Owner, 2, after, cancellation.Token);
        try
        {
            await capture.Started.Task.WaitAsync(deadline.Token);
            await using var observer = new NpgsqlConnection(connection);
            await observer.OpenAsync(deadline.Token);
            await using var blocked = new NpgsqlCommand("SELECT $1 = ANY(pg_blocking_pids($2))", observer);
            blocked.Parameters.Add(new NpgsqlParameter { Value = blocker.ProcessID });
            blocked.Parameters.Add(new NpgsqlParameter { Value = waitingPid });
            while (await blocked.ExecuteScalarAsync(deadline.Token) is not true)
                Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync(CancellationToken.None);
            try { await pending; } catch (OperationCanceledException) { }
        }
        await using var fresh = CreateContext(connection);
        Assert.Equal(6, await fresh.GalleryMedia.CountAsync());
    });

    [MapCapturePostgresFact]
    public Task Keyset_DatabaseFailureCannotBecomeAnEmptySuccessfulPage() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection);
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE andril_hobbies.\"GalleryMedia\" RENAME TO keyset_gallery_unavailable");
        KeysetPosition? after = _experimentalEfShape is null ? null : new(CreatedAt.AddMinutes(2), KeysetId(1));
        var error = await Assert.ThrowsAsync<PostgresException>(() => ReadKeysetAsync(context, Owner, 2, after));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
    });

    [MapCapturePostgresFact]
    public Task Keyset_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection);
        var first = await ReadKeysetAsync(context, Owner, 2);
        Assert.Equal(3, first.FetchedCount);
        Assert.Equal(new[] { KeysetId(1), KeysetId(2) }, first.Items.Select(row => row.Id));
        Assert.Equal(new KeysetPosition(first.Items[^1].CreatedAtUtc, KeysetId(2)), first.Next);
        var second = await ReadKeysetAsync(context, Owner, 2, first.Next);
        Assert.Equal(new[] { KeysetId(3), KeysetId(4) }, second.Items.Select(row => row.Id));
        var final = await ReadKeysetAsync(context, Owner, 2, second.Next);
        Assert.Equal(new[] { KeysetId(5), KeysetId(6) }, final.Items.Select(row => row.Id));
        Assert.Equal(2, final.FetchedCount);
        Assert.Null(final.Next);
        var empty = await ReadKeysetAsync(context, Owner, 2, new(final.Items[^1].CreatedAtUtc, final.Items[^1].Id));
        Assert.Empty(empty.Items);
        Assert.Null(empty.Next);
    });

    [MapCapturePostgresFact]
    public Task Keyset_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using (var seed = CreateContext(connection))
        {
            seed.GalleryPreferences.Add(new GalleryPreferences { OwnerModuleProfileId = Owner, CoverMediaId = KeysetId(1) });
            await seed.SaveChangesAsync();
        }
        await using var context = CreateContext(connection);
        var tracked = await context.GalleryMedia.SingleAsync(row => row.Id == KeysetId(1));
        tracked.Title = "Unrelated unsaved caller change";
        var before = await SnapshotKeysetAsync(connection);
        Assert.Equal(6, (await TraverseKeysetAsync(context, Owner, 2)).Count);
        Assert.Equal(before, await SnapshotKeysetAsync(connection));
        var entry = Assert.Single(context.ChangeTracker.Entries<GalleryMedia>());
        Assert.Same(tracked, entry.Entity);
        Assert.Equal("Unrelated unsaved caller change", tracked.Title);
        Assert.Equal(EntityState.Modified, entry.State);
    });

    private sealed record KeysetPosition(DateTimeOffset Timestamp, Guid Id);
    private sealed record KeysetPage(IReadOnlyList<GalleryMedia> Items, KeysetPosition? Next, int FetchedCount);

    private static IQueryable<GalleryMedia> KeysetQuery(HobbiesGalleryDbContext context, Guid authorizedOwnerId,
        int limit, KeysetPosition? after)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit); // No Product cap/default is inferred.
        if (authorizedOwnerId == Guid.Empty) throw new ArgumentException("A trusted test owner is required.", nameof(authorizedOwnerId));
        var query = context.GalleryMedia.AsNoTracking().Where(row => row.OwnerModuleProfileId == authorizedOwnerId
            && row.Module == "Hobbies" && !row.IsDeleted);
        if (after is not null)
        {
            var timestamp = after.Timestamp;
            var id = after.Id;
            query = query.Where(row => row.CreatedAtUtc < timestamp
                || (row.CreatedAtUtc == timestamp && row.Id.CompareTo(id) > 0));
        }
        return query.OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(checked(limit + 1));
    }

    private async Task<KeysetPage> ReadKeysetAsync(HobbiesGalleryDbContext context, Guid authorizedOwnerId,
        int limit, KeysetPosition? after = null, CancellationToken token = default)
    {
        if (_exerciseRepositoryKeyset || _experimentalEfShape is not null)
        {
            var position = after is null ? null : new GalleryPagePosition(authorizedOwnerId, "Hobbies", after.Timestamp, after.Id);
            var page = _experimentalEfShape is EfSeekShape shape
                ? await ReadEfSeekAsync(context, shape, authorizedOwnerId, limit, 3, position, token)
                : await new GalleryRepository<HobbiesGalleryDbContext>(context).GetOwnerGalleryPageAsync(
                    authorizedOwnerId, limit, trustedTestOnlyMaximum: 3, position, token);
            if (page.Next is not null)
            {
                Assert.Equal(authorizedOwnerId, page.Next.OwnerModuleProfileId);
                Assert.Equal("Hobbies", page.Next.Module);
            }
            return new(page.Items, page.Next is null ? null : new(page.Next.Timestamp, page.Next.Id),
                page.Items.Count + (page.Next is null ? 0 : 1));
        }
        token.ThrowIfCancellationRequested();
        var fetched = await KeysetQuery(context, authorizedOwnerId, limit, after).ToListAsync(token);
        token.ThrowIfCancellationRequested();
        var items = fetched.Take(limit).ToArray();
        var next = fetched.Count > limit ? new KeysetPosition(items[^1].CreatedAtUtc, items[^1].Id) : null;
        return new(Array.AsReadOnly(items), next, fetched.Count);
    }

    private async Task<IReadOnlyList<Guid>> TraverseKeysetAsync(HobbiesGalleryDbContext context,
        Guid owner, int limit, KeysetPosition? after = null)
    {
        var result = new List<Guid>();
        for (var pageNumber = 0; pageNumber < 20; pageNumber++)
        {
            var page = await ReadKeysetAsync(context, owner, limit, after);
            Assert.InRange(page.FetchedCount, 0, limit + 1);
            Assert.InRange(page.Items.Count, 0, limit);
            Assert.All(page.Items, row =>
            {
                Assert.Equal(owner, row.OwnerModuleProfileId);
                Assert.Equal("Hobbies", row.Module);
                Assert.False(row.IsDeleted);
            });
            result.AddRange(page.Items.Select(row => row.Id));
            if (page.Next is null) return result;
            Assert.NotEqual(after, page.Next);
            after = page.Next;
        }
        throw new InvalidOperationException("The small test dataset did not terminate.");
    }

    private static Guid KeysetId(int suffix) => Guid.Parse($"30000000-0000-4000-8000-{suffix:D12}");
    private static GalleryMedia KeysetMedia(int suffix, DateTimeOffset timestamp, Guid? owner = null,
        string module = "Hobbies", bool deleted = false) => Media(KeysetId(suffix), owner ?? Owner, timestamp, module, deleted);
    private static GalleryMedia[] KeysetRows() =>
    [
        KeysetMedia(5, CreatedAt), KeysetMedia(2, CreatedAt.AddMinutes(2)),
        KeysetMedia(6, CreatedAt), KeysetMedia(3, CreatedAt.AddMinutes(1)),
        KeysetMedia(1, CreatedAt.AddMinutes(2)), KeysetMedia(4, CreatedAt.AddMinutes(1))
    ];
    private static async Task SeedKeysetAsync(string connection, params GalleryMedia[] rows)
    {
        await using var context = CreateContext(connection);
        context.GalleryMedia.AddRange(rows);
        await context.SaveChangesAsync();
    }
    private static async Task<string> SnapshotKeysetAsync(string connection)
    {
        await using var context = CreateContext(connection);
        var media = await context.GalleryMedia.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        var preferences = await context.GalleryPreferences.AsNoTracking().OrderBy(row => row.OwnerModuleProfileId).ToListAsync();
        return JsonSerializer.Serialize(new { Media = media, Preferences = preferences });
    }

    private sealed class KeysetCommandCapture : DbCommandInterceptor
    {
        public string? Sql { get; private set; }
        public int CommandCount { get; private set; }
        public IReadOnlyList<(string Name, string Type, object? Value)> Parameters { get; private set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            Sql = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>()
                .Select(parameter => (parameter.ParameterName, parameter.DbType.ToString(), parameter.Value)).ToArray();
            Started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
