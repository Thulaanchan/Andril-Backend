using System.Data.Common;
using System.Text.Json;
using Andril.Hobbies.Entities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

// Offline LINQ candidates only. No production repository, public cursor or Product limit is changed.
public sealed partial class GalleryPersistenceTests
{
    private enum EfSeekShape { A, B, C }
    private EfSeekShape? _experimentalEfShape;

    private async Task ExerciseEfSeekAsync(EfSeekShape shape, Func<Task> scenario)
    {
        _experimentalEfShape = shape;
        try { await scenario(); }
        finally { _experimentalEfShape = null; }
    }

    [MapCapturePostgresFact]
    public Task EfSeekB_EqualTimestampsCrossPageBoundariesInIdOrder() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_EqualTimestampsCrossPageBoundariesInIdOrder);

    [MapCapturePostgresFact]
    public Task EfSeekB_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows);

    [MapCapturePostgresFact]
    public Task EfSeekB_OwnerFilterCannotBeReplacedByACursorPosition() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_OwnerFilterCannotBeReplacedByACursorPosition);

    [MapCapturePostgresFact]
    public Task EfSeekB_WrongModuleRowsCannotFillThePageOrItsLookahead() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_WrongModuleRowsCannotFillThePageOrItsLookahead);

    [MapCapturePostgresFact]
    public Task EfSeekB_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries);

    [MapCapturePostgresFact]
    public Task EfSeekB_ContinuationDoesNotRequireTheAnchorRowToExist() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_ContinuationDoesNotRequireTheAnchorRowToExist);

    [MapCapturePostgresFact]
    public Task EfSeekB_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation);

    [MapCapturePostgresFact]
    public Task EfSeekB_InsertAfterCursorAppearsInTheLiveContinuation() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_InsertAfterCursorAppearsInTheLiveContinuation);

    [MapCapturePostgresFact]
    public Task EfSeekB_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks);

    [MapCapturePostgresFact]
    public Task EfSeekB_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering);

    [MapCapturePostgresFact]
    public Task EfSeekB_InFlightDatabaseCancellationPropagatesTheSuppliedToken() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_InFlightDatabaseCancellationPropagatesTheSuppliedToken);

    [MapCapturePostgresFact]
    public Task EfSeekB_DatabaseFailureCannotBecomeAnEmptySuccessfulPage() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_DatabaseFailureCannotBecomeAnEmptySuccessfulPage);

    [MapCapturePostgresFact]
    public Task EfSeekB_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor);

    [MapCapturePostgresFact]
    public Task EfSeekB_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities() =>
        ExerciseEfSeekAsync(EfSeekShape.B, Keyset_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities);

    [MapCapturePostgresFact]
    public Task EfSeekC_EqualTimestampsCrossPageBoundariesInIdOrder() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_EqualTimestampsCrossPageBoundariesInIdOrder);

    [MapCapturePostgresFact]
    public Task EfSeekC_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows);

    [MapCapturePostgresFact]
    public Task EfSeekC_OwnerFilterCannotBeReplacedByACursorPosition() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_OwnerFilterCannotBeReplacedByACursorPosition);

    [MapCapturePostgresFact]
    public Task EfSeekC_WrongModuleRowsCannotFillThePageOrItsLookahead() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_WrongModuleRowsCannotFillThePageOrItsLookahead);

    [MapCapturePostgresFact]
    public Task EfSeekC_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries);

    [MapCapturePostgresFact]
    public Task EfSeekC_ContinuationDoesNotRequireTheAnchorRowToExist() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_ContinuationDoesNotRequireTheAnchorRowToExist);

    [MapCapturePostgresFact]
    public Task EfSeekC_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation);

    [MapCapturePostgresFact]
    public Task EfSeekC_InsertAfterCursorAppearsInTheLiveContinuation() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_InsertAfterCursorAppearsInTheLiveContinuation);

    [MapCapturePostgresFact]
    public Task EfSeekC_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks);

    [MapCapturePostgresFact]
    public Task EfSeekC_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering);

    [MapCapturePostgresFact]
    public Task EfSeekC_InFlightDatabaseCancellationPropagatesTheSuppliedToken() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_InFlightDatabaseCancellationPropagatesTheSuppliedToken);

    [MapCapturePostgresFact]
    public Task EfSeekC_DatabaseFailureCannotBecomeAnEmptySuccessfulPage() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_DatabaseFailureCannotBecomeAnEmptySuccessfulPage);

    [MapCapturePostgresFact]
    public Task EfSeekC_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor);

    [MapCapturePostgresFact]
    public Task EfSeekC_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities() =>
        ExerciseEfSeekAsync(EfSeekShape.C, Keyset_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities);

    private static IQueryable<GalleryMedia> EfSeekQuery(HobbiesGalleryDbContext context, EfSeekShape shape,
        Guid owner, int fetch, GalleryPagePosition? after)
    {
        var scope = context.GalleryMedia.AsNoTracking().Where(row =>
            row.OwnerModuleProfileId == owner && row.Module == "Hobbies" && !row.IsDeleted);
        IQueryable<GalleryMedia> query = scope;
        if (after is not null)
        {
            var timestamp = after.Timestamp;
            var id = after.Id;
            if (shape == EfSeekShape.C)
            {
                var same = scope.Where(row => row.CreatedAtUtc == timestamp && row.Id.CompareTo(id) > 0)
                    .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(fetch);
                var older = scope.Where(row => row.CreatedAtUtc < timestamp)
                    .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(fetch);
                // Disjoint branches; each local top-fetch contains every possible global top-fetch row.
                query = same.Concat(older);
            }
            else
            {
                if (shape == EfSeekShape.B) query = query.Where(row => row.CreatedAtUtc <= timestamp);
                query = query.Where(row => row.CreatedAtUtc < timestamp
                    || (row.CreatedAtUtc == timestamp && row.Id.CompareTo(id) > 0));
            }
        }
        return query.OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(fetch);
    }

    private static async Task<GalleryPage> ReadEfSeekAsync(HobbiesGalleryDbContext context, EfSeekShape shape,
        Guid owner, int size, int testOnlyMaximum, GalleryPagePosition? after = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Test-only guard mirrors the existing internal position contract; not an M2 authorization adapter.
        if (owner == Guid.Empty) throw new ArgumentException("A trusted test owner is required.", nameof(owner));
        if (testOnlyMaximum <= 0 || testOnlyMaximum == int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(testOnlyMaximum));
        if (size <= 0 || size > testOnlyMaximum) throw new ArgumentOutOfRangeException(nameof(size));
        if (after is not null && (after.OwnerModuleProfileId != owner || after.Module != "Hobbies"
            || after.Id == Guid.Empty || after.Timestamp.Offset != TimeSpan.Zero
            || after.Timestamp == DateTimeOffset.MinValue || after.Timestamp == DateTimeOffset.MaxValue
            || after.Timestamp.Ticks % 10 != 0))
            throw new ArgumentException("Invalid continuation for this scope.", nameof(after));
        var fetched = await EfSeekQuery(context, shape, owner, checked(size + 1), after).ToListAsync(token);
        token.ThrowIfCancellationRequested();
        var items = fetched.Take(size).ToArray();
        return new GalleryPage(Array.AsReadOnly(items), fetched.Count > size
            ? new GalleryPagePosition(owner, "Hobbies", items[^1].CreatedAtUtc, items[^1].Id) : null);
    }

    [Fact]
    public async Task EfSeek_InvalidInputsAndPreCancellationNeverOpenAConnection()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        var valid = new GalleryPagePosition(Owner, "Hobbies", CreatedAt, KeysetId(1));
        foreach (var shape in new[] { EfSeekShape.B, EfSeekShape.C })
        {
            foreach (var after in new[]
            {
                valid with { OwnerModuleProfileId = OtherOwner }, valid with { Module = "Social" },
                valid with { Module = null! }, valid with { Id = Guid.Empty }, valid with { Timestamp = default },
                valid with { Timestamp = DateTimeOffset.MaxValue }, valid with { Timestamp = CreatedAt.AddTicks(1) },
                valid with { Timestamp = CreatedAt.ToOffset(TimeSpan.FromHours(1)) }
            })
            {
                var error = await Assert.ThrowsAsync<ArgumentException>(() => ReadEfSeekAsync(context, shape, Owner, 1, 3, after));
                Assert.DoesNotContain(OtherOwner.ToString(), error.Message);
                Assert.DoesNotContain("synthetic/", error.Message);
            }
            await Assert.ThrowsAsync<ArgumentException>(() => ReadEfSeekAsync(context, shape, Guid.Empty, 1, 3));
            foreach (var size in new[] { -1, 0, 4, int.MaxValue })
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadEfSeekAsync(context, shape, Owner, size, 3));
            foreach (var bound in new[] { -1, 0, int.MaxValue })
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadEfSeekAsync(context, shape, Owner, 1, bound));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReadEfSeekAsync(context, shape, Owner, 1, 3, token: cancellation.Token));
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        }
    }

    [MapCapturePostgresFact]
    public Task EfSeek_AllPositionsAndLargeTiesMatchTheActualRepositoryInOneCommand() => WithDatabaseAsync(async connection =>
    {
        var rows = Enumerable.Range(1, 360).Select(id =>
            KeysetMedia(id, CreatedAt.AddTicks(19).AddSeconds(-(id / 120)))).ToArray();
        await SeedKeysetAsync(connection, [.. rows, KeysetMedia(500, CreatedAt, OtherOwner),
            KeysetMedia(501, CreatedAt, module: "Social"), KeysetMedia(502, CreatedAt, deleted: true)]);
        await using var reference = CreateContext(connection);
        var ordered = await reference.GalleryMedia.AsNoTracking()
            .Where(row => row.OwnerModuleProfileId == Owner && row.Module == "Hobbies" && !row.IsDeleted)
            .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).ToListAsync();
        var positions = new GalleryPagePosition?[] { null, Position(ordered[19]), Position(ordered[179]), Position(ordered[323]) };
        foreach (var after in positions)
        {
            var baseline = await new GalleryRepository<HobbiesGalleryDbContext>(reference).GetOwnerGalleryPageAsync(Owner, 50, 64, after);
            foreach (var shape in new[] { EfSeekShape.B, EfSeekShape.C })
            {
                var capture = new KeysetCommandCapture();
                await using var context = CreateContext(connection, capture);
                var page = await ReadEfSeekAsync(context, shape, Owner, 50, 64, after);
                Assert.Equal(1, capture.CommandCount);
                Assert.Equal(baseline.Items.Select(row => row.Id), page.Items.Select(row => row.Id));
                Assert.Equal(baseline.Next, page.Next);
                var replay = await ReadEfSeekAsync(context, shape, Owner, 50, 64, after);
                Assert.Equal(page.Items.Select(row => row.Id), replay.Items.Select(row => row.Id));
                Assert.Equal(page.Next, replay.Next);
                Assert.Empty(context.ChangeTracker.Entries());
                if (after is not null) AssertEfSeekSql(shape, capture.Sql!);
            }
        }
        foreach (var shape in new[] { EfSeekShape.B, EfSeekShape.C })
        {
            var seen = new List<Guid>();
            GalleryPagePosition? after = null;
            do
            {
                var page = await ReadEfSeekAsync(reference, shape, Owner, 50, 64, after);
                seen.AddRange(page.Items.Select(row => row.Id));
                Assert.True(seen.Count <= ordered.Count);
                after = page.Next;
            } while (after is not null);
            Assert.Equal(ordered.Select(row => row.Id), seen);
            Assert.Equal(seen.Count, seen.Distinct().Count());
        }
    });

    private static GalleryPagePosition Position(GalleryMedia row) => new(Owner, "Hobbies", row.CreatedAtUtc, row.Id);

    private static void AssertEfSeekSql(EfSeekShape shape, string sql)
    {
        Assert.Contains("OwnerModuleProfileId", sql);
        Assert.Contains("'Hobbies'", sql);
        Assert.Contains("IsDeleted", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.DoesNotContain("OFFSET", sql);
        Assert.Contains(">", sql);
        if (shape == EfSeekShape.B) Assert.Contains("<=", sql);
        if (shape == EfSeekShape.C)
        {
            Assert.Contains("UNION ALL", sql);
            Assert.Equal(3, sql.Split("LIMIT", StringSplitOptions.None).Length - 1);
            Assert.Equal(2, sql.Split("'Hobbies'", StringSplitOptions.None).Length - 1);
        }
    }

    [MapCapturePostgresFact]
    public Task EfSeek_CancellationAfterReaderCreationDoesNotReturnASuccessfulPage() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        foreach (var shape in new[] { EfSeekShape.B, EfSeekShape.C })
        {
            using var cancellation = new CancellationTokenSource();
            var gate = new CancelEfSeekReader(cancellation);
            await using var context = CreateContext(connection, gate);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ReadEfSeekAsync(context, shape, Owner, 2, 3,
                    new(Owner, "Hobbies", CreatedAt.AddMinutes(2), KeysetId(1)), cancellation.Token));
            Assert.True(gate.ReachedReader);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(context.ChangeTracker.Entries());
        }
    });


    [MapCapturePostgresFact]
    public Task EfSeek_CapturedCommandsHaveComparablePlansWithBothIndexes() => WithDatabaseAsync(async connection =>
    {
        var samples = new List<(string Dataset, string Index, string Position, EfSeekShape Shape,
            double Milliseconds, long Hits, long Reads, long Removed)>();
        foreach (var allTies in new[] { false, true })
        {
            await using var setup = CreateContext(connection);
            await setup.Database.ExecuteSqlRawAsync("TRUNCATE andril_hobbies.\"GalleryMedia\"");
            var ties = allTies ? 50000 : 1000;
            await setup.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO andril_hobbies.""GalleryMedia""
(""Id"",""OwnerModuleProfileId"",""Module"",""Title"",""Description"",""Category"",""MediaType"",""StorageObjectKey"",""IsPrivate"",""CreatedAtUtc"",""IsDeleted"")
SELECT md5('ef-seek:'||r::text)::uuid,{Owner},
 CASE WHEN r%10=0 THEN 'Social' ELSE 'Hobbies' END,'Synthetic',repeat('Synthetic ',8),'Synthetic','Photo','synthetic/private.jpg',true,
 {CreatedAt} - (((r-1)/{ties})*interval '1 second'),r%100<50
FROM generate_series(1,50000) r");
            await setup.Database.ExecuteSqlRawAsync("ANALYZE andril_hobbies.\"GalleryMedia\"");
            var ordered = await setup.GalleryMedia.AsNoTracking()
                .Where(row => row.OwnerModuleProfileId == Owner && row.Module == "Hobbies" && !row.IsDeleted)
                .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).ToListAsync();
            var positions = new (string Name, int Anchor)[] { ("first", -1), ("early", 19),
                ("middle", ordered.Count / 2), ("late", ordered.Count * 9 / 10) };
            foreach (var round in new[] { 0, 1 })
            foreach (var candidate in round == 0 ? new[] { false, true } : new[] { true, false })
            {
                await setup.Database.ExecuteSqlRawAsync(candidate
                    ? "CREATE INDEX IF NOT EXISTS gallery_ef_seek_candidate ON andril_hobbies.\"GalleryMedia\" (\"OwnerModuleProfileId\",\"Module\",\"IsDeleted\",\"CreatedAtUtc\" DESC,\"Id\" ASC)"
                    : "DROP INDEX IF EXISTS andril_hobbies.gallery_ef_seek_candidate");
                await setup.Database.ExecuteSqlRawAsync("ANALYZE andril_hobbies.\"GalleryMedia\"");
                foreach (var position in round == 0 ? positions : positions.Reverse())
                {
                    var after = position.Anchor < 0 ? null : Position(ordered[position.Anchor]);
                    var expected = ordered.Skip(position.Anchor + 1).Take(50).Select(row => row.Id).ToArray();
                    foreach (var shape in round == 0 ? new[] { EfSeekShape.A, EfSeekShape.B, EfSeekShape.C }
                        : new[] { EfSeekShape.C, EfSeekShape.B, EfSeekShape.A })
                    {
                        var capture = new KeysetCommandCapture();
                        await using var context = CreateContext(connection, capture);
                        var page = shape == EfSeekShape.A
                            ? await new GalleryRepository<HobbiesGalleryDbContext>(context).GetOwnerGalleryPageAsync(Owner, 50, 64, after)
                            : await ReadEfSeekAsync(context, shape, Owner, 50, 64, after);
                        Assert.Equal(expected, page.Items.Select(row => row.Id));
                        Assert.Equal(1, capture.CommandCount);
                        Assert.Empty(context.ChangeTracker.Entries());
                        if (after is not null && shape != EfSeekShape.A) AssertEfSeekSql(shape, capture.Sql!);
                        await using var database = new NpgsqlConnection(connection);
                        await database.OpenAsync();
                        for (var repetition = 0; repetition < 4; repetition++)
                        {
                            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + capture.Sql, database);
                            foreach (var parameter in capture.Parameters)
                                explain.Parameters.AddWithValue(parameter.Name, parameter.Value!);
                            using var document = JsonDocument.Parse((await explain.ExecuteScalarAsync())!.ToString()!);
                            var root = document.RootElement[0];
                            var node = root.GetProperty("Plan");
                            Assert.Equal(51, node.GetProperty("Actual Rows").GetInt32());
                            var nodes = new List<JsonElement>();
                            CollectEfPlanNodes(node, nodes);
                            var removed = nodes.Max(item => EfPlanNumber(item, "Rows Removed by Filter"));
                            if (repetition > 0)
                                samples.Add((allTies ? "one_timestamp" : "groups_1000", candidate ? "candidate" : "existing",
                                    position.Name, shape, root.GetProperty("Execution Time").GetDouble(),
                                    EfPlanNumber(node, "Shared Hit Blocks"), EfPlanNumber(node, "Shared Read Blocks"), removed));
                            if (round == 0 && candidate && position.Name == "late" && repetition == 1)
                            {
                                _keysetOutput.WriteLine($"SQL {allTies} {shape}: {capture.Sql}");
                                _keysetOutput.WriteLine(JsonSerializer.Serialize(nodes.Select(item => new
                                {
                                    Node = item.GetProperty("Node Type").GetString(),
                                    IndexCond = item.TryGetProperty("Index Cond", out var cond) ? cond.GetString() : null,
                                    Filter = item.TryGetProperty("Filter", out var filter) ? filter.GetString() : null,
                                    Rows = EfPlanNumber(item, "Actual Rows"),
                                    Removed = EfPlanNumber(item, "Rows Removed by Filter"),
                                    Sort = item.TryGetProperty("Sort Key", out var sort) ? sort.ToString() : null,
                                    Hits = EfPlanNumber(item, "Shared Hit Blocks")
                                })));
                            }
                        }
                    }
                }
            }
            await using var sizeQuery = new NpgsqlConnection(connection);
            await sizeQuery.OpenAsync();
            // Candidate was dropped in reverse round. Recreate only in this disposable fixture to measure size.
            await setup.Database.ExecuteSqlRawAsync("CREATE INDEX gallery_ef_seek_candidate ON andril_hobbies.\"GalleryMedia\" (\"OwnerModuleProfileId\",\"Module\",\"IsDeleted\",\"CreatedAtUtc\" DESC,\"Id\" ASC)");
            await using var sizes = new NpgsqlCommand("SELECT indexrelname,pg_relation_size(indexrelid) FROM pg_stat_user_indexes WHERE schemaname='andril_hobbies' AND relname='GalleryMedia' ORDER BY indexrelname", sizeQuery);
            await using (var reader = await sizes.ExecuteReaderAsync())
                while (await reader.ReadAsync()) _keysetOutput.WriteLine($"SIZE {allTies}: {reader.GetString(0)}={reader.GetInt64(1)}");
            await setup.Database.ExecuteSqlRawAsync("DROP INDEX andril_hobbies.gallery_ef_seek_candidate");
        }
        _keysetOutput.WriteLine("dataset,index,position,shape,median_ms,max_hits,max_reads,max_filtered");
        foreach (var group in samples.GroupBy(sample => (sample.Dataset, sample.Index, sample.Position, sample.Shape)))
        {
            var times = group.Select(sample => sample.Milliseconds).Order().ToArray();
            _keysetOutput.WriteLine($"{group.Key.Dataset},{group.Key.Index},{group.Key.Position},{group.Key.Shape},{(times[2] + times[3]) / 2:F3},{group.Max(sample => sample.Hits)},{group.Max(sample => sample.Reads)},{group.Max(sample => sample.Removed)}");
        }
        Assert.Equal(288, samples.Count);
    });

    private static long EfPlanNumber(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) ? value.GetInt64() : 0;

    private static void CollectEfPlanNodes(JsonElement node, List<JsonElement> nodes)
    {
        nodes.Add(node);
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) CollectEfPlanNodes(child, nodes);
    }

    private sealed class CancelEfSeekReader(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool ReachedReader { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            ReachedReader = true;
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [MapCapturePostgresFact]
    public Task EfSeek_UnavailableDatabasePropagatesRatherThanReturningAnEmptyPage() => WithDatabaseAsync(async connection =>
    {
        var settings = new NpgsqlConnectionStringBuilder(connection) { Database = $"andril_m3_missing_{Guid.NewGuid():N}" };
        foreach (var shape in new[] { EfSeekShape.B, EfSeekShape.C })
        {
            await using var context = CreateContext(settings.ConnectionString);
            var error = await Assert.ThrowsAsync<PostgresException>(() => ReadEfSeekAsync(context, shape, Owner, 1, 3,
                new(Owner, "Hobbies", CreatedAt.AddMinutes(2), KeysetId(1))));
            Assert.Equal(PostgresErrorCodes.InvalidCatalogName, error.SqlState);
        }
    });
}
