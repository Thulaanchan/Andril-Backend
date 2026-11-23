
using System;
using System.Linq;
using System.Reflection;
using Andril.Communication.Notifications;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Communication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// D8-C1: Offline PostgreSQL query-scope security tests.
///
/// No database connection, SQL execution, migrations,
/// or production service activation.
/// </summary>
public sealed class PostgresNotificationRepositorySqlScopeTests
{
    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public void ScopedQuery_ShouldFilterByModuleAndProfile(
        NotificationModule module)
    {
        // Dummy database settings. ToQueryString does not connect.
        var options =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;" +
                    "Database=andril_offline_dummy;" +
                    "Username=dummy;Password=dummy")
                .Options;

        using var context =
            new NotificationsDbContext(options);

        var repository =
            new PostgresNotificationRepository(context);

        var scope = new NotificationModuleScope(
            module,
            Guid.NewGuid());

        // Access existing private Scoped() query builder
        // without changing production code.
        var method =
            typeof(PostgresNotificationRepository)
                .GetMethod(
                    "Scoped",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

        Assert.NotNull(method);

        var query = Assert.IsAssignableFrom<IQueryable>(
            method.Invoke(
                repository,
                new object[] { scope }));

        var sql = query.ToQueryString();

        // PostgreSQL can use quoted or unquoted identifiers.
        var normalizedSql = sql.Replace(
            "\"",
            string.Empty,
            StringComparison.Ordinal);

        // 1. Correct Notifications schema and table.
        Assert.Contains(
            "FROM andril_notifications.notifications",
            normalizedSql,
            StringComparison.OrdinalIgnoreCase);

        // 2. Query must contain a WHERE clause.
        var whereIndex = normalizedSql.IndexOf(
            "WHERE",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(
            whereIndex >= 0,
            "Expected an SQL WHERE clause.");

        var whereSql = normalizedSql[whereIndex..];

        // 3. Exact recipient private profile predicate.
        Assert.Matches(
            @"(?i)\brecipient_module_profile_id\s*=\s*@",
            whereSql);

        // 4. Separate module predicate.
        // Avoid matching the word 'module' inside
        // recipient_module_profile_id.
        Assert.Matches(
            @"(?i)\bmodule\s*=\s*@",
            whereSql);

        // 5. Both predicates must be combined.
        Assert.Contains(
            " AND ",
            whereSql,
            StringComparison.OrdinalIgnoreCase);

        // 6. Expected canonical backend module value.
        var expectedModule = module switch
        {
            NotificationModule.Learning => "learning",
            NotificationModule.Hobbies => "hobbies",
            NotificationModule.Social => "social",
            _ => throw new ArgumentOutOfRangeException(
                nameof(module))
        };

        Assert.Contains(
            expectedModule,
            normalizedSql,
            StringComparison.OrdinalIgnoreCase);

        // 7. Verify expected recipient profile value
        // appears in generated SQL parameter information.
        Assert.Contains(
            scope.RecipientModuleProfileId.ToString("D"),
            normalizedSql,
            StringComparison.OrdinalIgnoreCase);
    }
}
