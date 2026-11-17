using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class PrivateProfilePersistenceTests
{
    [LocalPostgresFact]
    public async Task ConcurrentNameClaimsHaveOneWinnerAndRetriesPreserveIdentity()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var accounts = new[] { Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow), Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow) };
        await using (var seed = database.Open())
        {
            seed.Accounts.AddRange(accounts);
            foreach (var account in accounts)
            {
                seed.StarterMemberships.Add(StarterMembership.Create(account.Id, "learning", false, DateTimeOffset.UtcNow));
                seed.PrivateProfiles.Add(PrivateModuleProfile.Create(account.Id, "learning", DateTimeOffset.UtcNow));
            }
            await seed.SaveChangesAsync();
        }
        var claims = await Task.WhenAll(accounts.Select(async account =>
        {
            await using var db = database.Open();
            try
            {
                var repository = new PrivateProfileRepository(db);
                var winner = await repository.UpdateAsync(account.Id, "learning", new("  SharedName  ", "draft", "private"), _ => { });
                var retry = await repository.UpdateAsync(account.Id, "learning", new("SharedName", "draft", "private"), _ => { });
                Assert.Equal(winner.Id, retry.Id);
                Assert.Equal(winner.CreatedAtUtc, retry.CreatedAtUtc);
                return true;
            }
            catch (ProfileNameConflictException) { return false; }
        }));
        Assert.Single(claims, x => x);
        await using var check = database.Open();
        Assert.Equal(1, await check.PrivateProfiles.CountAsync(x => x.CanonicalDisplayName == "sharedname"));
    }

    [Fact]
    public void NamesMigrationIsAdditiveAndCanonicalIsGenerated()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("20261116000100_AddPrivateModuleProfiles", "20261116000200_AddPrivateProfileFields");
        Assert.Contains("GENERATED ALWAYS AS", sql);
        Assert.Contains("uq_profiles_owner_name", sql);
        Assert.Contains("uq_profiles_module_name", sql);
        Assert.Contains("COLLATE", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }

    [LocalPostgresFact]
    public async Task DisplayNameConstraintsAllowOnlyApprovedReuse()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var a = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var b = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.Accounts.AddRange(a, b);
        db.PrivateProfiles.AddRange(PrivateModuleProfile.Create(a.Id, "learning", DateTimeOffset.UtcNow),
            PrivateModuleProfile.Create(a.Id, "hobbies", DateTimeOffset.UtcNow),
            PrivateModuleProfile.Create(b.Id, "learning", DateTimeOffset.UtcNow),
            PrivateModuleProfile.Create(b.Id, "hobbies", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        Task Set(Guid owner, string module, string name) => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.private_module_profiles SET display_name={name} WHERE account_id={owner} AND module={module}");
        await Set(a.Id, "learning", "Alice");
        await Assert.ThrowsAsync<PostgresException>(() => Set(a.Id, "hobbies", "ALICE"));
        await Assert.ThrowsAsync<PostgresException>(() => Set(b.Id, "learning", "alice"));
        await Set(b.Id, "hobbies", "ALICE");
        await Assert.ThrowsAsync<PostgresException>(() => Set(a.Id, "learning", " Alice "));
        Assert.Equal(2, await db.PrivateProfiles.CountAsync(x => x.CanonicalDisplayName == "alice"));
    }

    [Fact]
    public void MigrationMatchesModelAndRestrictsDirectClients()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var sql = migrator.GenerateScript("20261115000400_AddStarterMemberships", "20261116000100_AddPrivateModuleProfiles");
        Assert.Contains("CREATE TABLE andril_identity.private_module_profiles", sql);
        Assert.Contains("CREATE UNIQUE INDEX uq_profiles_account_module", sql);
        Assert.Contains("REFERENCES andril_identity.accounts (id) ON DELETE CASCADE", sql);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", sql);
        Assert.Contains("FROM PUBLIC", sql);
        Assert.Contains("FROM anon", sql);
        Assert.Contains("FROM authenticated", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("GRANT", sql);
        var rollback = migrator.GenerateScript("20261116000100_AddPrivateModuleProfiles", "20261115000400_AddStarterMemberships");
        Assert.Contains("DROP TABLE andril_identity.private_module_profiles", rollback);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", rollback);
    }

    [LocalPostgresFact]
    public async Task ConcurrentRetriesPersistExactlyOneProfileAndKeepModuleOwnership()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using (var db = database.Open())
        {
            db.Accounts.Add(account);
            db.StarterMemberships.Add(StarterMembership.Create(account.Id, "learning", false, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = database.Open();
            return (await new PrivateProfileRepository(db).GetOrCreateAsync(account.Id, "learning", _ => { }, TimeProvider.System)).Id;
        }));
        Assert.Single(ids.Distinct());
        await using var check = database.Open();
        Assert.Single(await check.PrivateProfiles.ToListAsync());
        Assert.Equal(ids[0], (await new PrivateProfileRepository(check).ReadAsync(account.Id, "learning", _ => { }))!.Id);
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => new PrivateProfileRepository(check)
            .ReadAsync(account.Id, "social", _ => { }));
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => new PrivateProfileRepository(check)
            .ReadAsync(Guid.NewGuid(), "learning", _ => { }));
    }

    [LocalPostgresFact]
    public async Task DatabaseEnforcesUniquenessOwnershipModuleAndCascade()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = Account.Create(Guid.NewGuid(), DateTimeOffset.UtcNow);
        await using var db = database.Open();
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        async Task Insert(Guid owner, string module) => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO andril_identity.private_module_profiles (id, account_id, module, created_at_utc)
            VALUES ({Guid.NewGuid()}, {owner}, {module}, {now})
            """);
        foreach (var module in new[] { "learning", "hobbies", "social" }) await Insert(account.Id, module);
        Assert.Equal(3, (await db.PrivateProfiles.Select(x => x.Id).ToListAsync()).Distinct().Count());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => Insert(account.Id, "learning"))).SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => Insert(Guid.NewGuid(), "learning"))).SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => Insert(account.Id, "dating"))).SqlState);
        await db.Accounts.Where(x => x.Id == account.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.PrivateProfiles.ToListAsync());
    }
}
