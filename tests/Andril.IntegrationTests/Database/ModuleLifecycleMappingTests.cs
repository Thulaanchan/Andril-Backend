using System.Data;
using System.Data.Common;
using Andril.Common.Accounts;
using Andril.Persistence.Accounts;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class ModuleLifecycleMappingTests
{
    private const string ConnectionString = "Host=localhost;Database=not_connected;Username=not_connected";

    [Fact]
    public void OfflineModelHasAnIndependentAccountModuleKeyAndARevisionConcurrencyToken()
    {
        var guard = new RejectConnections();
        using var context = new OfflineLifecycleContext(new DbContextOptionsBuilder<OfflineLifecycleContext>()
            .UseNpgsql(ConnectionString).AddInterceptors(guard).Options);
        var mapping = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ModuleLifecycleRow))!;

        Assert.Equal("andril_identity", mapping.GetSchema());
        Assert.Equal("account_module_lifecycles", mapping.GetTableName());
        Assert.Equal(new[] { nameof(ModuleLifecycleRow.AccountId), nameof(ModuleLifecycleRow.Module) },
            mapping.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Empty(mapping.GetIndexes());

        var account = Assert.Single(mapping.GetForeignKeys());
        Assert.Equal(typeof(Account), account.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Account.Id), Assert.Single(account.PrincipalKey.Properties).Name);
        Assert.Equal(nameof(ModuleLifecycleRow.AccountId), Assert.Single(account.Properties).Name);
        Assert.Equal(DeleteBehavior.Restrict, account.DeleteBehavior);

        Assert.All(mapping.GetProperties(), property => Assert.False(property.IsNullable));
        var revision = mapping.FindProperty(nameof(ModuleLifecycleRow.Revision))!;
        Assert.True(revision.IsConcurrencyToken);
        Assert.Equal("bigint", revision.GetColumnType());
        Assert.Equal(ValueGenerated.Never, revision.ValueGenerated);
        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void OfflinePostgreSqlDdlContainsTheApprovedShapeWithoutAnyBackfill()
    {
        var guard = new RejectConnections();
        using var context = new OfflineLifecycleContext(new DbContextOptionsBuilder<OfflineLifecycleContext>()
            .UseNpgsql(ConnectionString).AddInterceptors(guard).Options);

        var sql = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE andril_identity.account_module_lifecycles", sql);
        Assert.Contains("account_id uuid NOT NULL", sql);
        Assert.Contains("module character varying(16) COLLATE \"C\" NOT NULL", sql);
        Assert.Contains("state character varying(8) COLLATE \"C\" NOT NULL", sql);
        Assert.Contains("revision bigint NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (account_id, module)", sql);
        Assert.Contains("FOREIGN KEY (account_id) REFERENCES andril_identity.accounts (id) ON DELETE RESTRICT", sql);
        Assert.Contains("CHECK (module IN ('learning', 'hobbies', 'social'))", sql);
        Assert.Contains("CHECK (state IN ('active', 'inactive'))", sql);
        Assert.Contains("CHECK (revision > 0)", sql);
        Assert.Contains("isfinite(first_activated_at_utc) AND isfinite(state_changed_at_utc) AND state_changed_at_utc >= first_activated_at_utc", sql);
        Assert.Contains("first_activated_at_utc timestamp with time zone NOT NULL DEFAULT (statement_timestamp())", sql);
        Assert.Contains("state_changed_at_utc timestamp with time zone NOT NULL DEFAULT (statement_timestamp())", sql);
        Assert.DoesNotContain("INSERT INTO", sql);
        Assert.DoesNotContain("account_starter_memberships", sql);
        Assert.DoesNotContain("private_module_profiles", sql);
        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void AccountsModelAndMigrationSnapshotIncludeTheAuthorizedMappingWithoutConnecting()
    {
        var guard = new RejectConnections();
        using var context = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(ConnectionString,
                options => options.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity"))
            .AddInterceptors(guard).Options);

        Assert.NotNull(context.Model.FindEntityType(typeof(ModuleLifecycleRow)));
        Assert.Contains(context.Model.GetEntityTypes(),
            entity => entity.GetTableName() == "account_module_lifecycles");
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(0, guard.Attempts);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void DeactivationOutcomeMigrationKeepsMembershipReferentialIntegrityForExistingRows()
    {
        var guard = new RejectConnections();
        using var context = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(ConnectionString).AddInterceptors(guard).Options);
        const string previous = "20261124130000_AddControlledModuleDeactivation";
        const string current = "20261124140000_AddControlledDeactivationOutcomes";
        var migration = context.GetService<IMigrator>();
        var upgrade = migration.GenerateScript(previous, current);
        var downgrade = migration.GenerateScript(current, previous);
        Assert.Contains("CASE WHEN revision > 0 THEN module END", upgrade);
        Assert.Contains("fk_lifecycle_receipts_result_membership", upgrade);
        Assert.Contains("fk_lifecycle_receipts_accounts", upgrade);
        Assert.Contains("revision = 0", upgrade);
        Assert.Contains("ADD CONSTRAINT ck_lifecycle_receipt_revision", downgrade);
        Assert.DoesNotContain("DELETE FROM andril_identity.account_module_lifecycle_receipts", downgrade);
        Assert.Equal(0, guard.Attempts);
    }

    private sealed class OfflineLifecycleContext(DbContextOptions<OfflineLifecycleContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new AccountConfiguration());
            modelBuilder.ApplyConfiguration(new ModuleLifecycleConfiguration());
        }
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("The offline model must never open a database connection.");
        }
    }
}
