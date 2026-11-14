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

public sealed class LanguagePreferencePersistenceTests
{
    private const string Initial = "20261114000100_InitialAccounts";
    private const string Language = "20261115000100_AddAccountLanguagePreferences";

    [Fact]
    public void ModelMatchesSnapshot_AndMigrationDoesNotAlterAccounts()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var up = migrator.GenerateScript(Initial, Language);
        Assert.Contains("CREATE TABLE andril_identity.account_language_preferences", up);
        Assert.Contains("PRIMARY KEY (account_id)", up);
        Assert.Contains("REFERENCES andril_identity.accounts (id) ON DELETE CASCADE", up);
        Assert.Contains("language IN ('en', 'ta', 'si')", up);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", up);
        Assert.DoesNotContain("ALTER TABLE andril_identity.accounts", up);
        Assert.DoesNotContain("CREATE TABLE andril_identity.accounts", up);
        Assert.DoesNotContain("DROP TABLE", up);
        var down = migrator.GenerateScript(Language, Initial);
        Assert.Contains("DROP TABLE andril_identity.account_language_preferences", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", down);
    }

    [LocalPostgresFact]
    public async Task ChoicesPersistAcrossContexts_RepeatedSettingsAreNoOps_AccountIsUnchanged()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var repository = new LanguagePreferenceRepository(db);
        Assert.Null(await repository.FindAsync(account.Id));
        var first = await repository.WriteAsync(account.Id, "ta", true, DateTimeOffset.UtcNow);
        foreach (var code in new[] { "en", "ta", "si" })
        {
            var written = await repository.WriteAsync(account.Id, code, false, DateTimeOffset.UtcNow);
            var repeated = await repository.WriteAsync(account.Id, code, false, DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(written.UpdatedAtUtc, repeated.UpdatedAtUtc);
            Assert.Equal(first.CreatedAtUtc, repeated.CreatedAtUtc);
            await using var fresh = database.Open();
            Assert.Equal(code, (await new LanguagePreferenceRepository(fresh).FindAsync(account.Id))!.Language);
        }
        Assert.Equal(1, await db.LanguagePreferences.CountAsync());
        Assert.Equal(account.UpdatedAtUtc, (await db.Accounts.AsNoTracking().SingleAsync()).UpdatedAtUtc);
        await db.GetService<IMigrator>().MigrateAsync(Initial);
        Assert.Equal(account.Id, (await db.Accounts.AsNoTracking().SingleAsync()).Id);
    }

    [LocalPostgresFact]
    public async Task ConcurrentFirstSyncHasOneWinner_AndLaterSyncCannotOverwriteSettings()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var values = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var other = database.Open();
            return await new LanguagePreferenceRepository(other).WriteAsync(account.Id, new[] { "en", "ta", "si" }[i % 3],
                true, DateTimeOffset.UtcNow);
        }));
        Assert.Single(values.Select(v => v.Language).Distinct());
        Assert.Equal(1, await db.LanguagePreferences.CountAsync());
        var repository = new LanguagePreferenceRepository(db);
        await repository.WriteAsync(account.Id, "si", false, DateTimeOffset.UtcNow);
        Assert.Equal("si", (await repository.WriteAsync(account.Id, "ta", true, DateTimeOffset.UtcNow)).Language);
    }

    [LocalPostgresFact]
    public async Task DatabaseEnforcesLanguageUniquenessAndAccountForeignKey()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_language_preferences VALUES ({account.Id}, {"fr"}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        var missingAccount = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_language_preferences VALUES ({Guid.NewGuid()}, {"en"}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingAccount.SqlState);
        await new LanguagePreferenceRepository(db).WriteAsync(account.Id, "en", true, now);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.account_language_preferences VALUES ({account.Id}, {"ta"}, {now}, {now})
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [LocalPostgresFact]
    public async Task FailedWriteRollsBack_AndAccountsAreIsolated()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var accounts = new AccountRepository(db);
        var a = await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var b = await accounts.GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var preferences = new LanguagePreferenceRepository(db);
        await preferences.WriteAsync(a.Id, "ta", false, DateTimeOffset.UtcNow);
        await preferences.WriteAsync(b.Id, "si", false, DateTimeOffset.UtcNow);
        await using (var failing = database.Open(new FailRead()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LanguagePreferenceRepository(failing).WriteAsync(a.Id, "en", false, DateTimeOffset.UtcNow));
        }
        Assert.Equal("ta", (await preferences.FindAsync(a.Id))!.Language);
        Assert.Equal("si", (await preferences.FindAsync(b.Id))!.Language);
    }

    private sealed class FailRead : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM andril_identity.account_language_preferences", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected preference read failure.");
            return ValueTask.FromResult(result);
        }
    }
}
