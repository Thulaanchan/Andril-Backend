using System.Data.Common;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Andril.IntegrationTests.Database;

/// <summary>Reuses the existing disposable M2/Common authority fixture. Its Diary role is not
/// used for CV access: CV has a separate restricted login and Learning-owned table grants.</summary>
internal sealed class CvPostgresFixture : IAsyncDisposable
{
    internal DiaryPostgresFixture Shared { get; private set; } = null!;
    internal DiaryPostgresFixture.Authority Owner => Shared.Owner;
    private string? role;
    private string? connection;
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("SYNTHETIC-CV-LOST-ACK"));
    }
    internal static async Task<CvPostgresFixture> Create()
    {
        var f = new CvPostgresFixture { Shared = await DiaryPostgresFixture.Create() };
        try
        {
            await LearningPostgresFixture.Migrate(f.Shared.Database);
            var name = "andril_cv_runtime_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var db = f.OpenOwner(); await db.Database.OpenConnectionAsync();
            await using var sql = new NpgsqlCommand($"CREATE ROLE {name} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)db.Database.GetDbConnection());
            await sql.ExecuteNonQueryAsync(); f.role = name;
            sql.CommandText = $"GRANT USAGE ON SCHEMA andril_learning TO {name}; GRANT SELECT,INSERT ON andril_learning.learning_cvs TO {name}; GRANT UPDATE(template,include_contact_information,sections) ON andril_learning.learning_cvs TO {name}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {name}";
            await sql.ExecuteNonQueryAsync();
            f.connection = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Username = name, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal LearningDbContext OpenOwner() => LearningPostgresFixture.Open(Shared.Database);
    private DbContextOptions<LearningDbContext> Options(bool loseAck = false)
    {
        var builder = new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection);
        if (loseAck) builder.AddInterceptors(new LostAcknowledgement());
        return builder.Options;
    }
    internal LearningDbContext OpenRuntime() => new(Options());
    internal IsolatedLearningCvRepository Repository(DiaryPostgresFixture.Authority? authority = null,
        Func<string, CancellationToken, Task>? checkpoint = null, bool loseAck = false)
    {
        var a = authority ?? Owner;
        return new(Options(loseAck), a, a, a) { TestCheckpoint = checkpoint };
    }
    internal LearningCvApplicationService Service => new(Repository());
    internal static async Task Bind(LearningDbContext db, DiaryPostgresFixture.Authority owner)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{owner.Subject.ToString()},false),set_config('andril.account',{owner.Account.ToString()},false),set_config('andril.module','learning',false)");
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (role is not null)
            {
                await using var db = OpenOwner(); await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"REVOKE UPDATE(template,include_contact_information,sections) ON andril_learning.learning_cvs FROM {role}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}", (NpgsqlConnection)db.Database.GetDbConnection());
                await sql.ExecuteNonQueryAsync();
            }
        }
        finally { connection = null; await Shared.DisposeAsync(); }
    }
}
