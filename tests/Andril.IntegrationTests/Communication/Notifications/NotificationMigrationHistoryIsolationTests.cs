
using System.Linq;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// D6 offline-only migration-history configuration tests.
///
/// No connection string, database connection, SQL execution,
/// schema creation, or migration execution is performed.
///
/// These tests do not modify production DbContext registration.
/// </summary>
public sealed class NotificationMigrationHistoryIsolationTests
{
    private const string NotificationsSchema =
        "andril_notifications";

    private const string NotificationsHistory =
        "__EFMigrationsHistory_Notifications";

    private const string AccountsSchema =
        "andril_identity";

    private const string AccountsHistory =
        "__EFMigrationsHistory";

    private static DbContextOptions<NotificationsDbContext>
        CreateNotificationsOptions()
    {
        return new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(npgsql =>
                npgsql.MigrationsHistoryTable(
                    NotificationsHistory,
                    NotificationsSchema))
            .Options;
    }

    private static DbContextOptions<AccountsDbContext>
        CreateAccountsOptions()
    {
        // Test-only representation of the existing M2
        // migration-history configuration.
        // Does not access or modify production DI.
        return new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql(npgsql =>
                npgsql.MigrationsHistoryTable(
                    AccountsHistory,
                    AccountsSchema))
            .Options;
    }

    private static RelationalOptionsExtension GetHistoryOptions(
        DbContextOptions options)
    {
        return Assert.Single(
            options.Extensions.OfType<RelationalOptionsExtension>());
    }

    [Fact]
    public void NotificationsHistory_ShouldUseDedicatedSchemaAndTable()
    {
        var options = CreateNotificationsOptions();

        var history = GetHistoryOptions(options);

        Assert.Equal(
            NotificationsHistory,
            history.MigrationsHistoryTableName);

        Assert.Equal(
            NotificationsSchema,
            history.MigrationsHistoryTableSchema);

        // No database connection string is configured.
        Assert.Null(history.ConnectionString);
    }

    [Fact]
    public void NotificationsAndAccountsHistory_ShouldRemainIndependent()
    {
        var notificationsOptions = CreateNotificationsOptions();
        var accountsOptions = CreateAccountsOptions();

        var notifications = GetHistoryOptions(notificationsOptions);
        var accounts = GetHistoryOptions(accountsOptions);

        // Verify the Notifications test-only configuration.
        Assert.Equal(
            NotificationsHistory,
            notifications.MigrationsHistoryTableName);

        Assert.Equal(
            NotificationsSchema,
            notifications.MigrationsHistoryTableSchema);

        // Verify the independent test-only Accounts options.
        Assert.Equal(
            AccountsHistory,
            accounts.MigrationsHistoryTableName);

        Assert.Equal(
            AccountsSchema,
            accounts.MigrationsHistoryTableSchema);

        Assert.NotEqual(
            notifications.MigrationsHistoryTableName,
            accounts.MigrationsHistoryTableName);

        Assert.NotEqual(
            notifications.MigrationsHistoryTableSchema,
            accounts.MigrationsHistoryTableSchema);

        // Neither configuration requires a live connection.
        Assert.Null(notifications.ConnectionString);
        Assert.Null(accounts.ConnectionString);
    }
}
