using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Persistence.Accounts;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Lifecycle;

// These tests inspect the disposable-role inventory. Keep that inventory stable
// while exercising concurrency explicitly inside each test using real connections.
[CollectionDefinition("ControlledLifecyclePostgreSQL", DisableParallelization = true)]
public sealed class ControlledLifecyclePostgresCollection;

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

internal sealed class SyntheticDeactivationAuthority : IControlledDeactivationAuthority
{
    internal ModuleAuthorizationOutcome Outcome = ModuleAuthorizationOutcome.Satisfied;
    internal Guid Version = Guid.NewGuid();
    internal Guid? CurrentVersion;
    internal ModuleMembershipScope? ScopeOverride;
    internal Guid? SubjectOverride;
    public Task<ModuleDeactivationEvidence> ReadAsync(Guid subject, ModuleMembershipScope scope,
        DateTimeOffset databaseTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModuleDeactivationEvidence(ScopeOverride ?? scope, SubjectOverride ?? subject,
            Outcome, Version, CurrentVersion ?? Version, databaseTime));
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
    internal SyntheticDeactivationAuthority DeactivationAuthority { get; } = new();
    internal Guid Subject { get; } = Guid.NewGuid();
    internal Guid AccountId { get; private set; }
    private bool created;
    private ControlledLifecycleDatabase(string admin) { this.admin = admin; }

    internal static async Task<ControlledLifecycleDatabase> CreateAsync(int age = 25, bool consent = true,
        bool failAfterDatabaseCreation = false, string? targetMigration = null)
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
            // PostgreSQL 17 requires SET membership to assign database ownership.
            // The bootstrap already receives ADMIN from CREATE ROLE; enable only SET.
            await fixture.Admin($"GRANT {fixture.migrationRole} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE");
            // Cleanup must terminate only this fixture's runtime sessions as their
            // own role. Do not grant pg_signal_backend or migration membership to runtime.
            await fixture.Admin($"GRANT {fixture.runtimeRole} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE");
            await fixture.Admin($"CREATE DATABASE {fixture.name} OWNER {fixture.migrationRole}");
            fixture.created = true;
            if (failAfterDatabaseCreation) throw new IOException("Injected disposable fixture setup failure.");
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
                await db.GetService<IMigrator>().MigrateAsync(targetMigration);
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
                foreach (var table in new[] { "account_module_lifecycles", "account_module_lifecycle_receipts", "account_module_lifecycle_audit" })
                    await fixture.Admin($"CREATE POLICY test_owner_inspection ON andril_identity.{table} TO {fixture.migrationRole} USING (true) WITH CHECK (true)", true, fixture.name);
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
        // Migration-owner inspection on the random test database. Its explicit
        // fixture-only inspection policy is inaccessible to the runtime role.
        var settings = new NpgsqlConnectionStringBuilder(MigrationConnection);
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        await using (var role = new NpgsqlCommand($"SET ROLE {migrationRole}", db)) await role.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, db);
        return await command.ExecuteNonQueryAsync();
    }
    internal async Task<long> Count(string table)
    {
        if (table is not ("account_module_lifecycles" or "account_module_lifecycle_receipts" or "account_module_lifecycle_audit"))
            throw new ArgumentException(nameof(table));
        var settings = new NpgsqlConnectionStringBuilder(MigrationConnection);
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        await using (var role = new NpgsqlCommand($"SET ROLE {migrationRole}", db)) await role.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM andril_identity.{table}", db);
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task Admin(string sql, bool asMigration = false, string? database = null, bool asRuntime = false)
    {
        var settings = new NpgsqlConnectionStringBuilder(database is null ? admin : MigrationConnection);
        if (database is not null) settings.Database = database;
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        if (asMigration || asRuntime)
        {
            await using var role = new NpgsqlCommand($"SET ROLE {(asRuntime ? runtimeRole : migrationRole)}", db);
            await role.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (RuntimeConnection.Length > 0)
        {
            using var pool = new NpgsqlConnection(RuntimeConnection);
            NpgsqlConnection.ClearPool(pool);
        }
        if (created)
        {
            await Admin($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{name}' AND usename='{runtimeRole}' AND pid<>pg_backend_pid()", asRuntime: true);
            await Admin($"DROP DATABASE {name} WITH (FORCE)", true);
            created = false;
        }
        await Admin($"DROP ROLE IF EXISTS {runtimeRole}");
        await Admin($"DROP ROLE IF EXISTS {migrationRole}");
    }
}
