using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AccountPersistenceTests
{
    [Fact]
    public void MigrationMatchesModel_AndScriptOnlyTargetsAccountSchema()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE andril_identity.accounts", script);
        Assert.Contains("UNIQUE (supabase_user_id)", script);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", script);
        Assert.DoesNotContain("auth.users", script);
        Assert.DoesNotContain("DROP TABLE", script);
        var down = db.GetService<IMigrator>().GenerateScript("20261114000100_InitialAccounts", "0");
        Assert.Contains("DROP TABLE andril_identity.accounts", down);
        Assert.DoesNotContain("DROP SCHEMA", down);
    }

    [LocalPostgresFact]
    public async Task RepeatedAndConcurrentInitialization_CreatesExactlyOneAccount()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var subject = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = database.Open();
            return await new AccountRepository(db).GetOrCreateAsync(subject, DateTimeOffset.UtcNow);
        }));
        Assert.Single(results.Select(x => x.Id).Distinct());
        Assert.Single(results.Select(x => x.CreatedAtUtc).Distinct());
        await using var check = database.Open();
        Assert.Equal(1, await check.Accounts.CountAsync());
        var repeat = await new AccountRepository(check).GetOrCreateAsync(subject, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(results[0].Id, repeat.Id);
        Assert.Equal(results[0].UpdatedAtUtc, repeat.UpdatedAtUtc);
    }

    [LocalPostgresFact]
    public async Task UniqueAndCheckConstraints_AreEnforcedByPostgres()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var subject = Guid.NewGuid();
        await using (var db = database.Open())
        {
            await new AccountRepository(db).GetOrCreateAsync(subject, DateTimeOffset.UtcNow);
            db.Accounts.Add(Account.Create(subject, DateTimeOffset.UtcNow));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using var check = database.Open();
        var now = DateTimeOffset.UtcNow;
        var emptySubject = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts VALUES ({Guid.NewGuid()}, {Guid.Empty}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptySubject.SqlState);
        var invalidTime = await Assert.ThrowsAsync<PostgresException>(() => check.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.accounts VALUES ({Guid.NewGuid()}, {Guid.NewGuid()}, {now}, {now.AddDays(-1)})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidTime.SqlState);
        Assert.Equal(1, await check.Accounts.CountAsync());
    }

    [LocalPostgresFact]
    public async Task FailureAfterInsert_RollsBackAccountCreation()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using (var db = database.Open(new FailAccountRead()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow));
        }
        await using var check = database.Open();
        Assert.Equal(0, await check.Accounts.CountAsync());
    }

    [LocalPostgresFact]
    public async Task MigrationRollback_PreservesUnrelatedTables()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE public.teammate_sentinel (id integer PRIMARY KEY)");
        await db.GetService<IMigrator>().MigrateAsync("0");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO public.teammate_sentinel VALUES (1)");
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task AccountServicePreservesOwnedIdentityAcrossConcurrentInitializationAndFreshReads()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var subjects = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var subject = subjects[index % subjects.Length];
            await using var db = database.Open();
            var service = new AccountService(new ServiceUser(subject), new AccountRepository(db), TimeProvider.System);
            var initialized = await service.InitializeAsync();
            Assert.Equal(initialized, await service.GetMeAsync());
            Assert.NotEqual(subject, initialized.AccountId);
            return (Subject: subject, Account: initialized);
        }));
        Assert.Equal(2, results.Select(result => result.Account.AccountId).Distinct().Count());
        foreach (var subject in subjects)
        {
            var initialized = Assert.Single(results.Where(result => result.Subject == subject).Select(result => result.Account).Distinct());
            await using var fresh = database.Open();
            var service = new AccountService(new ServiceUser(subject), new AccountRepository(fresh), TimeProvider.System);
            Assert.Equal(initialized, await service.GetMeAsync());
            var json = System.Text.Json.JsonSerializer.Serialize(initialized);
            foreach (var identity in subjects) Assert.DoesNotContain(identity.ToString(), json);
        }
        await using var check = database.Open();
        var missing = new AccountService(new ServiceUser(Guid.NewGuid()), new AccountRepository(check), TimeProvider.System);
        Assert.Null(await missing.GetMeAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing.InitializeAsync(cancellation.Token));
        Assert.Equal(2, await check.Accounts.CountAsync());
    }

    private sealed record ServiceUser(Guid? UserId, bool IsAuthenticated = true) : ICurrentUser;

    private sealed class FailAccountRead : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM andril_identity.accounts", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected failure after insert.");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class LocalPostgresFactAttribute : FactAttribute
{
    public LocalPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES")))
            Skip = "Requires an explicitly configured disposable local PostgreSQL server.";
    }
}

internal sealed class LocalAccountDatabase : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string connection;
    private readonly string name;
    private LocalAccountDatabase(string adminConnection, string connection, string name)
    {
        this.adminConnection = adminConnection; this.connection = connection; this.name = name;
    }

    internal static async Task<LocalAccountDatabase> CreateAsync()
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES"));
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Tests require a local PostgreSQL maintenance database.");
        settings.Pooling = false;
        settings.IncludeErrorDetail = false;
        var admin = settings.ConnectionString;
        var name = "andril_m2_test_" + Guid.NewGuid().ToString("N");
        await using (var db = new NpgsqlConnection(admin))
        {
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", db);
            await command.ExecuteNonQueryAsync();
        }
        settings.Database = name;
        var database = new LocalAccountDatabase(admin, settings.ConnectionString, name);
        try
        {
            await using var db = database.Open();
            await db.Database.MigrateAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    internal AccountsDbContext Open(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(connection,
            o => o.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity"))
            .AddInterceptors(interceptors).Options);

    public async ValueTask DisposeAsync()
    {
        await using var db = new NpgsqlConnection(adminConnection);
        await db.OpenAsync();
        // Only the random database created by this instance is removed, never a supplied name.
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", db);
        await command.ExecuteNonQueryAsync();
    }
}
