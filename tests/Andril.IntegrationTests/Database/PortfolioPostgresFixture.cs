using System.Data.Common;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Andril.IntegrationTests.Database;

/// <summary>Reuses CV/M2 disposable provisioning, with a separate least-privilege Portfolio
/// runtime login. Fixture credentials and provisioning privileges never enter runtime code.</summary>
internal sealed class PortfolioPostgresFixture : IAsyncDisposable
{
    internal CvPostgresFixture Shared { get; private set; } = null!;
    internal DiaryPostgresFixture.Authority Owner => Shared.Owner;
    private string? role;
    private string? connection;
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("SYNTHETIC-PORTFOLIO-LOST-ACK"));
    }
    internal static async Task<PortfolioPostgresFixture> Create()
    {
        var f = new PortfolioPostgresFixture { Shared = await CvPostgresFixture.Create() };
        try
        {
            var name = "andril_portfolio_runtime_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var db = f.Shared.OpenOwner(); await db.Database.OpenConnectionAsync();
            await using var sql = new NpgsqlCommand($"CREATE ROLE {name} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)db.Database.GetDbConnection());
            await sql.ExecuteNonQueryAsync(); f.role = name;
            sql.CommandText = $"GRANT USAGE ON SCHEMA andril_learning TO {name}; GRANT SELECT,INSERT,DELETE ON andril_learning.learning_portfolios TO {name}; GRANT UPDATE(template,projects) ON andril_learning.learning_portfolios TO {name}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {name}";
            await sql.ExecuteNonQueryAsync();
            f.connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Username = name, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    private DbContextOptions<LearningDbContext> Options(bool loseAck = false)
    {
        var b = new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection);
        if (loseAck) b.AddInterceptors(new LostAcknowledgement()); return b.Options;
    }
    internal LearningDbContext OpenRuntime() => new(Options());
    internal IsolatedLearningPortfolioRepository Repository(DiaryPostgresFixture.Authority? authority = null,
        Func<string, CancellationToken, Task>? checkpoint = null, bool loseAck = false)
    { var a = authority ?? Owner; return new(Options(loseAck), a, a, a) { TestCheckpoint = checkpoint }; }
    internal LearningPortfolioApplicationService Service => new(Repository());
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (role is not null)
            {
                await using var db = Shared.OpenOwner(); await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"REVOKE UPDATE(template,projects) ON andril_learning.learning_portfolios FROM {role}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}", (NpgsqlConnection)db.Database.GetDbConnection());
                await sql.ExecuteNonQueryAsync();
            }
        }
        finally { connection = null; await Shared.DisposeAsync(); }
    }
}
