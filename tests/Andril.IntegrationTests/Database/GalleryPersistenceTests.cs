using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class GalleryPersistenceTests
{
    private const string OfflineConnection = "Host=127.0.0.1;Database=andril_model_only;Username=unused";
    private static readonly Guid Owner = new("10000000-0000-4000-8000-000000000001");
    private static readonly Guid OtherOwner = new("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Older = new("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Newer = new("20000000-0000-4000-8000-000000000002");
    private static readonly Guid ForeignOwner = new("20000000-0000-4000-8000-000000000003");
    private static readonly Guid ForeignModule = new("20000000-0000-4000-8000-000000000004");
    private static readonly Guid Deleted = new("20000000-0000-4000-8000-000000000005");
    private static readonly Guid Missing = new("20000000-0000-4000-8000-000000000006");
    private static readonly Guid AlternateCover = new("20000000-0000-4000-8000-000000000007");
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Migration_IsDiscoveredOnlyForGalleryAndMatchesTheCurrentModelOffline()
    {
        using var gallery = CreateContext(interceptor: new RejectConnectionOpen());
        var migrationId = Assert.Single(gallery.Database.GetMigrations());
        Assert.EndsWith("_InitialHobbiesGallery", migrationId);
        var assembly = gallery.GetService<IMigrationsAssembly>();
        Assert.NotNull(assembly.ModelSnapshot);
        Assert.False(gallery.Database.HasPendingModelChanges());

        using var accounts = new AccountsDbContext(
            new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(OfflineConnection)
                .AddInterceptors(new RejectConnectionOpen()).Options);
        Assert.NotEmpty(accounts.Database.GetMigrations());
        Assert.DoesNotContain(migrationId, accounts.Database.GetMigrations());
        using var map = new HobbiesMapDbContext(
            new DbContextOptionsBuilder<HobbiesMapDbContext>().UseNpgsql(OfflineConnection)
                .AddInterceptors(new RejectConnectionOpen()).Options);
        Assert.DoesNotContain(migrationId, map.Database.GetMigrations());
    }

    [Fact]
    public void Migration_GeneratesOnlyGallerySchemaChangesOffline()
    {
        using var context = CreateContext(interceptor: new RejectConnectionOpen());
        var assembly = context.GetService<IMigrationsAssembly>();
        var mapping = Assert.Single(assembly.Migrations);
        var migration = assembly.CreateMigration(mapping.Value, context.Database.ProviderName!);
        Assert.Collection(migration.UpOperations,
            operation => Assert.Equal("GalleryMedia", Assert.IsType<CreateTableOperation>(operation).Name),
            operation => Assert.Equal("GalleryPreferences", Assert.IsType<CreateTableOperation>(operation).Name),
            operation => Assert.Equal("GalleryMedia", Assert.IsType<CreateIndexOperation>(operation).Table));
        Assert.Collection(migration.DownOperations,
            operation => Assert.Equal("GalleryMedia", Assert.IsType<DropTableOperation>(operation).Name),
            operation => Assert.Equal("GalleryPreferences", Assert.IsType<DropTableOperation>(operation).Name));

        var migrator = context.GetService<IMigrator>();
        var up = migrator.GenerateScript(Migration.InitialDatabase, mapping.Key);
        var down = migrator.GenerateScript(mapping.Key, Migration.InitialDatabase);
        var modelStatements = context.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, modelStatements.Length);
        Assert.All(modelStatements, statement => Assert.Contains(statement + ";", up));
        Assert.DoesNotContain("DROP ", up);
        Assert.Contains("DROP TABLE \"GalleryMedia\";", down);
        Assert.Contains("DROP TABLE \"GalleryPreferences\";", down);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task Migration_AppliesAndRollsBackOnlyGalleryTables() =>
        WithDatabaseAsync(async connection =>
        {
            await using var context = CreateContext(connection);
            var migrationId = Assert.Single(context.Database.GetMigrations());
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE migration_isolation_probe (id integer PRIMARY KEY)");
            await context.Database.ExecuteSqlRawAsync("INSERT INTO migration_isolation_probe VALUES (1)");
            await context.Database.MigrateAsync(migrationId);
            Assert.Equal(migrationId, Assert.Single(await context.Database.GetAppliedMigrationsAsync()));
            context.GalleryMedia.Add(Media(Older, Owner, CreatedAt));
            context.GalleryPreferences.Add(new GalleryPreferences { OwnerModuleProfileId = Owner, CoverMediaId = Older });
            await context.SaveChangesAsync();
            Assert.Equal(1, await context.GalleryMedia.CountAsync());
            Assert.Equal(1, await context.GalleryPreferences.CountAsync());

            await context.Database.MigrateAsync(Migration.InitialDatabase);
            Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
            await using var probe = new NpgsqlConnection(connection);
            await probe.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT to_regclass('"GalleryMedia"') IS NULL
                   AND to_regclass('"GalleryPreferences"') IS NULL
                   AND EXISTS (SELECT 1 FROM migration_isolation_probe WHERE id = 1)
                """, probe);
            Assert.Equal(true, await command.ExecuteScalarAsync());
        }, initializeSchema: false);

    [Fact]
    public void Model_MapsGalleryColumnsPrimaryKeysAndOwnerIndexOffline()
    {
        using var context = CreateContext();
        var media = context.GalleryMedia.EntityType;
        Assert.Equal("GalleryMedia", media.GetTableName());
        Assert.Equal(nameof(GalleryMedia.Id), Assert.Single(media.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(11, media.GetProperties().Count());
        Assert.Null(media.FindProperty("PublicUrl"));
        foreach (var (property, type, nullable) in new[]
        {
            (nameof(GalleryMedia.Id), "uuid", false),
            (nameof(GalleryMedia.OwnerModuleProfileId), "uuid", false),
            (nameof(GalleryMedia.Module), "text", false),
            (nameof(GalleryMedia.Title), "text", false),
            (nameof(GalleryMedia.Description), "text", true),
            (nameof(GalleryMedia.Category), "text", false),
            (nameof(GalleryMedia.MediaType), "text", false),
            (nameof(GalleryMedia.StorageObjectKey), "text", false),
            (nameof(GalleryMedia.IsPrivate), "boolean", false),
            (nameof(GalleryMedia.IsDeleted), "boolean", false),
            (nameof(GalleryMedia.CreatedAtUtc), "timestamp with time zone", false)
        })
        {
            var mapped = media.FindProperty(property)!;
            Assert.Equal(property, mapped.GetColumnName());
            Assert.Equal(type, mapped.GetColumnType());
            Assert.Equal(nullable, mapped.IsNullable);
        }

        var index = Assert.Single(media.GetIndexes());
        Assert.Equal(new[] { nameof(GalleryMedia.OwnerModuleProfileId), nameof(GalleryMedia.Module),
            nameof(GalleryMedia.IsDeleted), nameof(GalleryMedia.CreatedAtUtc) }, index.Properties.Select(p => p.Name));
        var preferences = context.GalleryPreferences.EntityType;
        Assert.Equal("GalleryPreferences", preferences.GetTableName());
        var owner = Assert.Single(preferences.FindPrimaryKey()!.Properties);
        Assert.Equal(nameof(GalleryPreferences.OwnerModuleProfileId), owner.GetColumnName());
        Assert.Equal("uuid", owner.GetColumnType());
        Assert.Equal(ValueGenerated.Never, owner.ValueGenerated);
        Assert.False(owner.IsNullable);
        var cover = preferences.FindProperty(nameof(GalleryPreferences.CoverMediaId))!;
        Assert.Equal(nameof(GalleryPreferences.CoverMediaId), cover.GetColumnName());
        Assert.Equal("uuid", cover.GetColumnType());
        Assert.True(cover.IsNullable);
        Assert.Empty(preferences.GetForeignKeys());
        var ddl = context.Database.GenerateCreateScript();
        Assert.Contains("PRIMARY KEY (\"OwnerModuleProfileId\")", ddl);
        Assert.Contains("\"StorageObjectKey\" text NOT NULL", ddl);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task Repository_InvalidIdentifiersAndBlankTitlesShortCircuitOffline()
    {
        await using var context = CreateContext();
        var repository = Repository(context);
        Assert.Empty(await repository.GetOwnerGalleryAsync(Guid.Empty));
        Assert.Null(await repository.GetOwnedByIdAsync(Guid.Empty, Owner));
        Assert.Null(await repository.GetOwnedByIdAsync(Older, Guid.Empty));
        Assert.Null(await repository.GetCoverIdAsync(Guid.Empty));
        Assert.False(await repository.UpdateTitleAsync(Older, Owner, " \t"));
        Assert.False(await repository.UpdateTitleAsync(Older, Guid.Empty, "Updated"));
        Assert.False(await repository.UpdateTitleAsync(Guid.Empty, Owner, "Updated"));
        Assert.False(await repository.SetCoverAsync(Guid.Empty, Owner));
        Assert.False(await repository.SetCoverAsync(Older, Guid.Empty));
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task SetCover_ConcurrentReplacementPropagatesSerializationFailureWithoutRetrying() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using (var seed = CreateContext(connection))
            {
                seed.GalleryMedia.Add(Media(AlternateCover, Owner, CreatedAt));
                await seed.SaveChangesAsync();
                Assert.True(await Repository(seed).SetCoverAsync(Older, Owner));
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var gate = new CoverSaveGate();
            await using var first = CreateContext(connection, gate);
            await using var second = CreateContext(connection, gate);
            var errors = await Task.WhenAll(
                Record.ExceptionAsync(async () => Assert.True(await Repository(first).SetCoverAsync(Newer, Owner, deadline.Token))),
                Record.ExceptionAsync(async () => Assert.True(await Repository(second).SetCoverAsync(AlternateCover, Owner, deadline.Token))));

            Assert.Contains(errors, error => error is null);
            var failure = Assert.Single(errors, error => error is not null);
            var postgres = RequirePostgresException(failure);
            Assert.Equal(PostgresErrorCodes.SerializationFailure, postgres.SqlState);
            Assert.Equal(2, gate.Arrivals);
            Assert.Empty(first.ChangeTracker.Entries());
            Assert.Empty(second.ChangeTracker.Entries());
            Assert.Null(first.Database.CurrentTransaction);
            Assert.Null(second.Database.CurrentTransaction);

            await using var reader = CreateContext(connection);
            var winner = errors[0] is null ? Newer : AlternateCover;
            var preference = Assert.Single(await reader.GalleryPreferences.AsNoTracking().ToListAsync());
            Assert.Equal(Owner, preference.OwnerModuleProfileId);
            Assert.Equal(winner, preference.CoverMediaId);
            Assert.Equal(winner, await Repository(reader).GetCoverIdAsync(Owner));
        });

    [Fact]
    public async Task SetCover_RejectsRetryingExecutionStrategyBeforeOpeningConnection()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen(), retrying: true);
        var unrelated = context.GalleryMedia.Attach(Media(ForeignOwner, OtherOwner, CreatedAt));
        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(context).SetCoverAsync(Older, Owner));
        Assert.Contains("non-retrying execution strategy", error.Message);
        Assert.Equal(EntityState.Unchanged, unrelated.State);
        Assert.Same(unrelated.Entity, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SetCover_RejectsAnOuterRetryingExecutionStrategyBeforeOpeningConnection()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        await using var retrying = CreateContext(interceptor: new RejectConnectionOpen(), retrying: true);
        var strategy = retrying.Database.CreateExecutionStrategy();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strategy.ExecuteAsync(() => Repository(context).SetCoverAsync(Older, Owner)));

        Assert.Contains("non-retrying execution strategy", error.Message);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SetCover_RejectsAmbientTransactionWithoutEndingItOrOpeningConnection()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        using var scope = new TransactionScope(TransactionScopeOption.RequiresNew, TransactionScopeAsyncFlowOption.Enabled);
        var ambient = Transaction.Current;
        Assert.NotNull(ambient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(context).SetCoverAsync(Older, Owner));
        Assert.Contains("own transaction", error.Message);
        Assert.Same(ambient, Transaction.Current);
        Assert.Equal(TransactionStatus.Active, ambient.TransactionInformation.Status);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        scope.Complete();
    }

    [MapCapturePostgresFact]
    public Task SetCover_RejectsUnrelatedWritesAndCanProceedOnceContextIsClean() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using var writer = CreateContext(connection);
            var repository = Repository(writer);
            var other = await writer.GalleryMedia.SingleAsync(media => media.Id == ForeignOwner);
            writer.ChangeTracker.AutoDetectChangesEnabled = false;
            other.Title = "Caller-owned change";

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SetCoverAsync(Older, Owner));
            Assert.Contains("pending changes", failure.Message);
            Assert.Equal(EntityState.Modified, writer.Entry(other).State);
            Assert.Equal("Caller-owned change", other.Title);
            await using var reader = CreateContext(connection);
            Assert.Empty(await reader.GalleryPreferences.ToListAsync());
            Assert.Equal("Original title", (await reader.GalleryMedia.AsNoTracking().SingleAsync(media => media.Id == ForeignOwner)).Title);

            writer.Entry(other).Property(media => media.Title).CurrentValue = "Original title";
            writer.Entry(other).State = EntityState.Unchanged;
            Assert.True(await repository.SetCoverAsync(Older, Owner));
            Assert.True(await repository.SetCoverAsync(Newer, Owner));
            Assert.Same(other, Assert.Single(writer.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Unchanged, writer.Entry(other).State);
            Assert.Equal(Newer, await Repository(reader).GetCoverIdAsync(Owner));
        });

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SetCover_RejectsPendingChangesEvenWithAutoDetectDisabled(EntityState pendingState)
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        var unrelated = context.GalleryMedia.Attach(Media(ForeignOwner, OtherOwner, CreatedAt));
        var untouched = context.GalleryPreferences.Attach(new GalleryPreferences { OwnerModuleProfileId = OtherOwner });
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        if (pendingState == EntityState.Modified)
            unrelated.Entity.Title = "Pending title";
        else
            unrelated.State = pendingState;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(context).SetCoverAsync(Older, Owner));
        Assert.Contains("pending changes", error.Message);
        Assert.Equal(pendingState, unrelated.State);
        Assert.Equal(pendingState == EntityState.Modified ? "Pending title" : "Original title", unrelated.Entity.Title);
        Assert.Equal(EntityState.Unchanged, untouched.State);
        Assert.Equal(2, context.ChangeTracker.Entries().Count());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SetCover_RejectsAnAlreadyTrackedOwnerPreferenceWithoutChangingIt()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        var existing = context.GalleryPreferences.Attach(new GalleryPreferences
        {
            OwnerModuleProfileId = Owner,
            CoverMediaId = Newer
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(context).SetCoverAsync(Older, Owner));
        Assert.Contains("already tracked", error.Message);
        Assert.Equal(EntityState.Unchanged, existing.State);
        Assert.Equal(Newer, existing.Entity.CoverMediaId);
        Assert.Same(existing.Entity, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task SetCover_PropagatesPreCancellationWithoutTouchingPendingState()
    {
        await using var context = CreateContext(interceptor: new RejectConnectionOpen());
        var unrelated = context.GalleryMedia.Add(Media(ForeignOwner, OtherOwner, CreatedAt));
        var token = new CancellationToken(canceled: true);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repository(context).SetCoverAsync(Older, Owner, token));
        Assert.Equal(token, error.CancellationToken);
        Assert.Equal(EntityState.Added, unrelated.State);
        Assert.Same(unrelated.Entity, Assert.Single(context.ChangeTracker.Entries()).Entity);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [MapCapturePostgresFact]
    public Task SetCover_FailedInsertLeavesNoPendingPreferenceForLaterWrites() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using var writer = CreateContext(connection);
            await writer.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "GalleryPreferences" ADD CONSTRAINT ck_test_reject_cover CHECK ("CoverMediaId" IS NULL)""");
            var unrelated = await writer.GalleryMedia.SingleAsync(media => media.Id == ForeignOwner);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => Repository(writer).SetCoverAsync(Older, Owner));
            var postgres = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("ck_test_reject_cover", postgres.ConstraintName);
            Assert.Same(unrelated, Assert.Single(writer.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Unchanged, writer.Entry(unrelated).State);
            Assert.Null(writer.Database.CurrentTransaction);

            unrelated.Title = "Later title";
            Assert.Equal(1, await writer.SaveChangesAsync());
            await using var reader = CreateContext(connection);
            Assert.Empty(await reader.GalleryPreferences.ToListAsync());
            Assert.Equal("Later title", (await reader.GalleryMedia.SingleAsync(media => media.Id == ForeignOwner)).Title);
        });

    [MapCapturePostgresFact]
    public Task SetCover_CancellationBeforeCommitRollsBackUpdateAndDetachesItsPreference() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using (var seed = CreateContext(connection))
            {
                Assert.True(await Repository(seed).SetCoverAsync(Older, Owner));
            }

            using var cancellation = new CancellationTokenSource();
            var interceptor = new CancelCoverCommit(cancellation);
            await using var writer = CreateContext(connection, interceptor);
            var unrelated = await writer.GalleryMedia.SingleAsync(media => media.Id == ForeignOwner);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Repository(writer).SetCoverAsync(Newer, Owner, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(interceptor.ReachedCommit);
            Assert.Same(unrelated, Assert.Single(writer.ChangeTracker.Entries()).Entity);
            Assert.Equal(EntityState.Unchanged, writer.Entry(unrelated).State);
            Assert.Null(writer.Database.CurrentTransaction);

            unrelated.Title = "Later title";
            Assert.Equal(1, await writer.SaveChangesAsync());
            await using var reader = CreateContext(connection);
            Assert.Equal(Older, await Repository(reader).GetCoverIdAsync(Owner));
        });

    [MapCapturePostgresFact]
    public Task SetCover_RejectsCallerOwnedTransactionWithoutEndingIt() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using var writer = CreateContext(connection);
            await using var transaction = await writer.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(writer).SetCoverAsync(Older, Owner));
            Assert.Contains("own transaction", error.Message);
            Assert.Same(transaction, writer.Database.CurrentTransaction);
            Assert.Empty(await writer.GalleryPreferences.ToListAsync());
            await transaction.RollbackAsync();
        });

    [MapCapturePostgresFact]
    public Task Repository_ListsAndRetrievesOnlyTheOwnersActiveHobbiesMedia() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var gallery = await repository.GetOwnerGalleryAsync(Owner);
            Assert.Equal(new[] { Newer, Older }, gallery.Select(item => item.Id));
            var media = await repository.GetOwnedByIdAsync(Older, Owner);
            Assert.NotNull(media);
            Assert.True(media.IsPrivate);
            Assert.Equal($"synthetic/{Older:N}.jpg", media.StorageObjectKey);
            Assert.Equal(Owner, media.OwnerModuleProfileId);
            foreach (var excluded in new[] { ForeignOwner, ForeignModule, Deleted, Missing })
                Assert.Null(await repository.GetOwnedByIdAsync(excluded, Owner));
            Assert.Null(await repository.GetOwnedByIdAsync(Older, OtherOwner));
            Assert.Empty(context.ChangeTracker.Entries());
        });

    [MapCapturePostgresFact]
    public Task Repository_PersistsTrimmedTitleOnlyForEligibleOwnedMedia() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using (var writer = CreateContext(connection))
            {
                var repository = Repository(writer);
                foreach (var excluded in new[] { ForeignOwner, ForeignModule, Deleted, Missing })
                    Assert.False(await repository.UpdateTitleAsync(excluded, Owner, "Denied title"));
                Assert.True(await repository.UpdateTitleAsync(Older, Owner, "  Updated title  "));
            }

            await using var reader = CreateContext(connection);
            var rows = await reader.GalleryMedia.AsNoTracking().ToDictionaryAsync(media => media.Id);
            Assert.Equal("Updated title", rows[Older].Title);
            Assert.All(rows.Values.Where(media => media.Id != Older), media => Assert.Equal("Original title", media.Title));
        });

    [MapCapturePostgresFact]
    public Task Repository_SetsAndReplacesCoverAndSuppressesStaleDeletedSelection() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using (var writer = CreateContext(connection))
            {
                var repository = Repository(writer);
                Assert.Null(await repository.GetCoverIdAsync(Owner));
                foreach (var excluded in new[] { ForeignOwner, ForeignModule, Deleted, Missing })
                    Assert.False(await repository.SetCoverAsync(excluded, Owner));
                Assert.True(await repository.SetCoverAsync(Older, Owner));
                Assert.Equal(Older, await repository.GetCoverIdAsync(Owner));
                Assert.True(await repository.SetCoverAsync(Newer, Owner));
            }

            await using var reader = CreateContext(connection);
            var cover = Assert.Single(await reader.GalleryPreferences.AsNoTracking().ToListAsync());
            Assert.Equal(Owner, cover.OwnerModuleProfileId);
            Assert.Equal(Newer, cover.CoverMediaId);
            Assert.Equal(Newer, await Repository(reader).GetCoverIdAsync(Owner));
            Assert.Null(await Repository(reader).GetCoverIdAsync(OtherOwner));
            Assert.Equal(1, await reader.GalleryMedia.Where(media => media.Id == Newer)
                .ExecuteUpdateAsync(setters => setters.SetProperty(media => media.IsDeleted, true)));
            Assert.Null(await Repository(reader).GetCoverIdAsync(Owner));
        });

    [MapCapturePostgresFact]
    public Task Repository_ConcurrentFirstCoverSelectionLeavesExactlyOneOwnedPreference() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var gate = new CoverSaveGate();
            await using var first = CreateContext(connection, gate);
            await using var second = CreateContext(connection, gate);
            var errors = await Task.WhenAll(
                Record.ExceptionAsync(async () => Assert.True(await Repository(first).SetCoverAsync(Older, Owner, deadline.Token))),
                Record.ExceptionAsync(async () => Assert.True(await Repository(second).SetCoverAsync(Newer, Owner, deadline.Token))));

            Assert.Contains(errors, error => error is null);
            var failure = Assert.Single(errors, error => error is not null);
            var postgres = RequirePostgresException(failure);
            Assert.Contains(postgres.SqlState, new[] { PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.UniqueViolation });
            if (postgres.SqlState == PostgresErrorCodes.UniqueViolation)
                Assert.Equal("PK_GalleryPreferences", postgres.ConstraintName);
            var winner = errors[0] is null ? Older : Newer;
            await using var reader = CreateContext(connection);
            var preference = Assert.Single(await reader.GalleryPreferences.AsNoTracking().ToListAsync());
            Assert.Equal(Owner, preference.OwnerModuleProfileId);
            Assert.Equal(winner, preference.CoverMediaId);
            Assert.Equal(winner, await Repository(reader).GetCoverIdAsync(Owner));
        });

    [MapCapturePostgresFact]
    public Task Repository_PropagatesCancellationAndDatabaseFailures() =>
        WithDatabaseAsync(async connection =>
        {
            await SeedAsync(connection);
            await using var context = CreateContext(connection);
            var repository = Repository(context);
            var token = new CancellationToken(canceled: true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetOwnerGalleryAsync(Owner, token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetOwnedByIdAsync(Older, Owner, token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetCoverIdAsync(Owner, token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.UpdateTitleAsync(Older, Owner, "Cancelled", token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SetCoverAsync(Older, Owner, token));
            Assert.Empty(await context.GalleryPreferences.AsNoTracking().ToListAsync());
            Assert.Equal("Original title", (await repository.GetOwnedByIdAsync(Older, Owner))!.Title);

            await context.Database.ExecuteSqlRawAsync("ALTER TABLE \"GalleryMedia\" RENAME TO gallery_media_unavailable");
            var operations = new Func<Task>[]
            {
                () => repository.GetOwnerGalleryAsync(Owner),
                () => repository.GetOwnedByIdAsync(Older, Owner),
                () => repository.UpdateTitleAsync(Older, Owner, "Failed"),
                () => repository.SetCoverAsync(Older, Owner)
            };
            foreach (var operation in operations)
            {
                var error = await Assert.ThrowsAsync<PostgresException>(operation);
                Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
            }
        });

    private static IGalleryRepository Repository(HobbiesGalleryDbContext context) =>
        new GalleryRepository<HobbiesGalleryDbContext>(context);

    private static async Task SeedAsync(string connection)
    {
        await using var context = CreateContext(connection);
        context.GalleryMedia.AddRange(
            Media(Older, Owner, CreatedAt), Media(Newer, Owner, CreatedAt.AddMinutes(1)),
            Media(ForeignOwner, OtherOwner, CreatedAt.AddMinutes(2)),
            Media(ForeignModule, Owner, CreatedAt.AddMinutes(3), module: "Social"),
            Media(Deleted, Owner, CreatedAt.AddMinutes(4), deleted: true));
        await context.SaveChangesAsync();
    }

    private static GalleryMedia Media(Guid id, Guid owner, DateTimeOffset createdAt,
        string module = "Hobbies", bool deleted = false) => new()
    {
        Id = id,
        OwnerModuleProfileId = owner,
        Module = module,
        Title = "Original title",
        Category = "Synthetic",
        StorageObjectKey = $"synthetic/{id:N}.jpg",
        IsDeleted = deleted,
        CreatedAtUtc = createdAt
    };

    private static HobbiesGalleryDbContext CreateContext(
        string connection = OfflineConnection, IInterceptor? interceptor = null, bool retrying = false)
    {
        var options = new DbContextOptionsBuilder<HobbiesGalleryDbContext>().UseNpgsql(connection, postgres =>
        {
            if (retrying)
                postgres.EnableRetryOnFailure();
        });
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new HobbiesGalleryDbContext(options.Options);
    }

    private static PostgresException RequirePostgresException(Exception failure)
    {
        var exceptionTypes = new System.Collections.Generic.List<string>();
        for (Exception? error = failure; error is not null; error = error.InnerException)
        {
            if (error is PostgresException postgres)
                return postgres;
            exceptionTypes.Add(error.GetType().Name);
        }

        throw new Xunit.Sdk.XunitException(
            $"Expected a PostgreSQL exception in the failure chain. Observed: {string.Join(" -> ", exceptionTypes)}.");
    }

    private sealed class CoverSaveGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => Volatile.Read(ref _arrivals);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // Both transactions read the same preference state before either writes it.
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class RejectConnectionOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("The offline test attempted to open a database connection.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The offline test attempted to open a database connection.");
    }

    private sealed class CancelCoverCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public bool ReachedCommit { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            // Simulate cancellation after SaveChangesAsync completes, before the provider commits.
            var preference = Assert.Single(eventData.Context!.ChangeTracker.Entries<GalleryPreferences>());
            Assert.Equal(EntityState.Unchanged, preference.State);
            Assert.Equal(Newer, preference.Entity.CoverMediaId);
            ReachedCommit = true;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    // The M3 attribute is shared. Existing disposable-database helpers are private to their fixtures.
    private static async Task WithDatabaseAsync(Func<string, Task> test, bool initializeSchema = true)
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
        await using (var verify = new NpgsqlCommand("SELECT inet_server_addr() IN ('127.0.0.1'::inet, '::1'::inet)", admin))
        {
            if (await verify.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("The PostgreSQL server must report a loopback address.");
        }
        var name = $"andril_m3_gallery_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", admin))
        {
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            settings.Database = name;
            var connection = settings.ConnectionString;
            if (initializeSchema)
            {
                await using var context = CreateContext(connection);
                await context.Database.EnsureCreatedAsync();
            }
            await test(connection);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
