using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.Accounts;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.IntegrationTests.Lifecycle;

// Synthetic authority used only by the opted-in integration host. There is deliberately
// no corresponding production standing/revocation implementation until its contract exists.
internal sealed class SyntheticAuthority : IControlledLifecycleAuthority, IOnboardingIdentity, IPolicyCatalogProvider
{
    internal bool Confirmed = true;
    internal ModuleAuthorizationOutcome Outcome = ModuleAuthorizationOutcome.Satisfied;
    internal bool? Restrictions = true;
    internal bool? SocialOptIn;
    internal Guid Version = Guid.NewGuid();
    internal Guid? CurrentVersion;
    internal bool ThrowUnavailable;
    public bool IsConfirmedFor(Guid subject) => subject != Guid.Empty && Confirmed;
    public PolicyCatalog GetCurrent() => new(new("test-terms", "https://example.invalid/terms", "en"),
        new("test-privacy", "https://example.invalid/privacy", "en"), new("test-community", "https://example.invalid/community", "en"));
    public Task<LifecycleAuthorityState> ReadAsync(Guid subject, Guid account, string module, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowUnavailable) throw new IOException("Synthetic dependency unavailable");
        return Task.FromResult(new LifecycleAuthorityState(Outcome, Restrictions, SocialOptIn, Version, CurrentVersion ?? Version));
    }
}

internal sealed class ControlledLifecycleDatabase : IAsyncDisposable
{
    private readonly string admin;
    private readonly string name = "andril_m2_test_" + Guid.NewGuid().ToString("N");
    private readonly string migrationRole = "m2_migrate_" + Guid.NewGuid().ToString("N");
    private readonly string runtimeRole = "m2_runtime_" + Guid.NewGuid().ToString("N");
    internal string MigrationConnection { get; private set; } = "";
    internal string RuntimeConnection { get; private set; } = "";
    internal SyntheticAuthority Authority { get; } = new();
    internal Guid Subject { get; } = Guid.NewGuid();
    internal Guid AccountId { get; private set; }
    private bool created;
    private ControlledLifecycleDatabase(string admin) { this.admin = admin; }

    internal static async Task<ControlledLifecycleDatabase> CreateAsync(int age = 25, bool consent = true)
    {
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ANDRIL_M2_TEST_POSTGRES"));
        if (settings.Host is not ("localhost" or "127.0.0.1" or "::1") || settings.Database != "postgres")
            throw new InvalidOperationException("Only an explicitly configured disposable loopback PostgreSQL server is allowed.");
        settings.Pooling = false;
        var fixture = new ControlledLifecycleDatabase(settings.ConnectionString);
        try
        {
            var migrationPassword = Guid.NewGuid().ToString("N");
            var runtimePassword = Guid.NewGuid().ToString("N");
            await fixture.Admin($"CREATE ROLE {fixture.migrationRole} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD '{migrationPassword}'");
            await fixture.Admin($"CREATE ROLE {fixture.runtimeRole} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS PASSWORD '{runtimePassword}'");
            await fixture.Admin($"CREATE DATABASE {fixture.name} OWNER {fixture.migrationRole}");
            fixture.created = true;
            settings.Database = fixture.name;
            settings.Username = fixture.migrationRole;
            settings.Password = migrationPassword;
            fixture.MigrationConnection = settings.ConnectionString;
            settings.Username = fixture.runtimeRole;
            settings.Password = runtimePassword;
            settings.Pooling = true;
            settings.MaxPoolSize = 1;
            settings.IncludeErrorDetail = false;
            fixture.RuntimeConnection = settings.ConnectionString;
            await using (var db = fixture.Open())
            {
                await db.Database.MigrateAsync();
                // Existing source tables already enable RLS. The test migration owner
                // can seed them; lifecycle FORCE RLS is not bypassed by that owner.
                var now = DateTimeOffset.UtcNow.AddMinutes(-5);
                var account = Account.Create(fixture.Subject, now);
                fixture.AccountId = account.Id;
                db.Accounts.Add(account);
                db.Demographics.Add(AccountDemographics.Create(account.Id, DateOnly.FromDateTime(now.UtcDateTime).AddYears(-age), "prefer_not_to_say", now));
                if (consent)
                {
                    db.PolicyRecords.Add(AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, "test-terms", now));
                    db.PolicyRecords.Add(AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, "test-privacy", now));
                }
                await db.SaveChangesAsync();
                // Identifiers below are exclusively generated fixed prefixes plus UUID-N;
                // PostgreSQL cannot parameterize role/database names in GRANT statements.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"""
                    REVOKE ALL ON DATABASE {fixture.name} FROM PUBLIC;
                    GRANT CONNECT ON DATABASE {fixture.name} TO {fixture.runtimeRole};
                    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                    GRANT USAGE ON SCHEMA andril_identity TO {fixture.runtimeRole};
                    GRANT SELECT ON andril_identity.accounts, andril_identity.account_demographics, andril_identity.account_policy_records TO {fixture.runtimeRole};
                    CREATE POLICY test_subject ON andril_identity.accounts FOR SELECT TO {fixture.runtimeRole}
                    USING (supabase_user_id = '{fixture.Subject}'::uuid AND supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid);
                    CREATE POLICY test_demographics ON andril_identity.account_demographics FOR SELECT TO {fixture.runtimeRole}
                    USING (account_id = '{fixture.AccountId}'::uuid AND account_id = nullif(current_setting('andril.account', true), '')::uuid);
                    CREATE POLICY test_policies ON andril_identity.account_policy_records FOR SELECT TO {fixture.runtimeRole}
                    USING (account_id = '{fixture.AccountId}'::uuid AND account_id = nullif(current_setting('andril.account', true), '')::uuid);
                    GRANT SELECT, INSERT ON andril_identity.account_module_lifecycles,
                        andril_identity.account_module_lifecycle_receipts, andril_identity.account_module_lifecycle_audit TO {fixture.runtimeRole};
                    GRANT UPDATE (state, revision, state_changed_at_utc) ON andril_identity.account_module_lifecycles TO {fixture.runtimeRole};
                    """);
#pragma warning restore EF1002
            }
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal ControlledModuleLifecycleStore Store() => new(RuntimeConnection, Authority, Authority, Authority, true);
    internal OfflineModuleLifecycleService Service(IModuleLifecycleStore? store = null) => new(store ?? Store());
    internal AccountsDbContext Open() => new(new DbContextOptionsBuilder<AccountsDbContext>()
        .UseNpgsql(MigrationConnection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")).Options);
    internal async Task<int> Sql(string sql)
    {
        // Privileged inspection/fixtures only, on the random test database. Never used
        // as the lifecycle runtime. Superuser inspection independently verifies RLS writes.
        var settings = new NpgsqlConnectionStringBuilder(admin) { Database = name };
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return await command.ExecuteNonQueryAsync();
    }
    internal async Task<long> Count(string table)
    {
        if (table is not ("account_module_lifecycles" or "account_module_lifecycle_receipts" or "account_module_lifecycle_audit"))
            throw new ArgumentException(nameof(table));
        var settings = new NpgsqlConnectionStringBuilder(admin) { Database = name };
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM andril_identity.{table}", db);
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task Admin(string sql)
    {
        await using var db = new NpgsqlConnection(admin);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (created) await Admin($"DROP DATABASE {name} WITH (FORCE)");
        await Admin($"DROP ROLE IF EXISTS {runtimeRole}");
        await Admin($"DROP ROLE IF EXISTS {migrationRole}");
    }
}
