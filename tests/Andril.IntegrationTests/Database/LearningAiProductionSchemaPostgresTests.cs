using Andril.AI.ChatBox;
using Andril.AI.Persistence;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiProductionSchemaPostgresTests
{
    [Fact]
    public void RuntimePermissionIdentifiersCannotInjectSqlOrSelectPrivilegedRolesByDefault()
    {
        foreach(var role in new[]{"x; DROP SCHEMA public", "x' OR true --", "", "PUBLIC", new string('x',64)})
            Assert.Throws<ArgumentException>(()=>LearningAiRuntimePermissions.GrantSql(role));
        var sql=LearningAiRuntimePermissions.GrantSql("andril_ai_test_runtime");
        Assert.DoesNotContain("GRANT DELETE",sql);Assert.DoesNotContain("BYPASSRLS TO",sql);Assert.Contains("NOT rolbypassrls",sql);
    }
    private static LearningAiDbContext Open(string connection)
        => new(new DbContextOptionsBuilder<LearningAiDbContext>().UseNpgsql(connection,
            postgres=>postgres.MigrationsHistoryTable(LearningAiDbContext.HistoryTable,LearningAiDbContext.Schema)).Options);
    private static async Task Bind(LearningAiDbContext db,LearningAiConversationPostgresFixture f)
    {
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{f.Owner.Subject.ToString()},false),set_config('andril.account',{f.Actor().UserId.ToString()},false),set_config('andril.module','learning',false)");
    }

    [LocalPostgresFact]
    public async Task FreshProductionCoreMigrationAppliesAndUsesDedicatedHistoryWithoutFixtureDiscoveryTables()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();await using var db=Open(source.Database.GetConnectionString()!);
        await db.Database.MigrateAsync();Assert.Equal(5,db.Model.GetEntityTypes().Count());Assert.All(db.Model.GetEntityTypes(),x=>Assert.Equal("andril_ai",x.GetSchema()));
        Assert.Equal("20261127090000_LearningAiCore",Assert.Single(await db.Database.GetAppliedMigrationsAsync()));
        var tables=await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='andril_ai'").ToListAsync();Assert.Equal(6,tables.Count);
        Assert.Contains("__EFMigrationsHistory_AI",tables);Assert.DoesNotContain("classroom_discovery",tables);Assert.DoesNotContain("learning_partner_discovery",tables);
        Assert.Empty(source.Database.GetMigrations());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('andril_ai_isolated.\"__EFMigrationsHistory_AI\"') IS NULL AS \"Value\"").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task EveryProductionCoreTableHasForceRlsAndPublicHasNoDataPrivileges()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();await using var db=Open(source.Database.GetConnectionString()!);await db.Database.MigrateAsync();
        var flags=await db.Database.SqlQueryRaw<string>("SELECT c.relname||'|'||c.relrowsecurity::text||'|'||c.relforcerowsecurity::text AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='andril_ai' AND c.relkind='r' AND c.relname <> '__EFMigrationsHistory_AI'").ToListAsync();
        Assert.Equal(5,flags.Count);Assert.All(flags,x=>Assert.EndsWith("|true|true",x));
        var grants=await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.role_table_grants WHERE table_schema='andril_ai' AND grantee='PUBLIC'").ToListAsync();Assert.Empty(grants);
    }

    [LocalPostgresFact]
    public async Task RollbackAndReapplyPreserveOtherSchemasAndSeparateHistory()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var actor=f.Actor();var conversation=await f.Repository().CreateWithUserMessageAsync(actor,Guid.NewGuid(),"isolated existing data");
        await using var source=await f.BoundOwner();await using var db=Open(source.Database.GetConnectionString()!);await db.Database.MigrateAsync();
        await db.GetService<IMigrator>().MigrateAsync("0");Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        Assert.NotNull(await f.Repository().FindAsync(actor,conversation.ConversationId));
        await db.Database.MigrateAsync();Assert.Single(await db.Database.GetAppliedMigrationsAsync());Assert.NotNull(await f.Repository().FindAsync(actor,conversation.ConversationId));
    }

    [LocalPostgresFact]
    public async Task ProductionCoreRoundTripsMessagesAndPreservesExpiryUnderOwnerContext()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();await using var db=Open(source.Database.GetConnectionString()!);await db.Database.MigrateAsync();await Bind(db,f);
        var actor=f.Actor();var op=Guid.NewGuid();var conversation=AiConversationEntity.CreateForFirstUserMessage(actor,op,f.Time.Now);
        var message=AiMessageEntity.CreateUserMessage(conversation,actor,op,"private synthetic content",f.Time.Now);db.Add(conversation);db.Add(message);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var stored=await db.Set<AiConversationEntity>().SingleAsync();Assert.Equal(f.Time.Now.AddDays(14),stored.ExpiresAtUtc);
        var saved=await db.Set<AiMessageEntity>().SingleAsync();Assert.Equal(op,saved.OperationId);Assert.Equal("private synthetic content",saved.Text);
    }

    [LocalPostgresFact]
    public async Task RestrictedRoleCannotAccessProductionBeforeExplicitRoleProvisioning()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();await using var owner=Open(source.Database.GetConnectionString()!);await owner.Database.MigrateAsync();
        await using var runtime=f.OpenRuntime();await using var restricted=Open(runtime.Database.GetConnectionString()!);
        var allowed=await restricted.Database.SqlQueryRaw<bool>("SELECT has_schema_privilege(current_user,'andril_ai','USAGE') AS \"Value\"").SingleAsync();Assert.False(allowed);
        var error=await Assert.ThrowsAsync<PostgresException>(()=>restricted.Database.ExecuteSqlRawAsync("SELECT 1 FROM andril_ai.conversations"));Assert.Equal("42501",error.SqlState);
    }

    [LocalPostgresFact]
    public async Task FrozenMigrationDoesNotMutateExistingIsolatedModelSchema()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();var before=source.Database.GenerateCreateScript();
        await using var db=Open(source.Database.GetConnectionString()!);await db.Database.MigrateAsync();Assert.Equal(before,source.Database.GenerateCreateScript());
        Assert.Equal(7,source.Model.GetEntityTypes().Count());
        var otherHistory=await db.Database.SqlQueryRaw<string>("SELECT schemaname||'.'||tablename AS \"Value\" FROM pg_tables WHERE tablename LIKE '__EFMigrationsHistory%' AND schemaname <> 'andril_ai'").ToListAsync();Assert.NotEmpty(otherHistory);
    }

    [LocalPostgresFact]
    public async Task ExplicitRestrictedRoleProvisioningAllowsScopedInsertsButNoDeleteOrOwnershipRewrite()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await using var source=await f.BoundOwner();await using var owner=Open(source.Database.GetConnectionString()!);await owner.Database.MigrateAsync();
        await using var existingRuntime=f.OpenRuntime();var role=await existingRuntime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        try
        {
            await owner.Database.ExecuteSqlRawAsync(LearningAiRuntimePermissions.GrantSql(role));
            await using var runtime=Open(existingRuntime.Database.GetConnectionString()!);await Bind(runtime,f);
            var actor=f.Actor();var op=Guid.NewGuid();var conversation=AiConversationEntity.CreateForFirstUserMessage(actor,op,f.Time.Now);
            runtime.Add(conversation);runtime.Add(AiMessageEntity.CreateUserMessage(conversation,actor,op,"restricted private input",f.Time.Now));await runtime.SaveChangesAsync();
            var permissions=await runtime.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai.messages','DELETE')::text||'|'||has_table_privilege(current_user,'andril_ai.messages','UPDATE')::text||'|'||has_column_privilege(current_user,'andril_ai.conversations','account_id','UPDATE')::text AS \"Value\"").SingleAsync();Assert.Equal("false|false|false",permissions);
            await runtime.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{Guid.NewGuid().ToString()},false)");
            Assert.Empty(await runtime.Set<AiConversationEntity>().AsNoTracking().ToListAsync());
        }
        finally { await owner.Database.ExecuteSqlRawAsync(LearningAiRuntimePermissions.RevokeSql(role)); }
    }
}
