using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Hobbies.Entities;
using Andril.Hobbies.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
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
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

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
            var postgres = Assert.IsType<PostgresException>(failure is DbUpdateException update ? update.InnerException : failure);
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

    private static HobbiesGalleryDbContext CreateContext(string connection = OfflineConnection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<HobbiesGalleryDbContext>().UseNpgsql(connection);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new HobbiesGalleryDbContext(options.Options);
    }

    private sealed class CoverSaveGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // Both transactions must see the absence of preferences before either attempts its insert.
            if (Interlocked.Increment(ref _arrivals) == 2)
                _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    // The M3 attribute is shared. Existing disposable-database helpers are private to their fixtures.
    private static async Task WithDatabaseAsync(Func<string, Task> test)
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
            await using (var context = CreateContext(connection))
            {
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
