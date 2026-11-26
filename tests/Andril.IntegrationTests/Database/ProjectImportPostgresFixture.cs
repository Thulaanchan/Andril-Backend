using System.Data.Common;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Andril.IntegrationTests.Database;

internal sealed class ProjectImportPostgresFixture : IAsyncDisposable
{
    internal PortfolioPostgresFixture Shared { get; private set; } = null!;
    internal DiaryPostgresFixture.Authority Owner => Shared.Owner;
    private string? role;
    private string? connection;
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("SYNTHETIC-IMPORT-LOST-ACK"));
    }
    internal static async Task<ProjectImportPostgresFixture> Create()
    {
        var f = new ProjectImportPostgresFixture { Shared = await PortfolioPostgresFixture.Create() };
        try
        {
            var name = "andril_import_runtime_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var db = f.Shared.Shared.OpenOwner(); await db.Database.OpenConnectionAsync();
            await using var sql = new NpgsqlCommand($"CREATE ROLE {name} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)db.Database.GetDbConnection());
            await sql.ExecuteNonQueryAsync(); f.role = name;
            sql.CommandText = $"GRANT USAGE ON SCHEMA andril_learning TO {name}; GRANT SELECT ON andril_learning.learning_cvs,andril_learning.learning_portfolios TO {name}; GRANT UPDATE(sections) ON andril_learning.learning_cvs TO {name}; GRANT SELECT,INSERT ON andril_learning.learning_project_import_reviews,andril_learning.learning_project_import_origins TO {name}; GRANT UPDATE(confirmed) ON andril_learning.learning_project_import_reviews TO {name}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {name}";
            await sql.ExecuteNonQueryAsync();
            f.connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Username = name, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    private DbContextOptions<LearningDbContext> Options(bool loseAck = false)
    { var b = new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection); if (loseAck) b.AddInterceptors(new LostAcknowledgement()); return b.Options; }
    internal LearningDbContext OpenRuntime() => new(Options());
    internal IsolatedLearningProjectImportRepository Repository(DiaryPostgresFixture.Authority? authority = null,
        Func<string, CancellationToken, Task>? checkpoint = null, bool loseAck = false, ICurrentUser? currentUser = null)
    { var a = authority ?? Owner; return new(Options(loseAck), currentUser ?? a, a, a) { TestCheckpoint = checkpoint }; }
    internal LearningProjectImportApplicationService Service => new(Repository());
    internal async Task<LearningDbContext> BoundOwner()
    { var db = Shared.Shared.OpenOwner(); try { await CvPostgresFixture.Bind(db, Owner); return db; } catch { await db.DisposeAsync(); throw; } }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (role is not null)
            {
                await using var db = Shared.Shared.OpenOwner(); await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"REVOKE UPDATE(sections) ON andril_learning.learning_cvs FROM {role}; REVOKE UPDATE(confirmed) ON andril_learning.learning_project_import_reviews FROM {role}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}", (NpgsqlConnection)db.Database.GetDbConnection());
                await sql.ExecuteNonQueryAsync();
            }
        }
        finally { connection = null; await Shared.DisposeAsync(); }
    }
}
