using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Andril.Persistence.DbContexts;

public sealed class HobbiesMapDbContextFactory : IDesignTimeDbContextFactory<HobbiesMapDbContext>
{
    public HobbiesMapDbContext CreateDbContext(string[] args)
    {
        // Configure the provider for scaffolding and SQL generation without reading deployment settings.
        var options = new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=andril_model_only;Username=unused", HobbiesDatabaseOptions.ConfigureMapHistory)
            .AddInterceptors(new OfflineConnectionGuard())
            .Options;

        return new HobbiesMapDbContext(options);
    }

    private sealed class OfflineConnectionGuard : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            throw new InvalidOperationException("The Hobbies Map design-time context only supports offline operations.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Hobbies Map design-time context only supports offline operations.");
    }
}
