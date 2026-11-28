using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Safe design-time factory for <see cref="AccountsDbContext"/>.
/// Configures Npgsql provider for EF Core CLI migrations and schema scaffolding
/// while enforcing fail-closed offline safety via <see cref="OfflineConnectionGuard"/>.
/// Prevents accidental connection opening or live credential leakage during design-time runs.
/// </summary>
public sealed class AccountsDbContextFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public const string MigrationsHistoryTableName = "__EFMigrationsHistory";
    public const string Schema = "andril_identity";

    public AccountsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AccountsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=andril_model_only;Username=unused", npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTableName, Schema);
            })
            .AddInterceptors(new OfflineConnectionGuard())
            .Options;

        return new AccountsDbContext(options);
    }

    private sealed class OfflineConnectionGuard : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("The Accounts design-time context only supports offline operations.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Accounts design-time context only supports offline operations.");
    }
}
