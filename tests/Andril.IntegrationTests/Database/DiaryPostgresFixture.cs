using System.Data;
using System.Data.Common;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Diary;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Andril.IntegrationTests.Database;

/// <summary>Existing disposable M2 infrastructure, separated owner provisioning/runtime login.
/// Existing Common module/profile contracts are synthetic in this isolated host only.</summary>
internal sealed class DiaryPostgresFixture : IAsyncDisposable
{
    internal sealed class Authority(Guid subject, Guid account) : ICurrentUser, IPrivateModuleAccess, IPrivateProfileService
    {
        internal readonly Dictionary<string, Guid> Profiles = new(StringComparer.Ordinal)
        { ["learning"] = Guid.NewGuid(), ["hobbies"] = Guid.NewGuid(), ["social"] = Guid.NewGuid() };
        internal Guid Subject = subject;
        internal Guid Account = account;
        internal bool Denied, Unavailable, Unauthenticated;
        public Guid? UserId => Subject;
        public bool IsAuthenticated => !Unauthenticated;
        public Task RequireAsync(string module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable) throw new ProfileContractPendingException();
            if (Denied || !Profiles.ContainsKey(module)) throw new ProfileAccessDeniedException();
            return Task.CompletedTask;
        }
        public async Task<T> WithAuthorizedProfileAsync<T>(string module, Func<PrivateProfileDto, Task<T>> action, CancellationToken cancellationToken = default)
        {
            await RequireAsync(module, cancellationToken);
            return await action(new(Profiles[module], module, DateTimeOffset.UnixEpoch));
        }
        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            WithAuthorizedProfileAsync(module, p => Task.FromResult(p), cancellationToken);
        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fixture provisioning must be explicit.");
    }

    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("SYNTHETIC-DIARY-COMMIT-ACK"));
    }

    internal LocalAccountDatabase Database { get; private set; } = null!;
    internal Authority Owner { get; } = new(Guid.NewGuid(), Guid.NewGuid());
    private string? runtimeRole;
    private string? runtimeConnection;

    internal static async Task<DiaryPostgresFixture> Create()
    {
        var f = new DiaryPostgresFixture { Database = await LocalAccountDatabase.CreateAsync() };
        try
        {
            await using (var db = f.OpenOwner()) await db.Database.MigrateAsync();
            foreach (var module in f.Owner.Profiles.Keys)
                await LearningPostgresFixture.Profile(f.Database, new LearningSetupScope(f.Owner.Account, f.Owner.Profiles[module]), f.Owner.Subject, module);
            f.runtimeRole = "andril_diary_runtime_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var owner = f.OpenOwner(); await owner.Database.OpenConnectionAsync();
            await using var sql = new NpgsqlCommand($"CREATE ROLE {f.runtimeRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)owner.Database.GetDbConnection());
            // Identifiers/credentials are server random only; never log DDL or credentials.
            await sql.ExecuteNonQueryAsync();
            sql.CommandText = $"GRANT USAGE ON SCHEMA andril_diary TO {f.runtimeRole}; GRANT SELECT,INSERT,DELETE ON andril_diary.module_diary_entries TO {f.runtimeRole}; GRANT UPDATE(reflection_title,reflection_body,plans) ON andril_diary.module_diary_entries TO {f.runtimeRole}; GRANT EXECUTE ON FUNCTION andril_diary.scope_visible(uuid,text),andril_diary.lock_owner(uuid,uuid,text) TO {f.runtimeRole}";
            await sql.ExecuteNonQueryAsync();
            f.runtimeConnection = new NpgsqlConnectionStringBuilder(owner.Database.GetConnectionString())
            { Username = f.runtimeRole, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    internal DiaryDbContext OpenOwner()
    {
        using var accounts = Database.Open();
        return new(new DbContextOptionsBuilder<DiaryDbContext>().UseNpgsql(accounts.Database.GetConnectionString()).Options);
    }
    internal DiaryDbContext OpenRuntime() => new(RuntimeOptions());
    private DbContextOptions<DiaryDbContext> RuntimeOptions(bool loseAcknowledgement = false)
    {
        var b = new DbContextOptionsBuilder<DiaryDbContext>().UseNpgsql(runtimeConnection);
        if (loseAcknowledgement) b.AddInterceptors(new LostAcknowledgement());
        return b.Options;
    }
    internal IsolatedModuleDiaryRepository Repository(Authority? authority = null, Func<string, CancellationToken, Task>? checkpoint = null, bool loseAcknowledgement = false)
    {
        var a = authority ?? Owner;
        return new(RuntimeOptions(loseAcknowledgement), a, a, a) { TestCheckpoint = checkpoint };
    }
    internal ModuleDiaryEntry Entry(DateOnly date, string module = "learning", string body = "SYNTHETIC-PRIVATE-DIARY")
    {
        var entry = new ModuleDiaryEntry(Owner.Profiles[module], module, date);
        entry.SetReflection("SYNTHETIC-TITLE", body);
        entry.AddPlan(new DiaryPlan(Guid.NewGuid(), "SYNTHETIC-PLAN", "SYNTHETIC-DESCRIPTION"));
        return entry;
    }
    internal static async Task Bind(DiaryDbContext db, Authority a, string module)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{a.Subject.ToString()},false),set_config('andril.account',{a.Account.ToString()},false),set_config('andril.module',{module},false)");
    }
    internal async Task Lifecycle(string module, bool active)
    {
        await using var db = OpenOwner(); await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Owner.Subject.ToString()},true),set_config('andril.account',{Owner.Account.ToString()},true),set_config('andril.module',{module},true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state={(active ? "active" : "inactive")},revision=revision+1 WHERE account_id={Owner.Account} AND module={module}");
        await tx.CommitAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (runtimeRole is not null)
            {
                await using var db = OpenOwner(); await db.Database.OpenConnectionAsync();
                await using var sql = new NpgsqlCommand($"REVOKE UPDATE(reflection_title,reflection_body,plans) ON andril_diary.module_diary_entries FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_diary FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_diary FROM {runtimeRole}; REVOKE ALL PRIVILEGES ON SCHEMA andril_diary FROM {runtimeRole}; DROP ROLE {runtimeRole}", (NpgsqlConnection)db.Database.GetDbConnection());
                await sql.ExecuteNonQueryAsync();
            }
        }
        finally { runtimeConnection = null; await Database.DisposeAsync(); }
    }
}
