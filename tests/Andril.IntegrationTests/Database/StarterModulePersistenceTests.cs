using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.StarterModules;
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

public sealed class StarterModulePersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ModelMatchesSnapshot_AndMigrationIsAdditivePrivateAndStarterOnly()
    {
        using var db = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=localhost;Database=not_connected;Username=not_connected").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var migrator = db.GetService<IMigrator>();
        var script = migrator.GenerateScript("20261115000300_AddAccountPolicyRecords", "20261115000400_AddStarterMemberships");
        Assert.Contains("CREATE TABLE andril_identity.account_starter_memberships", script);
        Assert.Contains("PRIMARY KEY (account_id)", script);
        Assert.Contains("REFERENCES andril_identity.accounts (id)", script);
        Assert.Contains("ck_starter_social_opt_in", script);
        Assert.Contains("ENABLE ROW LEVEL SECURITY", script);
        Assert.Contains("FROM PUBLIC", script);
        Assert.Contains("FROM anon", script);
        Assert.Contains("FROM authenticated", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("profile", script, StringComparison.OrdinalIgnoreCase);
        var down = migrator.GenerateScript("20261115000400_AddStarterMemberships", "20261115000300_AddAccountPolicyRecords");
        Assert.Contains("DROP TABLE andril_identity.account_starter_memberships", down);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", down);
        Assert.DoesNotContain("DROP SCHEMA", down);
    }

    [LocalPostgresFact]
    public async Task ConcurrentIdenticalSocialRequestsCreateOneMembershipAndOneOptIn()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = database.Open();
            return await Service(db, account).SelectAsync("social", true);
        }));
        Assert.Single(results.Select(x => x.Membership).Distinct());
        await using var check = database.Open();
        var saved = await check.StarterMemberships.SingleAsync();
        Assert.Equal("social", saved.Module);
        Assert.Equal(saved.ActivatedAtUtc, saved.SocialOptInAtUtc);
        var later = await Service(check, account, new Clock(Now.AddDays(1))).SelectAsync("social", true);
        Assert.Equal(saved.ActivatedAtUtc, later.Membership!.ActivatedAtUtc);
        Assert.Equal(2, await check.PolicyRecords.CountAsync());
    }

    [LocalPostgresFact]
    public async Task ConcurrentDifferentChoicesHaveOneWinnerAndNoSwitch()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = database.Open();
            var module = i % 3 == 0 ? "learning" : i % 3 == 1 ? "hobbies" : "social";
            try { return (await Service(db, account).SelectAsync(module, module == "social")).Membership!.Module; }
            catch (StarterModuleConflictException) { return null; }
        }));
        var winner = Assert.Single(results.Where(x => x is not null).Distinct());
        Assert.Contains(results, x => x is null);
        await using var check = database.Open();
        Assert.Equal(winner, (await check.StarterMemberships.SingleAsync()).Module);
    }

    [LocalPostgresFact]
    public async Task ConstraintsEnforceOneStarterValidModuleOptInAndOwnership()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        await using var db = database.Open();
        async Task Reject(Guid id, string module, DateTimeOffset? optIn, string expected)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_identity.account_starter_memberships VALUES ({id}, {module}, {Now}, {optIn})
                """));
            Assert.Equal(expected, error.SqlState);
        }
        await Reject(Guid.NewGuid(), "learning", null, PostgresErrorCodes.ForeignKeyViolation);
        await Reject(account.Id, "unknown", null, PostgresErrorCodes.CheckViolation);
        await Reject(account.Id, "social", null, PostgresErrorCodes.CheckViolation);
        await Reject(account.Id, "learning", Now, PostgresErrorCodes.CheckViolation);
        await Reject(account.Id, "social", Now.AddSeconds(-1), PostgresErrorCodes.CheckViolation);
        await Service(db, account).SelectAsync("learning", null);
        await Reject(account.Id, "hobbies", null, PostgresErrorCodes.UniqueViolation);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE oid = 'andril_identity.account_starter_memberships'::regclass").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_policies WHERE schemaname = 'andril_identity' AND tablename = 'account_starter_memberships'").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task AccountsRemainIsolatedAndCurrentPoliciesAreRechecked()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var first = await Seed(database);
        var second = await Seed(database);
        await using var db = database.Open();
        await Service(db, first).SelectAsync("social", true);
        Assert.Null((await Service(db, second).GetAsync()).Membership);
        var catalog = new Catalog { TermsVersion = "test-next" };
        await Assert.ThrowsAsync<StarterModulePrerequisitesException>(() => Service(db, second, catalog: catalog).SelectAsync("learning", null));
        Assert.Single(await db.StarterMemberships.ToArrayAsync());
        await Service(db, second).SelectAsync("hobbies", null);
        Assert.Equal("social", (await Service(db, first).GetAsync()).Membership!.Module);
        Assert.Equal("hobbies", (await Service(db, second).GetAsync()).Membership!.Module);
    }

    [LocalPostgresFact]
    public async Task ConcurrentDobCorrectionIsObservedBeforeActivation()
    {
        await using var database = await LocalAccountDatabase.CreateAsync();
        var account = await Seed(database);
        await using var editor = database.Open();
        await using var transaction = await editor.Database.BeginTransactionAsync();
        await editor.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE andril_identity.account_demographics SET date_of_birth = {new DateOnly(2011, 11, 15)}
            WHERE account_id = {account.Id}
            """);
        var probe = new DemographicsLockProbe();
        await using var selector = database.Open(probe);
        var selection = Service(selector, account).SelectAsync("social", true);
        await probe.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<StarterModuleIneligibleException>(() => selection);
        Assert.Empty(await selector.StarterMemberships.ToArrayAsync());
    }

    private static async Task<Account> Seed(LocalAccountDatabase database)
    {
        await using var db = database.Open();
        var account = await new AccountRepository(db).GetOrCreateAsync(Guid.NewGuid(), Now);
        await new AccountDemographicsRepository(db).SaveAsync(account.Id, new(2008, 11, 15), "test", Now);
        await new AccountPolicyRepository(db).AppendAsync(account.Id,
            [new(PolicyActions.TermsAgreed, "test-terms"), new(PolicyActions.PrivacyAcknowledged, "test-privacy")], Now);
        return account;
    }
    private static StarterModuleService Service(AccountsDbContext db, Account account, Clock? clock = null, Catalog? catalog = null) =>
        new(new User(account.SupabaseUserId), new AccountRepository(db), new StarterModuleRepository(db), catalog ?? new Catalog(), clock ?? new Clock(Now));
    private sealed record User(Guid? UserId) : ICurrentUser { public bool IsAuthenticated => true; }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Catalog : IPolicyCatalogProvider
    {
        internal string TermsVersion = "test-terms";
        public PolicyCatalog GetCurrent() => new(new(TermsVersion, "https://policy.test/terms", "en"),
            new("test-privacy", "https://policy.test/privacy", "en"), new("test-guidelines", "https://policy.test/guidelines", "en"));
    }
    private sealed class DemographicsLockProbe : DbCommandInterceptor
    {
        internal readonly TaskCompletionSource Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("account_demographics", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
