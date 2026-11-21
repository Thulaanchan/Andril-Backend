using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Andril.Economy.FlightPoints;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class FlightPointsLedgerPersistenceTests
{
    private const string OfflineConnection = "Host=127.0.0.1;Database=andril_model_only;Username=unused";
    private const string CheckPrefix = "ck_flight_point_ledger_";
    private const string PrimaryKey = "pk_flight_point_ledger";
    private const string ReversalIndex = "ux_flight_point_ledger_reversed_entry";
    private const string ReversalForeignKey = "fk_flight_point_ledger_reversed_entry";
    private static readonly Guid FirstId = new("10000000-0000-4000-8000-000000000001");
    private static readonly Guid SecondId = new("10000000-0000-4000-8000-000000000002");
    private static readonly Guid ThirdId = new("10000000-0000-4000-8000-000000000003");
    private static readonly Guid Owner = new("20000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherOwner = new("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Contribution = new("30000000-0000-4000-8000-000000000001");
    private static readonly Guid Attraction = new("40000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public async Task GetNetPoints_RejectsEmptyOwnerAndHonorsCancellationWithoutUsingDatabase()
    {
        var probe = new OfflineLedgerQueryProbe();
        await using var context = CreateContext(interceptor: probe);
        var repository = Repository(context);

        await Assert.ThrowsAsync<ArgumentException>("ownerHobbiesModuleProfileId",
            () => repository.GetNetPointsAsync(Guid.Empty));
        var token = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetNetPointsAsync(Owner, token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Equal(0, probe.OpenAttempts);
        AssertClosed(context);
    }

    [Fact]
    public async Task GetNetPoints_TranslatesOwnerFilterAndWideSignedSum()
    {
        var probe = new OfflineLedgerQueryProbe();
        await using var context = CreateContext(interceptor: probe);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Repository(context).GetNetPointsAsync(Owner));

        Assert.Same(probe.Stop, error);
        Assert.NotNull(probe.Sql);
        Assert.Matches(
            @"(?i)^SELECT COALESCE\(sum\(\w+\.points_delta::bigint\), 0\.0\)::bigint\s+FROM andril_hobbies\.flight_point_ledger AS \w+\s+WHERE \w+\.owner_hobbies_module_profile_id = @\w+\s*$",
            probe.Sql);
        Assert.Equal(Owner, Assert.Single(probe.Parameters).Value);
        AssertClosed(context);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetHistory_RejectsInvalidRequestsAndCancellationBeforeOpeningDatabase()
    {
        var probe = new OfflineLedgerQueryProbe();
        await using var context = CreateContext(interceptor: probe);
        var repository = Repository(context);
        var query = new FlightPointsHistoryQuery(0, 1);

        await Assert.ThrowsAsync<ArgumentException>("ownerHobbiesModuleProfileId",
            () => repository.GetHistoryAsync(Guid.Empty, query));
        await Assert.ThrowsAsync<ArgumentNullException>("query", () => repository.GetHistoryAsync(Owner, null!));
        var token = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetHistoryAsync(Owner, query, token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Equal(0, probe.OpenAttempts);
        Assert.Null(probe.Sql);
        AssertClosed(context);
    }

    [Fact]
    public async Task GetHistory_ProjectsAuthorizedColumnsAndPaginatesForOwnerInSql()
    {
        var probe = new OfflineLedgerQueryProbe();
        await using var context = CreateContext(interceptor: probe);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Repository(context).GetHistoryAsync(Owner, new FlightPointsHistoryQuery(17, 23)));

        Assert.Same(probe.Stop, error);
        Assert.NotNull(probe.Sql);
        var match = Regex.Match(probe.Sql,
            @"^SELECT f\.ledger_entry_id, f\.contribution_id, f\.attraction_id, f\.entry_type, f\.reward_type, f\.points_delta, f\.reversed_entry_id, f\.created_at_utc\s+" +
            @"FROM andril_hobbies\.flight_point_ledger AS f\s+WHERE f\.owner_hobbies_module_profile_id = @(?<owner>\w+)\s+" +
            @"ORDER BY f\.created_at_utc DESC, f\.ledger_entry_id\s+LIMIT @(?<limit>\w+) OFFSET @(?<offset>\w+)\s*$");
        Assert.True(match.Success, probe.Sql);
        Assert.Equal(3, probe.Parameters.Count);
        Assert.Equal(Owner, probe.Parameters[match.Groups["owner"].Value]);
        Assert.Equal(23, probe.Parameters[match.Groups["limit"].Value]);
        Assert.Equal(17, probe.Parameters[match.Groups["offset"].Value]);
        AssertClosed(context);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [MapCapturePostgresFact]
    public Task GetHistory_ReturnsReadOnlyPagesOfPersistedGrantsAndReversalsForOwner() =>
        WithDatabaseAsync(async connection =>
        {
            await using var writer = CreateContext(connection);
            await using var reader = CreateContext(connection);
            var ledger = Repository(writer);
            var history = Repository(reader);
            var page = new FlightPointsHistoryQuery(0, 2);
            Assert.Empty(await history.GetHistoryAsync(Owner, page));

            var first = Grant(FirstId);
            var second = Grant(SecondId, FlightPointsRewardType.FirstExplorer);
            var other = Grant(ThirdId, owner: OtherOwner);
            var reversal = Reverse(new Guid("10000000-0000-4000-8000-000000000004"), first);
            await ledger.AppendAsync([second, other, first]);
            await ledger.AppendAsync([reversal]);

            var pending = Grant(new Guid("10000000-0000-4000-8000-000000000005"));
            reader.FlightPointsLedgerEntries.Add(pending);
            var firstPage = await history.GetHistoryAsync(Owner, page);
            var secondPage = await history.GetHistoryAsync(Owner, new FlightPointsHistoryQuery(2, 2));
            Assert.Collection(firstPage,
                item => AssertHistoryItem(reversal, item),
                item => AssertHistoryItem(first, item));
            AssertHistoryItem(second, Assert.Single(secondPage));
            Assert.Empty(await history.GetHistoryAsync(Owner, new FlightPointsHistoryQuery(3, 2)));
            AssertHistoryItem(other, Assert.Single(await history.GetHistoryAsync(OtherOwner, page)));
            Assert.Throws<NotSupportedException>(() => ((IList<FlightPointsHistoryItem>)firstPage).Clear());
            var tracked = Assert.Single(reader.ChangeTracker.Entries());
            Assert.Same(pending, tracked.Entity);
            Assert.Equal(EntityState.Added, tracked.State);
        });

    [MapCapturePostgresFact]
    public Task GetHistory_PropagatesCancellationAndDatabaseFailure() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var query = new FlightPointsHistoryQuery(0, 1);
            var token = new CancellationToken(canceled: true);
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repository.GetHistoryAsync(Owner, query, token));
            Assert.Equal(token, canceled.CancellationToken);

            await context.Database.ExecuteSqlRawAsync("DROP TABLE andril_hobbies.flight_point_ledger");
            var failure = await Assert.ThrowsAsync<PostgresException>(() => repository.GetHistoryAsync(Owner, query));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, failure.SqlState);
        });

    [MapCapturePostgresFact]
    public Task GetNetPoints_SumsOnlyPersistedGrantsAndReversalsForRequestedOwner() =>
        WithDatabaseAsync(async connection =>
        {
            await using var writer = CreateContext(connection);
            await using var reader = CreateContext(connection);
            var ledger = Repository(writer);
            var totals = Repository(reader);
            var contributionGrant = Grant(FirstId);
            var explorerGrant = Grant(SecondId, FlightPointsRewardType.FirstExplorer);
            var otherGrant = Grant(ThirdId, FlightPointsRewardType.FirstExplorer, OtherOwner);

            Assert.Equal(0L, await totals.GetNetPointsAsync(Owner));
            await ledger.AppendAsync([contributionGrant, otherGrant]);
            Assert.Equal(5L, await totals.GetNetPointsAsync(Owner));
            Assert.Equal(10L, await totals.GetNetPointsAsync(OtherOwner));
            await ledger.AppendAsync([explorerGrant]);
            Assert.Equal(15L, await totals.GetNetPointsAsync(Owner));
            await ledger.AppendAsync([Reverse(new Guid("10000000-0000-4000-8000-000000000004"), contributionGrant)]);
            Assert.Equal(10L, await totals.GetNetPointsAsync(Owner));

            var pending = Grant(new Guid("10000000-0000-4000-8000-000000000005"));
            reader.FlightPointsLedgerEntries.Add(pending);
            await ledger.AppendAsync([Reverse(new Guid("10000000-0000-4000-8000-000000000006"), explorerGrant)]);
            Assert.Equal(0L, await totals.GetNetPointsAsync(Owner));
            Assert.Equal(10L, await totals.GetNetPointsAsync(OtherOwner));
            var tracked = Assert.Single(reader.ChangeTracker.Entries());
            Assert.Same(pending, tracked.Entity);
            Assert.Equal(EntityState.Added, tracked.State);
        });

    [MapCapturePostgresFact]
    public Task GetNetPoints_PropagatesCancellationAndDatabaseFailure() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var ledger = Repository(context);
            var token = new CancellationToken(canceled: true);
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ledger.GetNetPointsAsync(Owner, token));
            Assert.Equal(token, canceled.CancellationToken);

            await context.Database.ExecuteSqlRawAsync("DROP TABLE andril_hobbies.flight_point_ledger");
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ledger.GetNetPointsAsync(Owner));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, failure.SqlState);
        });

    [Fact]
    public void Model_RegistersLedgerWithAllTenColumnMappings()
    {
        using var context = CreateContext();
        var entity = context.FlightPointsLedgerEntries.EntityType;
        Assert.Same(context.Model.FindEntityType(typeof(FlightPointsLedgerEntry)), entity);
        Assert.Equal("flight_point_ledger", entity.GetTableName());
        Assert.Equal(10, entity.GetProperties().Count());
        var key = entity.FindPrimaryKey()!;
        Assert.Equal(PrimaryKey, key.GetName());
        var id = Assert.Single(key.Properties);
        Assert.Equal(nameof(FlightPointsLedgerEntry.LedgerEntryId), id.Name);
        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);

        foreach (var (property, column, type, nullable) in new[]
        {
            (nameof(FlightPointsLedgerEntry.LedgerEntryId), "ledger_entry_id", "uuid", false),
            (nameof(FlightPointsLedgerEntry.OwnerHobbiesModuleProfileId), "owner_hobbies_module_profile_id", "uuid", false),
            (nameof(FlightPointsLedgerEntry.ContributionId), "contribution_id", "uuid", false),
            (nameof(FlightPointsLedgerEntry.AttractionId), "attraction_id", "uuid", false),
            (nameof(FlightPointsLedgerEntry.EntryType), "entry_type", "integer", false),
            (nameof(FlightPointsLedgerEntry.RewardType), "reward_type", "integer", false),
            (nameof(FlightPointsLedgerEntry.PointsDelta), "points_delta", "integer", false),
            (nameof(FlightPointsLedgerEntry.IdempotencyKey), "idempotency_key", "text", false),
            (nameof(FlightPointsLedgerEntry.ReversedEntryId), "reversed_entry_id", "uuid", true),
            (nameof(FlightPointsLedgerEntry.CreatedAtUtc), "created_at_utc", "timestamp with time zone", false)
        })
        {
            var mapped = entity.FindProperty(property)!;
            Assert.Equal(column, mapped.GetColumnName());
            Assert.Equal(type, mapped.GetColumnType());
            Assert.Equal(nullable, mapped.IsNullable);
        }

        var entryType = entity.FindProperty(nameof(FlightPointsLedgerEntry.EntryType))!.GetTypeMapping().Converter!;
        var rewardType = entity.FindProperty(nameof(FlightPointsLedgerEntry.RewardType))!.GetTypeMapping().Converter!;
        Assert.Equal(1, entryType.ConvertToProvider(FlightPointsLedgerEntryType.Grant));
        Assert.Equal(2, entryType.ConvertToProvider(FlightPointsLedgerEntryType.Reversal));
        Assert.Equal(1, rewardType.ConvertToProvider(FlightPointsRewardType.FirstEligibleContribution));
        Assert.Equal(2, rewardType.ConvertToProvider(FlightPointsRewardType.FirstExplorer));
        Assert.Equal(typeof(int), entity.FindProperty(nameof(FlightPointsLedgerEntry.PointsDelta))!.ClrType);
        AssertClosed(context);
    }

    [Fact]
    public void Model_GeneratesLedgerChecksFilteredIndexAndRestrictedForeignKey()
    {
        using var context = CreateContext();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(FlightPointsLedgerEntry))!;
        var checks = entity.GetCheckConstraints().ToDictionary(check => check.Name!, check => check.Sql);
        var suffixes = new[]
        {
            "id", "owner_id", "contribution_id", "attraction_id", "idempotency_key",
            "entry_type", "reward_type", "points_delta", "reversal_reference"
        };
        var expected = suffixes.Select(suffix => CheckPrefix + suffix);
        Assert.Equal(expected.Order(StringComparer.Ordinal), checks.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("entry_type IN (1, 2)", checks[CheckPrefix + "entry_type"]);
        Assert.Equal("reward_type IN (1, 2)", checks[CheckPrefix + "reward_type"]);
        var index = Assert.Single(entity.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal(ReversalIndex, index.GetDatabaseName());
        Assert.Equal("entry_type = 2", index.GetFilter());
        Assert.Equal(nameof(FlightPointsLedgerEntry.ReversedEntryId), Assert.Single(index.Properties).Name);
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Same(entity, foreignKey.PrincipalEntityType);
        Assert.Equal(nameof(FlightPointsLedgerEntry.ReversedEntryId), Assert.Single(foreignKey.Properties).Name);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(ReversalForeignKey, foreignKey.GetConstraintName());

        var ddl = context.Database.GenerateCreateScript();
        Assert.All(expected, name => Assert.Contains($"CONSTRAINT {name} CHECK", ddl));
        Assert.Contains("CREATE UNIQUE INDEX " + ReversalIndex + " ON andril_hobbies.flight_point_ledger (reversed_entry_id) WHERE entry_type = 2", ddl);
        Assert.Contains("REFERENCES andril_hobbies.flight_point_ledger (ledger_entry_id) ON DELETE RESTRICT", ddl);
        Assert.Contains("points_delta = 5", ddl);
        Assert.Contains("points_delta = 10", ddl);
        Assert.Contains("points_delta = -5", ddl);
        Assert.Contains("points_delta = -10", ddl);
        AssertClosed(context);
    }

    [Fact]
    public void Model_PreservesMapMappingsAndCompilesOwnerScopedLedgerQuery()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var attraction = model.FindEntityType(typeof(MapAttraction))!;
        Assert.Equal("map_attractions", attraction.GetTableName());
        Assert.Equal("pk_map_attractions", attraction.FindPrimaryKey()!.GetName());
        Assert.Equal(4, attraction.GetCheckConstraints().Count());
        var session = model.FindEntityType(typeof(MapContributionCaptureSession))!;
        Assert.Equal("map_contribution_capture_sessions", session.GetTableName());
        Assert.Equal(nameof(MapContributionCaptureSession.SessionId), Assert.Single(session.FindPrimaryKey()!.Properties).Name);
        Assert.Equal("ix_map_capture_owner_attraction", Assert.Single(session.GetIndexes()).GetDatabaseName());
        Assert.Equal(4, session.GetCheckConstraints().Count());
        var contribution = model.FindEntityType(typeof(MapContribution))!;
        Assert.Equal("map_contributions", contribution.GetTableName());
        Assert.Equal("pk_map_contributions", contribution.FindPrimaryKey()!.GetName());
        Assert.Equal("jsonb", contribution.FindProperty(nameof(MapContribution.LocationEvidence))!.GetColumnType());
        Assert.Equal(8, contribution.GetCheckConstraints().Count());
        Assert.Contains(contribution.GetCheckConstraints(), check => check.Name == "ck_map_contribution_moderation_status"
            && check.Sql == "moderation_status = 1");

        var sql = context.FlightPointsLedgerEntries.AsNoTracking()
            .Where(entry => entry.LedgerEntryId == FirstId && entry.OwnerHobbiesModuleProfileId == Owner)
            .Take(2).ToQueryString();
        Assert.Contains("WHERE", sql);
        var predicate = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains(".ledger_entry_id =", predicate);
        Assert.Contains(" AND ", predicate);
        Assert.Contains(".owner_hobbies_module_profile_id =", predicate);
        AssertClosed(context);
    }

    [Fact]
    public async Task Append_RejectsInvalidCollectionsBeforeTracking()
    {
        await using var context = CreateContext();
        var repository = Repository(context);
        var grant = Grant(FirstId);
        await Assert.ThrowsAsync<ArgumentNullException>("entries", () => repository.AppendAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>("entries", () => repository.AppendAsync([]));
        await Assert.ThrowsAsync<ArgumentException>("entries", () => repository.AppendAsync([grant, null!]));
        await Assert.ThrowsAsync<ArgumentException>("entries", () => repository.AppendAsync([grant, Grant(FirstId)]));
        await Assert.ThrowsAsync<ArgumentException>("entries", () => repository.AppendAsync([
            Reverse(SecondId, grant), Reverse(ThirdId, grant)]));
        Assert.Empty(context.ChangeTracker.Entries());
        AssertClosed(context);
    }

    [Fact]
    public async Task Repository_ObservesPreCancellationBeforeEmptyInputShortCircuits()
    {
        await using var context = CreateContext();
        var repository = Repository(context);
        var token = new CancellationToken(canceled: true);
        var append = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AppendAsync([], token));
        var find = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindOwnedAsync(Guid.Empty, Owner, token));
        Assert.Equal(token, append.CancellationToken);
        Assert.Equal(token, find.CancellationToken);
        Assert.Null(await repository.FindOwnedAsync(Guid.Empty, Owner));
        Assert.Null(await repository.FindOwnedAsync(FirstId, Guid.Empty));
        Assert.Empty(context.ChangeTracker.Entries());
        AssertClosed(context);
    }

    [Fact]
    public async Task Append_RejectsTrackedInputAndUnrelatedPendingChangesWithoutDetachingThem()
    {
        await using var context = CreateContext();
        var repository = Repository(context);
        var attraction = context.MapAttractions.Add(CreateAttraction());
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync([Grant(FirstId)]));
        Assert.Contains("pending changes", rejection.Message);
        Assert.Equal(EntityState.Added, attraction.State);
        Assert.Same(attraction.Entity, Assert.Single(context.ChangeTracker.Entries()).Entity);

        attraction.State = EntityState.Unchanged;
        var tracked = context.FlightPointsLedgerEntries.Attach(Grant(FirstId));
        rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync([tracked.Entity]));
        Assert.Contains("already tracked", rejection.Message);
        Assert.Equal(EntityState.Unchanged, tracked.State);
        Assert.Equal(EntityState.Unchanged, attraction.State);
        AssertClosed(context);
    }

    [Fact]
    public async Task Append_RejectsDisabledTransactionsAmbientTransactionsAndRetryStrategiesOffline()
    {
        await using var context = CreateContext();
        var repository = Repository(context);
        var batch = new[] { Grant(FirstId) };
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync(batch));
        Assert.Contains("transaction-enabled relational context", rejection.Message);
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;

        using (var ambient = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled))
        {
            rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync(batch));
            Assert.Contains("ambient transaction", rejection.Message);
        }

        await using var retrying = CreateContext(retrying: true);
        rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(retrying).AppendAsync(batch));
        Assert.Contains("non-retrying execution strategy", rejection.Message);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(retrying.ChangeTracker.Entries());
        AssertClosed(context);
        AssertClosed(retrying);
    }

    [MapCapturePostgresFact]
    public Task Append_CombinedGrantsRoundTripThroughFreshOwnerScopedContext() =>
        WithDatabaseAsync(async connection =>
        {
            var first = Grant(FirstId);
            var explorer = Grant(SecondId, FlightPointsRewardType.FirstExplorer);
            await using (var writer = CreateContext(connection))
            {
                await Repository(writer).AppendAsync([first, explorer]);
                Assert.Empty(writer.ChangeTracker.Entries());
            }

            await using var reader = CreateContext(connection);
            var repository = Repository(reader);
            var firstRead = await repository.FindOwnedAsync(FirstId, Owner);
            var explorerRead = await repository.FindOwnedAsync(SecondId, Owner);
            AssertEntry(first, firstRead);
            AssertEntry(explorer, explorerRead);
            Assert.NotSame(first, firstRead);
            Assert.NotSame(explorer, explorerRead);
            Assert.Equal(5, firstRead!.PointsDelta);
            Assert.Equal(10, explorerRead!.PointsDelta);
            Assert.Equal(15, firstRead.PointsDelta + explorerRead.PointsDelta);
            Assert.Equal(2, await reader.FlightPointsLedgerEntries.CountAsync());
            Assert.Null(await repository.FindOwnedAsync(FirstId, OtherOwner));
            Assert.Null(await repository.FindOwnedAsync(ThirdId, Owner));
            Assert.Null(await repository.FindOwnedAsync(Guid.Empty, Owner));
            Assert.Null(await repository.FindOwnedAsync(FirstId, Guid.Empty));
            Assert.Empty(reader.ChangeTracker.Entries());
        });

    [MapCapturePostgresFact]
    public Task Append_PrimaryKeyFailureRollsBackEarlierInsertAndPreservesOtherTracking() =>
        WithDatabaseAsync(async connection =>
        {
            var original = Grant(SecondId);
            await using (var seed = CreateContext(connection))
            {
                await Repository(seed).AppendAsync([original]);
                seed.MapAttractions.Add(CreateAttraction());
                await seed.SaveChangesAsync();
            }

            // Separate commands let the successful first INSERT reach PostgreSQL before the conflicting INSERT.
            var observer = new InsertObserver();
            await using var writer = CreateContext(connection, interceptor: observer, separateCommands: true);
            var attraction = await writer.MapAttractions.SingleAsync();
            var valid = Grant(FirstId);
            var duplicate = Grant(SecondId, FlightPointsRewardType.FirstExplorer);
            await AssertDatabaseErrorAsync(() => Repository(writer).AppendAsync([valid, duplicate]),
                PostgresErrorCodes.UniqueViolation, PrimaryKey, wrapped: true);
            Assert.Equal(1, observer.SuccessfulInserts);
            Assert.Same(attraction, Assert.Single(writer.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Unchanged, writer.Entry(attraction).State);
            Assert.Equal(EntityState.Detached, writer.Entry(valid).State);
            Assert.Equal(EntityState.Detached, writer.Entry(duplicate).State);
            Assert.Null(writer.Database.CurrentTransaction);

            await using var reader = CreateContext(connection);
            AssertEntry(original, await reader.FlightPointsLedgerEntries.AsNoTracking().SingleAsync());
        });

    [MapCapturePostgresFact]
    public Task Append_ConcurrentDuplicatePrimaryKeyHasOneWinner() =>
        WithDatabaseAsync(async connection =>
        {
            var first = Grant(FirstId);
            var second = Grant(FirstId, FlightPointsRewardType.FirstExplorer);
            var winner = await CompeteAsync(connection, first, second, PrimaryKey);
            await using var reader = CreateContext(connection);
            AssertEntry(winner, await reader.FlightPointsLedgerEntries.AsNoTracking().SingleAsync());
        });

    [MapCapturePostgresFact]
    public Task Append_ReversesBothRewardCategoriesAndKeepsOriginalsUnchanged() =>
        WithDatabaseAsync(async connection =>
        {
            var first = Grant(FirstId);
            var explorer = Grant(SecondId, FlightPointsRewardType.FirstExplorer);
            var firstReversal = Reverse(ThirdId, first);
            var explorerReversal = Reverse(OtherOwner, explorer);
            await using (var writer = CreateContext(connection))
            {
                var repository = Repository(writer);
                await repository.AppendAsync([first, explorer]);
                await repository.AppendAsync([firstReversal, explorerReversal]);
                Assert.Empty(writer.ChangeTracker.Entries());
            }

            await using var reader = CreateContext(connection);
            var entries = await reader.FlightPointsLedgerEntries.AsNoTracking().ToDictionaryAsync(entry => entry.LedgerEntryId);
            Assert.Equal(4, entries.Count);
            foreach (var expected in new[] { first, explorer, firstReversal, explorerReversal })
                AssertEntry(expected, entries[expected.LedgerEntryId]);
            foreach (var reversed in new[] { entries[ThirdId], entries[OtherOwner] })
            {
                var original = entries[reversed.ReversedEntryId!.Value];
                Assert.Equal(-original.PointsDelta, reversed.PointsDelta);
                Assert.Equal(original.OwnerHobbiesModuleProfileId, reversed.OwnerHobbiesModuleProfileId);
                Assert.Equal(original.ContributionId, reversed.ContributionId);
                Assert.Equal(original.AttractionId, reversed.AttractionId);
                Assert.Equal(original.RewardType, reversed.RewardType);
            }
        });

    [MapCapturePostgresFact]
    public Task Append_RejectsReversalsThatDoNotMatchAuthoritativeGrants() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var original = Grant(FirstId);
            await repository.AppendAsync([original]);
            var existingReversal = Reverse(SecondId, original);
            await repository.AppendAsync([existingReversal]);

            var candidates = new[]
            {
                Reverse(ThirdId, Grant(OtherOwner)),
                Reverse(ThirdId, Grant(SecondId)),
                Reverse(ThirdId, Grant(FirstId, owner: OtherOwner)),
                Reverse(ThirdId, Grant(FirstId, contribution: OtherOwner)),
                Reverse(ThirdId, Grant(FirstId, attraction: OtherOwner)),
                Reverse(ThirdId, Grant(FirstId, FlightPointsRewardType.FirstExplorer)),
                WithInvalidReversalDelta(Reverse(ThirdId, original))
            };
            foreach (var candidate in candidates)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync([candidate]));
                Assert.Contains("does not match a persisted original grant", error.Message);
                Assert.Empty(context.ChangeTracker.Entries());
            }

            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(
                () => repository.AppendAsync([Reverse(ThirdId, original)]));
            Assert.Contains("already been reversed", repeated.Message);
            Assert.Equal(2, await context.FlightPointsLedgerEntries.CountAsync());
            AssertEntry(original, await repository.FindOwnedAsync(FirstId, Owner));
            AssertEntry(existingReversal, await repository.FindOwnedAsync(SecondId, Owner));
        });

    [MapCapturePostgresFact]
    public Task Append_ConcurrentReversalsReachFilteredUniqueIndexAndKeepOriginal() =>
        WithDatabaseAsync(async connection =>
        {
            var grant = Grant(FirstId);
            await using (var seed = CreateContext(connection))
            {
                await Repository(seed).AppendAsync([grant]);
            }

            var winner = await CompeteAsync(connection, Reverse(SecondId, grant), Reverse(ThirdId, grant), ReversalIndex);
            await using var reader = CreateContext(connection);
            var recorded = await reader.FlightPointsLedgerEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntryType == FlightPointsLedgerEntryType.Reversal);
            AssertEntry(winner, recorded);
            AssertEntry(grant, await Repository(reader).FindOwnedAsync(FirstId, Owner));
            Assert.Equal(2, await reader.FlightPointsLedgerEntries.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Database_RejectsInvalidLedgerRowsAndProtectsForeignKey() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            await Repository(context).AppendAsync([Grant(FirstId)]);
            var row = new LedgerRow(SecondId, Owner, Contribution, Attraction, 1, 1, 5, "synthetic", null);
            var reversal = row with { EntryType = 2, Points = -5, ReversedId = FirstId };

            foreach (var (invalid, check) in new[]
            {
                (row with { Id = Guid.Empty }, "id"),
                (row with { Profile = Guid.Empty }, "owner_id"),
                (row with { Place = Guid.Empty }, "attraction_id"),
                (row with { Submission = Guid.Empty }, "contribution_id"),
                (row with { Key = " \t\r\n" }, "idempotency_key"),
                (row with { EntryType = 99 }, "entry_type"),
                (row with { Points = 0 }, "points_delta"),
                (row with { Points = 20 }, "points_delta"),
                (row with { RewardType = 2 }, "points_delta"),
                (row with { Points = -5 }, "points_delta"),
                (reversal with { Points = 5 }, "points_delta"),
                (row with { ReversedId = FirstId }, "reversal_reference"),
                (reversal with { ReversedId = null }, "reversal_reference"),
                (reversal with { ReversedId = Guid.Empty }, "reversal_reference"),
                (reversal with { ReversedId = SecondId }, "reversal_reference")
            })
            {
                await AssertDatabaseErrorAsync(() => InsertRowAsync(context, invalid),
                    PostgresErrorCodes.CheckViolation, CheckPrefix + check);
            }

            // An undefined reward type also violates the amount matrix. PostgreSQL can report either check.
            var undefined = await Assert.ThrowsAsync<PostgresException>(
                () => InsertRowAsync(context, row with { RewardType = 99 }));
            Assert.Equal(PostgresErrorCodes.CheckViolation, undefined.SqlState);
            Assert.Contains(undefined.ConstraintName, new[] { CheckPrefix + "reward_type", CheckPrefix + "points_delta" });
            await AssertDatabaseErrorAsync(() => InsertRowAsync(context, reversal with { ReversedId = ThirdId }),
                PostgresErrorCodes.ForeignKeyViolation, ReversalForeignKey);
            Assert.Equal(1, await InsertRowAsync(context, reversal));
            await AssertDatabaseErrorAsync(
                () => context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_hobbies.flight_point_ledger WHERE ledger_entry_id = {FirstId}"),
                PostgresErrorCodes.ForeignKeyViolation, ReversalForeignKey);
            Assert.Equal(2, await context.FlightPointsLedgerEntries.CountAsync());
        });

    [MapCapturePostgresFact]
    public Task Append_RejectsCallerTransactionAndKeepsUnrelatedTrackedEntryAfterSuccess() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var attraction = context.MapAttractions.Add(CreateAttraction());
            await context.SaveChangesAsync();
            await using (var caller = await context.Database.BeginTransactionAsync())
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync([Grant(FirstId)]));
                Assert.Contains("caller-owned", error.Message);
                Assert.Same(caller, context.Database.CurrentTransaction);
                Assert.Equal(0, await context.FlightPointsLedgerEntries.CountAsync());
                await caller.RollbackAsync();
            }

            var entry = Grant(FirstId);
            await repository.AppendAsync([entry]);
            Assert.Equal(EntityState.Detached, context.Entry(entry).State);
            Assert.Same(attraction.Entity, Assert.Single(context.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Unchanged, attraction.State);
            AssertEntry(entry, await repository.FindOwnedAsync(FirstId, Owner));
        });

    private static FlightPointsLedgerEntry Grant(
        Guid id, FlightPointsRewardType reward = FlightPointsRewardType.FirstEligibleContribution,
        Guid? owner = null, Guid? contribution = null, Guid? attraction = null) =>
        FlightPointsLedgerEntry.CreateGrant(id, owner ?? Owner, contribution ?? Contribution, attraction ?? Attraction,
            reward, $"grant-{id:N}", CreatedAt);

    private static FlightPointsLedgerEntry Reverse(Guid id, FlightPointsLedgerEntry grant) =>
        FlightPointsLedgerEntry.CreateReversal(id, grant, $"reversal-{id:N}", CreatedAt.AddMinutes(1));

    private static FlightPointsLedgerEntry WithInvalidReversalDelta(FlightPointsLedgerEntry entry)
    {
        // Only this mismatched delta is impossible to construct through the public factories.
        var constructor = typeof(FlightPointsLedgerEntry).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (FlightPointsLedgerEntry)constructor.Invoke([
            entry.LedgerEntryId, entry.OwnerHobbiesModuleProfileId, entry.ContributionId, entry.AttractionId,
            entry.EntryType, entry.RewardType, -10, entry.IdempotencyKey, entry.ReversedEntryId, entry.CreatedAtUtc]);
    }

    private static MapAttraction CreateAttraction() => MapAttraction.Create(Attraction, "Synthetic attraction", 0, 0);

    private static IFlightPointsLedgerRepository Repository(HobbiesMapDbContext context) =>
        new FlightPointsLedgerRepository<HobbiesMapDbContext>(context);

    private static void AssertClosed(HobbiesMapDbContext context) =>
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

    private static void AssertEntry(FlightPointsLedgerEntry expected, FlightPointsLedgerEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.LedgerEntryId, actual.LedgerEntryId);
        Assert.Equal(expected.OwnerHobbiesModuleProfileId, actual.OwnerHobbiesModuleProfileId);
        Assert.Equal(expected.ContributionId, actual.ContributionId);
        Assert.Equal(expected.AttractionId, actual.AttractionId);
        Assert.Equal(expected.EntryType, actual.EntryType);
        Assert.Equal(expected.RewardType, actual.RewardType);
        Assert.Equal(expected.PointsDelta, actual.PointsDelta);
        Assert.Equal(expected.IdempotencyKey, actual.IdempotencyKey);
        Assert.Equal(expected.ReversedEntryId, actual.ReversedEntryId);
        Assert.True(expected.CreatedAtUtc.EqualsExact(actual.CreatedAtUtc));
        Assert.Equal(TimeSpan.Zero, actual.CreatedAtUtc.Offset);
    }

    private static async Task AssertDatabaseErrorAsync(Func<Task> operation, string state, string constraint, bool wrapped = false)
    {
        var error = wrapped
            ? Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(operation)).InnerException)
            : await Assert.ThrowsAsync<PostgresException>(operation);
        Assert.Equal(state, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    private static async Task<FlightPointsLedgerEntry> CompeteAsync(
        string connection, FlightPointsLedgerEntry first, FlightPointsLedgerEntry second, string constraint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var gate = new SaveGate();
        await using var firstContext = CreateContext(connection, interceptor: gate);
        await using var secondContext = CreateContext(connection, interceptor: gate);
        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => Repository(firstContext).AppendAsync([first], deadline.Token)),
            Record.ExceptionAsync(() => Repository(secondContext).AppendAsync([second], deadline.Token)));
        Assert.Contains(errors, error => error is null);
        var failed = Assert.IsType<DbUpdateException>(Assert.Single(errors, error => error is not null));
        var postgres = Assert.IsType<PostgresException>(failed.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
        Assert.Empty(firstContext.ChangeTracker.Entries());
        Assert.Empty(secondContext.ChangeTracker.Entries());
        return errors[0] is null ? first : second;
    }

    private static void AssertHistoryItem(FlightPointsLedgerEntry expected, FlightPointsHistoryItem actual)
    {
        Assert.Equal(expected.LedgerEntryId, actual.LedgerEntryId);
        Assert.Equal(expected.ContributionId, actual.ContributionId);
        Assert.Equal(expected.AttractionId, actual.AttractionId);
        Assert.Equal(expected.EntryType, actual.EntryType);
        Assert.Equal(expected.RewardType, actual.RewardType);
        Assert.Equal(expected.PointsDelta, actual.PointsDelta);
        Assert.Equal(expected.ReversedEntryId, actual.ReversedEntryId);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
    }

    private sealed class OfflineLedgerQueryProbe : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public InvalidOperationException Stop { get; } = new("Offline ledger SQL captured.");
        public string? Sql { get; private set; }
        public IReadOnlyDictionary<string, object?> Parameters { get; private set; } =
            new Dictionary<string, object?>();
        public int OpenAttempts { get; private set; }

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            return ValueTask.FromResult(InterceptionResult.Suppress());
        }

        public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
        {
            using (result)
            {
                Sql = result.CommandText;
                Parameters = result.Parameters.Cast<DbParameter>()
                    .ToDictionary(parameter => parameter.ParameterName.TrimStart('@'), parameter => parameter.Value);
                throw Stop;
            }
        }
    }

    private sealed class SaveGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // Both repositories have completed their pre-insert queries before either write executes.
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class InsertObserver : DbCommandInterceptor
    {
        public int SuccessfulInserts { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO andril_hobbies.flight_point_ledger", StringComparison.Ordinal))
                SuccessfulInserts++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed record LedgerRow(Guid Id, Guid Profile, Guid Submission, Guid Place,
        int EntryType, int RewardType, int Points, string Key, Guid? ReversedId);

    private static Task<int> InsertRowAsync(HobbiesMapDbContext context, LedgerRow row) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_hobbies.flight_point_ledger (ledger_entry_id, owner_hobbies_module_profile_id, contribution_id,
                attraction_id, entry_type, reward_type, points_delta, idempotency_key, reversed_entry_id, created_at_utc)
            VALUES ({row.Id}, {row.Profile}, {row.Submission}, {row.Place}, {row.EntryType}, {row.RewardType},
                {row.Points}, {row.Key}, CAST({row.ReversedId} AS uuid), {CreatedAt.ToUniversalTime()})
            """);

    private static HobbiesMapDbContext CreateContext(string connection = OfflineConnection,
        IInterceptor? interceptor = null, bool retrying = false, bool separateCommands = false)
    {
        var options = new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(connection, postgres =>
        {
            if (retrying)
                postgres.EnableRetryOnFailure();
            if (separateCommands)
                postgres.MaxBatchSize(1);
        });
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new HobbiesMapDbContext(options.Options);
    }

    // Reuse the M3 opt-in attribute. Existing disposable-database helpers are private to their fixtures.
    private static async Task WithDatabaseAsync(Func<string, Task> run)
    {
        var configured = Environment.GetEnvironmentVariable("ANDRIL_M3_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Local PostgreSQL test connection is missing.");

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(configured);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Local PostgreSQL test connection is invalid.");
        }

        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Use a local PostgreSQL server and its postgres maintenance database.");
        if (settings.Host == "localhost")
            settings.Host = "127.0.0.1";
        settings.Pooling = false;
        settings.Enlist = false;
        settings.IncludeErrorDetail = false;
        settings.SearchPath = "public";
        settings.Timeout = 10;
        settings.CommandTimeout = 15;

        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var verify = new NpgsqlCommand(
            "SELECT inet_server_addr() IN ('127.0.0.1'::inet, '::1'::inet)", admin))
        {
            if (await verify.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("The PostgreSQL server must report a loopback address.");
        }

        var databaseName = $"andril_m3_ledger_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\" TEMPLATE template0", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            settings.Database = databaseName;
            var connection = settings.ConnectionString;
            await using (var context = CreateContext(connection))
            {
                await context.Database.EnsureCreatedAsync();
            }
            await run(connection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
