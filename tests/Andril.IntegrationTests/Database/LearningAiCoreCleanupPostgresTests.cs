using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiCoreCleanupPostgresTests
{
    private sealed class Authority(AiCoreCleanupScope scope) : IAiCoreCleanupAuthority
    {
        public bool WorkerAllowed = true;
        public bool RetentionAllowed;
        public int Calls;
        public Func<int, CancellationToken, Task>? BeforeDecision;
        public Task<T> WithWorkerScopeAsync<T>(Func<AiCoreCleanupScope, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WorkerAllowed) throw new UnauthorizedAccessException("AI maintenance is not authorized.");
            return action(scope, cancellationToken);
        }
        public async Task<bool> MayRemoveCoreAsync(AiConversationCleanupCandidate candidate, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (BeforeDecision is not null) await BeforeDecision(Calls, cancellationToken);
            return RetentionAllowed;
        }
    }

    private static DbContextOptions<LearningAiDbContext> Options(string connection)
        => new DbContextOptionsBuilder<LearningAiDbContext>().UseNpgsql(connection,
            p => p.MigrationsHistoryTable(LearningAiDbContext.HistoryTable, LearningAiDbContext.Schema)).Options;

    private sealed class Harness(LearningAiConversationPostgresFixture fixture, LearningAiDbContext db,
        DbContextOptions<LearningAiDbContext> options, Authority authority) : IAsyncDisposable
    {
        public LearningAiConversationPostgresFixture Fixture { get; } = fixture;
        public LearningAiDbContext Db { get; } = db;
        public Authority Authority { get; } = authority;
        public LearningAiCoreCleanupStore Store { get; } = new(options, authority, fixture.Time);

        public static async Task<Harness> Create()
        {
            var f = await LearningAiConversationPostgresFixture.Create();
            try
            {
                await using var source = await f.BoundOwner();
                var options = Options(source.Database.GetConnectionString()!);
                var db = new LearningAiDbContext(options);
                try
                {
                    await db.Database.MigrateAsync();
                    await db.Database.OpenConnectionAsync();
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Owner.Subject.ToString()},false),set_config('andril.account',{f.Actor().UserId.ToString()},false),set_config('andril.module','learning',false)");
                    var authority = new Authority(new(f.Owner.Subject, f.Actor().UserId, f.Actor().ModuleProfileId));
                    return new(f, db, options, authority);
                }
                catch { await db.DisposeAsync(); throw; }
            }
            catch { await f.DisposeAsync(); throw; }
        }

        public async Task<AiConversationCleanupCandidate> Seed(bool deleted = true, bool expired = false)
        {
            var now = Fixture.Time.Now;
            var created = expired ? now.AddDays(-15) : now.AddHours(-1);
            var actor = Fixture.Actor();
            var op = Guid.NewGuid();
            var c = AiConversationEntity.CreateForFirstUserMessage(actor, op, created);
            var message = AiMessageEntity.CreateUserMessage(c, actor, op, "synthetic private content", created);
            if (deleted) c.MarkDeleted(now);
            Db.Add(c); Db.Add(message);
            var revoked = c.DeletedAtUtc is { } d && d < c.ExpiresAtUtc ? d : c.ExpiresAtUtc;
            if (deleted) Db.Add(new IsolatedAiPurgeWork { ConversationId = c.Id, AccountId = c.AccountId,
                ModuleProfileId = c.ModuleProfileId, Module = c.Module, RequestedAtUtc = now, PurgeByUtc = now.AddHours(24) });
            await Db.SaveChangesAsync(); Db.ChangeTracker.Clear();
            return new(c.Id, c.AccountId, c.ModuleProfileId, c.Module, revoked, revoked.AddHours(24));
        }

        public async Task AssertPresent(AiConversationCleanupCandidate candidate)
        {
            Assert.True(await Db.Set<AiConversationEntity>().AnyAsync(x => x.Id == candidate.ConversationId));
            Assert.True(await Db.Set<AiMessageEntity>().AnyAsync(x => x.ConversationId == candidate.ConversationId));
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Fixture.DisposeAsync(); }
    }

    [Fact]
    public void MaintenanceGrantIsNarrowAndRejectsUntrustedIdentifiers()
    {
        foreach (var role in new[] { "x;drop schema public", "x'", "PUBLIC", "", new string('x', 64) })
            Assert.Throws<ArgumentException>(() => LearningAiCleanupPermissions.GrantSql(role));
        var sql = LearningAiCleanupPermissions.GrantSql("andril_ai_maintenance_test");
        Assert.Contains("NOT rolbypassrls", sql);
        Assert.DoesNotContain("GRANT UPDATE", sql);
        Assert.DoesNotContain("privacy_acknowledgements", sql);
        Assert.DoesNotContain("dispatch_operations", sql);
        Assert.DoesNotContain("SELECT(text)", sql);
        Assert.DoesNotContain("BYPASSRLS TO", sql);
    }

    [LocalPostgresFact]
    public async Task VerifiedRemovalDeletesOnlyExactCoreAndIsIdempotentBeforeDeadline()
    {
        await using var h = await Harness.Create();
        var candidate = await h.Seed(); var unrelated = await h.Seed();
        h.Authority.RetentionAllowed = true;
        Assert.True(candidate.PurgeTargetUtc > h.Fixture.Time.Now);
        Assert.Equal(AiCoreCleanupOutcome.CoreRemoved, await h.Store.TryRemoveAsync(candidate));
        Assert.False(await h.Db.Set<AiConversationEntity>().AnyAsync(x => x.Id == candidate.ConversationId));
        Assert.False(await h.Db.Set<AiMessageEntity>().AnyAsync(x => x.ConversationId == candidate.ConversationId));
        Assert.False(await h.Db.Set<IsolatedAiPurgeWork>().AnyAsync(x => x.ConversationId == candidate.ConversationId));
        await h.AssertPresent(unrelated);
        Assert.Equal(AiCoreCleanupOutcome.NotEligibleOrMissing, await h.Store.TryRemoveAsync(candidate));
        Assert.Equal(2, h.Authority.Calls);
    }

    [LocalPostgresFact]
    public async Task ExpiredConversationUsesExistingFourteenDayExpiryAndTwentyFourHourTarget()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed(deleted: false, expired: true);
        h.Authority.RetentionAllowed = true;
        Assert.Equal(h.Fixture.Time.Now.AddDays(-1), candidate.RevokedAtUtc);
        Assert.Equal(h.Fixture.Time.Now, candidate.PurgeTargetUtc);
        Assert.Equal(AiCoreCleanupOutcome.CoreRemoved, await h.Store.TryRemoveAsync(candidate));
    }

    [LocalPostgresFact]
    public async Task MissingRetentionAndWorkerEvidenceNeverDeleteOrDiscloseContent()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        Assert.Equal(AiCoreCleanupOutcome.Deferred, await h.Store.TryRemoveAsync(candidate));
        h.Authority.WorkerAllowed = false;
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Store.TryRemoveAsync(candidate));
        Assert.Equal("AI maintenance is not authorized.", ex.Message);
        await h.AssertPresent(candidate);
    }

    [LocalPostgresFact]
    public async Task HoldIntroducedBeforeCommitRollsBackEveryCoreDeletion()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        h.Authority.RetentionAllowed = true;
        h.Authority.BeforeDecision = (call, _) => { if (call == 2) h.Authority.RetentionAllowed = false; return Task.CompletedTask; };
        Assert.Equal(AiCoreCleanupOutcome.Deferred, await h.Store.TryRemoveAsync(candidate));
        await h.AssertPresent(candidate);
        Assert.True(await h.Db.Set<IsolatedAiPurgeWork>().AnyAsync(x => x.ConversationId == candidate.ConversationId));
    }

    [LocalPostgresFact]
    public async Task CancellationBeforeCommitRollsBackEveryCoreDeletion()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        using var cancel = new CancellationTokenSource(); h.Authority.RetentionAllowed = true;
        h.Authority.BeforeDecision = (call, _) => { if (call == 2) cancel.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.TryRemoveAsync(candidate, cancel.Token));
        await h.AssertPresent(candidate);
    }

    [LocalPostgresFact]
    public async Task ActiveForeignModuleForeignAccountAndStaleMetadataNeverAuthorizeRemoval()
    {
        await using var h = await Harness.Create(); var active = await h.Seed(deleted: false); var candidate = await h.Seed();
        h.Authority.RetentionAllowed = true;
        foreach (var rejected in new[] { active, candidate with { Module = AiModule.Hobbies },
            candidate with { AccountId = Guid.NewGuid() }, candidate with { ModuleProfileId = Guid.NewGuid() },
            candidate with { PurgeTargetUtc = candidate.PurgeTargetUtc.AddSeconds(1) } })
            Assert.Equal(AiCoreCleanupOutcome.NotEligibleOrMissing, await h.Store.TryRemoveAsync(rejected));
        Assert.Equal(0, h.Authority.Calls); await h.AssertPresent(active); await h.AssertPresent(candidate);
    }

    [LocalPostgresFact]
    public async Task IndependentConnectionsCannotCommitDuplicateCoreRemoval()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        h.Authority.RetentionAllowed = true;
        var results = await Task.WhenAll(h.Store.TryRemoveAsync(candidate), h.Store.TryRemoveAsync(candidate));
        Assert.Equal(1, results.Count(x => x == AiCoreCleanupOutcome.CoreRemoved));
        Assert.Equal(1, results.Count(x => x == AiCoreCleanupOutcome.NotEligibleOrMissing));
        Assert.False(await h.Db.Set<AiMessageEntity>().AnyAsync(x => x.ConversationId == candidate.ConversationId));
    }

    [LocalPostgresFact]
    public async Task OrdinaryRuntimeRemainsUnableToDeleteCoreContent()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        await using var existing = h.Fixture.OpenRuntime();
        var role = await existing.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        try
        {
            await h.Db.Database.ExecuteSqlRawAsync(LearningAiRuntimePermissions.GrantSql(role));
            var store = new LearningAiCoreCleanupStore(Options(existing.Database.GetConnectionString()!), h.Authority, h.Fixture.Time);
            h.Authority.RetentionAllowed = true;
            Assert.Equal(AiCoreCleanupOutcome.Unavailable, await store.TryRemoveAsync(candidate));
            Assert.Equal(0, h.Authority.Calls); await h.AssertPresent(candidate);
        }
        finally { await h.Db.Database.ExecuteSqlRawAsync(LearningAiRuntimePermissions.RevokeSql(role)); }
    }

    [LocalPostgresFact]
    public async Task SeparateRestrictedMaintenanceRoleHasNoPayloadReadAndRlsStillControlsRemoval()
    {
        await using var h = await Harness.Create(); var candidate = await h.Seed();
        h.Authority.RetentionAllowed = true;
        var roleName = "andril_ai_cleanup_test_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder(); var role = builder.QuoteIdentifier(roleName);
        await using var admin = new NpgsqlConnection(h.Db.Database.GetConnectionString()); await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; GRANT {role} TO CURRENT_USER WITH SET TRUE, INHERIT FALSE", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            await h.Db.Database.ExecuteSqlRawAsync(LearningAiCleanupPermissions.GrantSql(roleName));
            await using (var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA andril_learning TO {role}; GRANT EXECUTE ON FUNCTION andril_learning.lock_cv_owner(uuid,uuid), andril_learning.scope_visible(uuid,text) TO {role}", admin)) await grant.ExecuteNonQueryAsync();
            await using var restricted = new NpgsqlConnection(h.Db.Database.GetConnectionString()); await restricted.OpenAsync();
            await using (var setRole = new NpgsqlCommand($"SET ROLE {role}", restricted)) await setRole.ExecuteNonQueryAsync();
            var options = new DbContextOptionsBuilder<LearningAiDbContext>().UseNpgsql(restricted).Options;
            await using (var check = new LearningAiDbContext(options))
            {
                Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT has_column_privilege(current_user,'andril_ai.messages','text','SELECT') AS \"Value\"").SingleAsync());
                Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT rolbypassrls OR rolsuper OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
                await check.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{h.Fixture.Owner.Subject.ToString()},false),set_config('andril.account',{Guid.NewGuid().ToString()},false),set_config('andril.module','learning',false)");
                Assert.Empty(await check.Set<AiConversationEntity>().AsNoTracking().ToListAsync());
                Assert.Equal(0, await check.Set<AiConversationEntity>().Where(x => x.Id == candidate.ConversationId).ExecuteDeleteAsync());
            }
            var store = new LearningAiCoreCleanupStore(options, h.Authority, h.Fixture.Time);
            Assert.Equal(AiCoreCleanupOutcome.CoreRemoved, await store.TryRemoveAsync(candidate));
        }
        finally
        {
            await h.Db.Database.ExecuteSqlRawAsync(LearningAiCleanupPermissions.RevokeSql(roleName));
            await using var drop = new NpgsqlCommand($"REVOKE ALL ON ALL FUNCTIONS IN SCHEMA andril_learning FROM {role}; REVOKE ALL ON SCHEMA andril_learning FROM {role}; DROP ROLE {role}", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
