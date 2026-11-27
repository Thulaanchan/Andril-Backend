using Andril.API.Extensions;
using Andril.Persistence.Configurations;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class EconomyMigrationConsistencyTests
{
    [Theory]
    [InlineData("AndrilEconomy")]
    [InlineData("AndrilAccounts")]
    public void RuntimeAndDesignTimeUseCanonicalEconomyHistory(string connectionName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:" + connectionName] =
                    "Host=127.0.0.1;Database=not_connected;Username=unused"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEconomy(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<EconomyDbContext>();
        using var design = new EconomyDbContextFactory().CreateDbContext([]);
        foreach (var context in new[] { runtime, design })
        {
            var options = context.GetService<IDbContextOptions>().Extensions
                .OfType<RelationalOptionsExtension>().Single();
            Assert.Equal("__EFMigrationsHistory_Economy", options.MigrationsHistoryTableName);
            Assert.Equal("andril_economy", options.MigrationsHistoryTableSchema);
        }
        using var accounts = new AccountsDbContextFactory().CreateDbContext([]);
        var accountsOptions = accounts.GetService<IDbContextOptions>().Extensions
            .OfType<RelationalOptionsExtension>().Single();
        Assert.Equal("__EFMigrationsHistory", accountsOptions.MigrationsHistoryTableName);
        Assert.Equal("andril_identity", accountsOptions.MigrationsHistoryTableSchema);
    }

    [Fact]
    public void SingleInitialMigrationSnapshotAndTargetMatchCurrentEconomyModel()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var assembly = db.GetService<IMigrationsAssembly>();
        Assert.Equal("20261126160000_InitialEconomy", Assert.Single(assembly.Migrations).Key);
        Assert.False(db.Database.HasPendingModelChanges());
        var migration = assembly.CreateMigration(assembly.Migrations.Single().Value,
            db.Database.ProviderName!);
        var target = db.GetService<IModelRuntimeInitializer>().Initialize(
            migration.TargetModel, designTime: true);
        var current = db.GetService<IDesignTimeModel>().Model;
        Assert.Empty(db.GetService<IMigrationsModelDiffer>().GetDifferences(
            target.GetRelationalModel(), current.GetRelationalModel()));
    }

    [Fact]
    public void InitialOperationsMatchCurrentTablesConstraintsAndIndexes()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single().Value,
            db.Database.ProviderName!);
        var current = db.GetService<IDesignTimeModel>().Model;
        var expected = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, current.GetRelationalModel());
        static string TableShape(CreateTableOperation table) => string.Join("|",
            new[] { table.Schema, table.Name, table.PrimaryKey!.Name,
                string.Join(",", table.PrimaryKey.Columns) }
            .Concat(table.Columns.OrderBy(c => c.Name).Select(c =>
                $"column:{c.Name}:{c.ColumnType}:{c.IsNullable}:{c.MaxLength}"))
            .Concat(table.CheckConstraints.OrderBy(c => c.Name).Select(c =>
                $"check:{c.Name}:{c.Sql}"))
            .Concat(table.ForeignKeys.OrderBy(f => f.Name).Select(f =>
                $"fk:{f.Name}:{string.Join(",", f.Columns)}:{f.PrincipalSchema}:{f.PrincipalTable}:{string.Join(",", f.PrincipalColumns ?? [])}:{f.OnDelete}")));
        static string IndexShape(CreateIndexOperation index) =>
            $"{index.Schema}:{index.Table}:{index.Name}:{index.IsUnique}:{string.Join(",", index.Columns)}:{index.Filter}";
        Assert.Equal(expected.OfType<CreateTableOperation>().Select(TableShape).OrderBy(s => s),
            migration.UpOperations.OfType<CreateTableOperation>().Select(TableShape).OrderBy(s => s));
        Assert.Equal(expected.OfType<CreateIndexOperation>().Select(IndexShape).OrderBy(s => s),
            migration.UpOperations.OfType<CreateIndexOperation>().Select(IndexShape).OrderBy(s => s));
    }

    [Fact]
    public void InitialDownDropsOnlyTheFourEconomyTables()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single().Value,
            db.Database.ProviderName!);
        Assert.All(migration.DownOperations, operation => Assert.IsType<DropTableOperation>(operation));
        var drops = migration.DownOperations.Cast<DropTableOperation>().ToArray();
        Assert.All(drops, table => Assert.Equal("andril_economy", table.Schema));
        Assert.Equal(new[] { "coin_transactions", "coin_wallets", "gift_transactions", "premium_entitlements" },
            drops.Select(table => table.Name).OrderBy(name => name));
    }

    [Fact]
    public void InitialScriptUsesCanonicalHistoryAndNeverManagesAccountTable()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("andril_economy.\"__EFMigrationsHistory_Economy\"", sql);
        Assert.Contains("REFERENCES andril_identity.accounts (id)", sql);
        Assert.DoesNotContain("CREATE TABLE andril_identity.accounts", sql);
        Assert.DoesNotContain("ALTER TABLE andril_identity.accounts", sql);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", sql);
        Assert.DoesNotContain("CREATE TABLE \"Account\"", sql);
    }
}
