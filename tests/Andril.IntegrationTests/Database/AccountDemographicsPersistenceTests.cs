using System.Data.Common;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AccountDemographicsPersistenceTests
{
    private const string Previous = "20261115000100_AddAccountLanguagePreferences";
    private const string Current = "20261115000200_AddAccountDemographics";

    [Fact]
    public void SnapshotMatches_AndMigrationOnlyAddsPrivateDemographics()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(Previous, Current);
        Assert.Contains("CREATE TABLE andril_identity.account_demographics", up);
        Assert.Contains("date_of_birth date NOT NULL", up);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", up);
        Assert.Contains("AT TIME ZONE 'UTC'", up);
        Assert.DoesNotContain("ALTER TABLE andril_identity.accounts", up);
        Assert.DoesNotContain("DROP TABLE", up);
        var down = migrator.GenerateScript(Current, Previous);
        Assert.Contains("DROP TABLE andril_identity.account_demographics", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.account_language_preferences", down);
    }

    [LocalPostgresFact]
    public async Task PrivateDataPersists_IdenticalSubmissionsDoNotChangeTimestamps_AccountsAreIsolated()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var accounts = new AccountRepository(db);
        var a = await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var b = await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var repository = new AccountDemographicsRepository(db);
        var first = await repository.SaveAsync(a.Id, new(2000, 2, 29), "private text", DateTimeOffset.UtcNow);
        var repeated = await repository.SaveAsync(a.Id, new(2000, 2, 29), "private text", DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(first.CreatedAtUtc, repeated.CreatedAtUtc);
        Assert.Equal(first.UpdatedAtUtc, repeated.UpdatedAtUtc);
        Assert.Null(await repository.FindAsync(b.Id));
        await using var fresh = database.Open();
        var persisted = await new AccountDemographicsRepository(fresh).FindAsync(a.Id);
        Assert.Equal(new(2000, 2, 29), persisted!.DateOfBirth);
        Assert.Equal("private text", persisted.Gender);
        Assert.Equal(a.UpdatedAtUtc, (await fresh.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id)).UpdatedAtUtc);
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.Equal(2, await db.Accounts.CountAsync());
    }

    [LocalPostgresFact]
    public async Task DatabaseEnforcesValidDobGenderAndOwnershipLink()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var tomorrow = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var future = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_demographics VALUES ({account.Id}, {tomorrow}, {"value"}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, future.SqlState);
        var emptyGender = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_demographics VALUES ({account.Id}, {new DateOnly(2000, 1, 1)}, {" "}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, emptyGender.SqlState);
        var missingAccount = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_demographics VALUES ({Guid.NewGuid()}, {new DateOnly(2000, 1, 1)}, {"value"}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingAccount.SqlState);
    }

    [LocalPostgresFact]
    public async Task FailureAfterWriteRollsBackPrivateData()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using (var failing = database.Open(new FailRead()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AccountDemographicsRepository(failing)
                .SaveAsync(account.Id, new(2000, 1, 1), "value", DateTimeOffset.UtcNow));
        }
        Assert.Empty(await db.Demographics.ToListAsync());
    }

    private sealed class FailRead : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM andril_identity.account_demographics", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected read failure.");
            return ValueTask.FromResult(result);
        }
    }
}
