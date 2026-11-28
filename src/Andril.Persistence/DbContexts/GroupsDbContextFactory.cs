using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Safe design-time factory for <see cref="GroupsDbContext"/>.
/// Configures Npgsql provider for EF Core CLI migrations and schema scaffolding
/// while enforcing fail-closed offline safety via <see cref="OfflineConnectionGuard"/>.
/// Prevents accidental connection opening or live credential leakage during design-time runs.
/// </summary>
public sealed class GroupsDbContextFactory : IDesignTimeDbContextFactory<GroupsDbContext>
{
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory_Groups";

    public GroupsDbContext CreateDbContext(string[] args)
    {
        // Configure provider for scaffolding and SQL generation without reading deployment settings.
        var options = new DbContextOptionsBuilder<GroupsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=andril_model_only;Username=unused", npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTableName, GroupsDbContext.Schema);
            })
            .AddInterceptors(new OfflineConnectionGuard())
            .Options;

        return new GroupsDbContext(options);
    }

    private sealed class OfflineConnectionGuard : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("The Groups design-time context only supports offline operations.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Groups design-time context only supports offline operations.");
    }
}
