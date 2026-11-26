using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.AI;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.IntegrationTests.Database;

internal sealed class LearningAiConversationPostgresFixture : IAsyncDisposable
{
    internal PortfolioPostgresFixture Shared { get; private set; } = null!;
    internal DiaryPostgresFixture.Authority Owner => Shared.Owner;
    internal sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal Clock Time { get; } = new();
    internal bool PrivacyDenied { get; set; }
    internal bool PrivacyUnavailable { get; set; }
    private string? role;
    private string? connection;
    private sealed class ActorReader(DiaryPostgresFixture.Authority authority, ICurrentUser? currentUser = null) : IAiActorAuthorization
    {
        public async Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var user = currentUser ?? authority;
            if (module != AiModule.Learning || !user.IsAuthenticated || user.UserId != authority.Subject || authority.Denied) return null;
            await authority.RequireAsync("learning", ct);
            return new(authority.Account, authority.Profiles["learning"], AiModule.Learning);
        }
    }
    private sealed class PrivacyGate(LearningAiConversationPostgresFixture f) : IAiPrivacyNoticeGate
    {
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(f.PrivacyUnavailable ? AiPrivacyNoticeGateOutcome.PolicyNotVerified : f.PrivacyDenied ? AiPrivacyNoticeGateOutcome.Denied : AiPrivacyNoticeGateOutcome.Satisfied); }
    }
    internal AiAuthorizedActor Actor(DiaryPostgresFixture.Authority? authority = null)
    { var a = authority ?? Owner; return new(a.Account, a.Profiles["learning"], AiModule.Learning); }
    internal static async Task<LearningAiConversationPostgresFixture> Create()
    {
        var f = new LearningAiConversationPostgresFixture { Shared = await PortfolioPostgresFixture.Create() };
        try
        {
            await using var owner = f.Shared.Shared.OpenOwner(); await owner.Database.OpenConnectionAsync();
            await using var model = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>().UseNpgsql(owner.Database.GetConnectionString()).Options);
            await owner.Database.ExecuteSqlRawAsync(model.Database.GenerateCreateScript());
            foreach (var table in new[] { "conversations", "messages", "purge_work", "privacy_acknowledgements" })
            {
                // Identifiers come only from the fixed table allowlist above, never from SQL values or callers.
                using var identifiers = new NpgsqlCommandBuilder();
                var qualifiedTable = "andril_ai_isolated." + identifiers.QuoteIdentifier(table);
                var policySql = $"ALTER TABLE {qualifiedTable} ENABLE ROW LEVEL SECURITY; ALTER TABLE {qualifiedTable} FORCE ROW LEVEL SECURITY; CREATE POLICY owner_scope ON {qualifiedTable} USING(module='Learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(module_profile_id,'learning')) WITH CHECK(module='Learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(module_profile_id,'learning')); REVOKE ALL ON {qualifiedTable} FROM PUBLIC";
                await owner.Database.ExecuteSqlRawAsync(policySql);
            }
            // Own-data FK and role/operation constraints enforce isolation and idempotency.
            // No AI history or shared M2 schema/foreign-key/deletion change is introduced.
            var name = "andril_ai_runtime_" + Guid.NewGuid().ToString("N");
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await using var command = new NpgsqlCommand($"CREATE ROLE {name} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS", (NpgsqlConnection)owner.Database.GetDbConnection());
            await command.ExecuteNonQueryAsync(); f.role = name;
            command.CommandText = $"GRANT USAGE ON SCHEMA andril_ai_isolated TO {name}; GRANT SELECT,INSERT ON ALL TABLES IN SCHEMA andril_ai_isolated TO {name}; GRANT UPDATE(last_saved_user_message_at_utc,expires_at_utc,deleted_at_utc) ON andril_ai_isolated.conversations TO {name}; GRANT USAGE ON ALL SEQUENCES IN SCHEMA andril_ai_isolated TO {name}; GRANT USAGE ON SCHEMA andril_learning TO {name}; GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {name}";
            await command.ExecuteNonQueryAsync();
            // Acknowledgement runtime may change only its evidence fields, never ownership/generation or delete rows.
            using var roleIdentifiers = new NpgsqlCommandBuilder();
            command.CommandText = "GRANT UPDATE(version,policy_version,acknowledged,last_operation_id,updated_at_utc) ON andril_ai_isolated.privacy_acknowledgements TO " + roleIdentifiers.QuoteIdentifier(name);
            await command.ExecuteNonQueryAsync();
            f.connection = new NpgsqlConnectionStringBuilder(owner.Database.GetConnectionString()) { Username = name, Password = password, Pooling = false, IncludeErrorDetail = false }.ConnectionString;
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal IsolatedLearningAiConversationRepository Repository(DiaryPostgresFixture.Authority? authority = null,
        Func<string, CancellationToken, Task>? checkpoint = null, IAiPrivacyNoticeGate? privacy = null, ICurrentUser? currentUser = null)
    {
        var a = authority ?? Owner;
        return new(new DbContextOptionsBuilder<Andril.Persistence.DbContexts.LearningDbContext>().UseNpgsql(connection).Options,
            currentUser ?? a, a, a, new AiPrivacyNoticeGuard(new ActorReader(a, currentUser), privacy ?? new PrivacyGate(this)), Time) { TestCheckpoint = checkpoint };
    }
    internal IsolatedLearningAiDbContext OpenRuntime() => new(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>().UseNpgsql(connection).Options);
    internal IsolatedLearningAiAcknowledgementRepository Acknowledgements(IAiPrivacyPolicyVersionSource policies,
        DiaryPostgresFixture.Authority? authority = null, Func<string, CancellationToken, Task>? checkpoint = null)
    {
        var a = authority ?? Owner;
        return new(new DbContextOptionsBuilder<Andril.Persistence.DbContexts.LearningDbContext>().UseNpgsql(connection).Options,
            a, a, a, policies, Time) { TestCheckpoint = checkpoint };
    }
    internal async Task<IsolatedLearningAiDbContext> BoundOwner()
    {
        await using var owner = Shared.Shared.OpenOwner();
        var db = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>().UseNpgsql(owner.Database.GetConnectionString()).Options);
        try { await db.Database.OpenConnectionAsync(); await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Owner.Subject.ToString()},false),set_config('andril.account',{Owner.Account.ToString()},false),set_config('andril.module','learning',false)"); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (role is not null)
            {
                await using var owner = Shared.Shared.OpenOwner(); await owner.Database.OpenConnectionAsync();
                using var identifiers = new NpgsqlCommandBuilder();
                await using var revokeAcknowledgementColumns = new NpgsqlCommand(
                    "REVOKE UPDATE(version,policy_version,acknowledged,last_operation_id,updated_at_utc) ON andril_ai_isolated.privacy_acknowledgements FROM " + identifiers.QuoteIdentifier(role),
                    (NpgsqlConnection)owner.Database.GetDbConnection());
                await revokeAcknowledgementColumns.ExecuteNonQueryAsync();
                await using var command = new NpgsqlCommand($"REVOKE UPDATE(last_saved_user_message_at_utc,expires_at_utc,deleted_at_utc) ON andril_ai_isolated.conversations FROM {role}; REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA andril_ai_isolated FROM {role}; REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA andril_ai_isolated FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_ai_isolated FROM {role}; REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL PRIVILEGES ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}", (NpgsqlConnection)owner.Database.GetDbConnection());
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { connection = null; await Shared.DisposeAsync(); }
    }
}
