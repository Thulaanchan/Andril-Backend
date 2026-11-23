using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class GalleryPersistenceTests
{
    // Reuse the proven scenarios unchanged, selecting the actual repository instead of the prototype.
    private async Task ExerciseRepositoryKeysetAsync(Func<Task> scenario)
    {
        _exerciseRepositoryKeyset = true;
        try { await scenario(); }
        finally { _exerciseRepositoryKeyset = false; }
    }

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_EqualTimestampsCrossPageBoundariesInIdOrder() =>
        ExerciseRepositoryKeysetAsync(Keyset_EqualTimestampsCrossPageBoundariesInIdOrder);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows() =>
        ExerciseRepositoryKeysetAsync(Keyset_CompleteMixedTimestampTraversalHasNoDuplicatesOrMissingRows);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_OwnerFilterCannotBeReplacedByACursorPosition() =>
        ExerciseRepositoryKeysetAsync(Keyset_OwnerFilterCannotBeReplacedByACursorPosition);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_WrongModuleRowsCannotFillThePageOrItsLookahead() =>
        ExerciseRepositoryKeysetAsync(Keyset_WrongModuleRowsCannotFillThePageOrItsLookahead);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries() =>
        ExerciseRepositoryKeysetAsync(Keyset_SoftDeletedRowsAreExcludedBeforeAndBetweenPageQueries);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_ContinuationDoesNotRequireTheAnchorRowToExist() =>
        ExerciseRepositoryKeysetAsync(Keyset_ContinuationDoesNotRequireTheAnchorRowToExist);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation() =>
        ExerciseRepositoryKeysetAsync(Keyset_InsertBeforeCursorRequiresRefreshWithoutShiftingContinuation);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_InsertAfterCursorAppearsInTheLiveContinuation() =>
        ExerciseRepositoryKeysetAsync(Keyset_InsertAfterCursorAppearsInTheLiveContinuation);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks() =>
        ExerciseRepositoryKeysetAsync(Keyset_PositionUsesStoredUtcTimestampPrecisionRatherThanInputTicks);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering() =>
        ExerciseRepositoryKeysetAsync(Keyset_UuidComparisonAndExecutedSqlMatchPostgreSqlReferenceOrdering);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_InFlightDatabaseCancellationPropagatesTheSuppliedToken() =>
        ExerciseRepositoryKeysetAsync(Keyset_InFlightDatabaseCancellationPropagatesTheSuppliedToken);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_DatabaseFailureCannotBecomeAnEmptySuccessfulPage() =>
        ExerciseRepositoryKeysetAsync(Keyset_DatabaseFailureCannotBecomeAnEmptySuccessfulPage);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor() =>
        ExerciseRepositoryKeysetAsync(Keyset_LookaheadDoesNotSkipTheNextRecordOrProduceATerminalCursor);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities() =>
        ExerciseRepositoryKeysetAsync(Keyset_ReadsDoNotMutateMediaPreferencesOrTrackedSourceEntities);

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_RepeatedPositionIsReadOnlyAndReturnsTheSamePage() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection);
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var first = await repository.GetOwnerGalleryPageAsync(Owner, 2, 3);
        Assert.NotNull(first.Next);
        var next = await repository.GetOwnerGalleryPageAsync(Owner, 2, 3, first.Next);
        var repeated = await repository.GetOwnerGalleryPageAsync(Owner, 2, 3, first.Next);
        Assert.Equal(new[] { KeysetId(3), KeysetId(4) }, next.Items.Select(row => row.Id));
        Assert.Equal(next.Items.Select(row => row.Id), repeated.Items.Select(row => row.Id));
        Assert.Equal(next.Next, repeated.Next);
        var final = await repository.GetOwnerGalleryPageAsync(Owner, 2, 3, next.Next);
        Assert.Null(final.Next);
        Assert.Equal(6, await context.GalleryMedia.CountAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_InvalidInputsAreRejectedAgainstPostgreSqlWithoutChangingRows() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection);
        await AssertInvalidRepositoryKeysetInputsAsync(context);
        Assert.Equal(6, await context.GalleryMedia.CountAsync());
        Assert.Empty(context.ChangeTracker.Entries());
    });

    [Fact]
    public async Task RepositoryKeyset_InvalidInputsAreRejectedBeforeOpeningAConnection()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        await AssertInvalidRepositoryKeysetInputsAsync(context);
    }

    private static async Task AssertInvalidRepositoryKeysetInputsAsync(HobbiesGalleryDbContext context)
    {
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var position = new GalleryPagePosition(Owner, "Hobbies", CreatedAt, KeysetId(1));
        var invalid = new[]
        {
            position with { OwnerModuleProfileId = OtherOwner },
            position with { OwnerModuleProfileId = Guid.Empty },
            position with { Module = "Social" },
            position with { Module = "hobbies" },
            position with { Module = null! },
            position with { Id = Guid.Empty },
            position with { Timestamp = default },
            position with { Timestamp = DateTimeOffset.MaxValue },
            position with { Timestamp = CreatedAt.ToOffset(TimeSpan.FromHours(1)) },
            position with { Timestamp = CreatedAt.AddTicks(1) }
        };
        foreach (var after in invalid)
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                repository.GetOwnerGalleryPageAsync(Owner, 1, 3, after));
            Assert.Equal("after", error.ParamName);
            Assert.DoesNotContain(OtherOwner.ToString(), error.Message);
            Assert.DoesNotContain("StorageObjectKey", error.Message);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetOwnerGalleryPageAsync(Guid.Empty, 1, 3));
        foreach (var size in new[] { int.MinValue, -1, 0, 4, int.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetOwnerGalleryPageAsync(Owner, size, 3));
        foreach (var bound in new[] { int.MinValue, -1, 0, int.MaxValue })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetOwnerGalleryPageAsync(Owner, 1, bound));
    }

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_PreCancellationDoesNotReadOrMutatePostgreSql() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        await using var context = CreateContext(connection, new RejectConnectionOpen());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GalleryRepository<HobbiesGalleryDbContext>(context)
                .GetOwnerGalleryPageAsync(Owner, 1, 3, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await using var fresh = CreateContext(connection);
        Assert.Equal(6, await fresh.GalleryMedia.CountAsync());
    });

    [MapCapturePostgresFact]
    public Task RepositoryKeyset_ExplainShowsTheExistingIndexAndSortWithoutAddingAnIndex() => WithDatabaseAsync(async connection =>
    {
        await SeedKeysetAsync(connection, KeysetRows());
        var capture = new KeysetCommandCapture();
        await using var context = CreateContext(connection, capture);
        var repository = new GalleryRepository<HobbiesGalleryDbContext>(context);
        var first = await repository.GetOwnerGalleryPageAsync(Owner, 1, 3);
        await repository.GetOwnerGalleryPageAsync(Owner, 1, 3, first.Next);
        Assert.NotNull(capture.Sql);
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        // Replay the actual captured SELECT with its bound values, prefixed only with fixed EXPLAIN.
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + capture.Sql, database);
        foreach (var parameter in capture.Parameters)
            explain.Parameters.AddWithValue(parameter.Name, parameter.Value!);
        var lines = new List<string>();
        await using (var rows = await explain.ExecuteReaderAsync())
            while (await rows.ReadAsync()) lines.Add(rows.GetString(0));
        var plan = string.Join(Environment.NewLine, lines);
        _keysetOutput.WriteLine(plan);
        Assert.Contains("Limit", plan);
        Assert.Contains("Sort", plan);
        Assert.Contains("CreatedAtUtc", plan);
        Assert.Contains("Id", plan);
        // Small fixtures do not establish a production plan or performance guarantee.
    });
}
