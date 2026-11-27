using Andril.Common.Accounts;
using Andril.Economy.Premium;
using Andril.Economy.Wallet;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class EconomyAccountMappingTests
{
    [Fact]
    public void ExternalAccountMatchesAccountsOwnerAndIsExcludedFromEconomyMigrations()
    {
        using var economy = new EconomyDbContextFactory().CreateDbContext([]);
        using var accounts = new AccountsDbContextFactory().CreateDbContext([]);
        var principal = economy.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Account))!;
        var owner = accounts.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Account))!;
        Assert.Equal("accounts", principal.GetTableName());
        Assert.Equal("andril_identity", principal.GetSchema());
        Assert.True(principal.IsTableExcludedFromMigrations());
        Assert.False(owner.IsTableExcludedFromMigrations());
        var table = StoreObjectIdentifier.Table("accounts", "andril_identity");
        Assert.Equal(owner.GetProperties().Select(p => p.Name), principal.GetProperties().Select(p => p.Name));
        foreach (var property in principal.GetProperties())
        {
            var owned = owner.FindProperty(property.Name)!;
            Assert.Equal(owned.GetColumnName(table), property.GetColumnName(table));
            Assert.Equal(owned.GetColumnType(), property.GetColumnType());
            Assert.Equal(owned.IsNullable, property.IsNullable);
            Assert.Equal(owned.ValueGenerated, property.ValueGenerated);
        }
        Assert.Equal("id", principal.FindPrimaryKey()!.Properties.Single().GetColumnName(table));
    }

    [Fact]
    public void EconomyModelCreatesOnlyItsFourTablesAndReferencesTheOwnedAccountKey()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var model = db.GetService<IDesignTimeModel>().Model;
        Assert.Equal(5, model.GetEntityTypes().Count());
        foreach (var type in new[] { typeof(CoinWallet), typeof(EntitlementRecord) })
        {
            var fk = model.FindEntityType(type)!.GetForeignKeys().Single();
            Assert.Equal(typeof(Account), fk.PrincipalEntityType.ClrType);
            Assert.True(fk.PrincipalKey.IsPrimaryKey());
            Assert.Equal(nameof(Account.Id), fk.PrincipalKey.Properties.Single().Name);
        }
        var operations = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var tables = operations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(new[] { "coin_transactions", "coin_wallets", "gift_transactions", "premium_entitlements" },
            tables.Select(t => t.Name).OrderBy(n => n));
        Assert.All(tables, t => Assert.Equal("andril_economy", t.Schema));
        Assert.All(tables.SelectMany(t => t.ForeignKeys), fk =>
        {
            Assert.Equal("andril_identity", fk.PrincipalSchema);
            Assert.Equal("accounts", fk.PrincipalTable);
            Assert.Equal(new[] { "id" }, fk.PrincipalColumns);
        });
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model).Select(command => command.CommandText));
        Assert.DoesNotContain("CREATE TABLE andril_identity.accounts", sql);
        Assert.DoesNotContain("ALTER TABLE andril_identity.accounts", sql);
        Assert.DoesNotContain("DROP TABLE andril_identity.accounts", sql);
        Assert.Contains("REFERENCES andril_identity.accounts (id)", sql);
    }

    [Fact]
    public void PreservedInitialEconomyMigrationEnablesRlsOnEveryBusinessTable()
    {
        using var db = new EconomyDbContextFactory().CreateDbContext([]);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(
            assembly.Migrations["20261126160000_InitialEconomy"], db.Database.ProviderName!);
        var statements = migration.UpOperations.OfType<SqlOperation>().Select(op => op.Sql).ToArray();
        foreach (var table in new[] { "coin_wallets", "coin_transactions", "gift_transactions", "premium_entitlements" })
            Assert.Contains("ALTER TABLE andril_economy." + table + " ENABLE ROW LEVEL SECURITY;", statements);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DesignTimeFactoriesRejectConnectionOpening(bool economy)
    {
        using DbContext db = economy
            ? new EconomyDbContextFactory().CreateDbContext([])
            : new AccountsDbContextFactory().CreateDbContext([]);
        var error = Assert.Throws<InvalidOperationException>(() => db.Database.OpenConnection());
        Assert.Contains("only supports offline operations", error.Message);
    }
}
