using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class AccountPolicyPersistenceTests
{
    [Fact]
    public void ModelMatchesSnapshot_AndMigrationIsAdditiveAndPrivate()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript("20261115000200_AddAccountDemographics", "20261115000300_AddAccountPolicyRecords");
        Assert.Contains("CREATE TABLE andril_identity.account_policy_records", script);
        Assert.Contains("PRIMARY KEY (account_id, action, version)", script);
        Assert.Contains("REFERENCES andril_identity.accounts (id)", script);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", script);
        Assert.Contains("FROM PUBLIC", script);
        Assert.Contains("FROM anon", script);
        Assert.Contains("FROM authenticated", script);
        Assert.DoesNotContain("UPDATE ", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("auth.users", script);
        var down = migrator.GenerateScript("20261115000300_AddAccountPolicyRecords", "20261115000200_AddAccountDemographics");
        Assert.Contains("DROP TABLE andril_identity.account_policy_records", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", down);
        Assert.DoesNotContain("DROP SCHEMA", down);
    }

    [LocalPostgresFact]
    public async Task ConcurrentDuplicateSubmissionsPreserveHistoryAndOriginalTimestamps()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        Guid accountId;
        await using (var db = database.Open())
            accountId = (await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), DateTimeOffset.UtcNow)).Id;
        var now = DateTimeOffset.UtcNow;
        PolicyActionSubmission[] actions = [new(PolicyActions.TermsAgreed, "test-terms"), new(PolicyActions.PrivacyAcknowledged, "test-privacy")];
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = database.Open();
            await new AccountPolicyRepository(db).AppendAsync(accountId, i % 2 == 0 ? actions : actions.Reverse().ToArray(), now);
        }));
        await using var check = database.Open();
        var repo = new AccountPolicyRepository(check);
        var original = await repo.ReadAsync(accountId);
        Assert.Equal(2, original.Count);
        await repo.AppendAsync(accountId, actions, now.AddDays(1));
        Assert.Equal(original.Select(x => x.RecordedAtUtc), (await repo.ReadAsync(accountId)).Select(x => x.RecordedAtUtc));
        await repo.AppendAsync(accountId, [new(PolicyActions.TermsAgreed, "test-next"), actions[1]], now.AddDays(2));
        Assert.Equal(3, (await repo.ReadAsync(accountId)).Count);
        Assert.Empty(await repo.ReadAsync(Guid.NewGuid()));
    }

    [LocalPostgresFact]
    public async Task ConstraintsRejectInvalidRows_AndRlsHasNoPublicPolicy()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var now = DateTimeOffset.UtcNow;
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), now);
        async Task Reject(Guid id, string action, string version, string code)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_policy_records VALUES ({id}, {action}, {version}, {now})
                """));
            Assert.Equal(code, error.SqlState);
        }
        await Reject(Guid.NewGuid(), PolicyActions.TermsAgreed, "test", PostgresErrorCodes.ForeignKeyViolation);
        await Reject(account.Id, "community_agreed", "test", PostgresErrorCodes.CheckViolation);
        await Reject(account.Id, PolicyActions.TermsAgreed, " ", PostgresErrorCodes.CheckViolation);
        await new AccountPolicyRepository(db).AppendAsync(account.Id, [new(PolicyActions.TermsAgreed, "test")], now);
        await Reject(account.Id, PolicyActions.TermsAgreed, "test", PostgresErrorCodes.UniqueViolation);
        var enabled = await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE oid = 'andril_identity.account_policy_records'::regclass").SingleAsync();
        Assert.True(enabled);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_policies WHERE schemaname = 'andril_identity' AND tablename = 'account_policy_records'").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task FailureOnSecondInsertRollsBackFirstInsert()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        await using var db = database.Open();
        var now = DateTimeOffset.UtcNow;
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), now);
        // Use a temporary database-only constraint to inject a failure on the second action.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE andril_identity.account_policy_records ADD CONSTRAINT test_reject_terms CHECK (action <> 'terms_agreed')");
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => new AccountPolicyRepository(db).AppendAsync(account.Id,
            [new(PolicyActions.TermsAgreed, "test"), new(PolicyActions.PrivacyAcknowledged, "test")], now));
        Assert.Empty(await new AccountPolicyRepository(db).ReadAsync(account.Id));
    }
}
