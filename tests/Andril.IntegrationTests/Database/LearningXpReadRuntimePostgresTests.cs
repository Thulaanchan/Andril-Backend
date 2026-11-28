using System.Net;
using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Basis=Andril.IntegrationTests.Database.LearningPrivateCvRuntimePostgresTests.Fixture;

namespace Andril.IntegrationTests.Database;

public sealed class LearningXpReadRuntimePostgresTests
{
    private sealed class Fixture(Basis basis):IAsyncDisposable
    {
        internal SharedGroupsDatabase Groups=>basis.Groups;
        internal string Role{get;}="xp_reader_"+Guid.NewGuid().ToString("N");
        private bool roleCreated;
        internal LearningXpReadDbContext Open()=>new(new DbContextOptionsBuilder<LearningXpReadDbContext>().UseNpgsql(Groups.Database.ConnectionString).Options);
        internal static async Task<Fixture> Create()
        {
            var f=new Fixture(await Basis.Create());
            try
            {
                await using var db=f.Open();await db.Database.MigrateAsync();using var quote=new NpgsqlCommandBuilder();var name=quote.QuoteIdentifier(f.Role);
                var sql=$"CREATE ROLE {name} NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE; GRANT {name} TO CURRENT_USER";
                await db.Database.ExecuteSqlRawAsync(sql);f.roleCreated=true;await db.Database.ExecuteSqlRawAsync(LearningXpReadRuntimePermissions.GrantSql(f.Role));return f;
            }
            catch{await f.DisposeAsync();throw;}
        }
        internal ILearningXpRepository Store(AccountsDbContext db,SharedGroupsDatabase.Actor actor,Func<CancellationToken,Task>? checkpoint=null)
        {
            var profiles=new PrivateProfileService(actor,actor,new AccountRepository(db),new PrivateProfileRepository(db),new LearningPrivateCvRuntimePostgresTests.Policies(),TimeProvider.System);
            return checkpoint is null?LearningXpReadPersistence.Create(db,actor,profiles,Role):LearningXpReadPersistence.Create(db,actor,profiles,Role,checkpoint);
        }
        internal async Task<Guid> Seed(SharedGroupsDatabase.Actor actor,long amount=7)
        {
            // Synthetic fixture-only credit; no production event, amount or policy is approved.
            var id=Guid.NewGuid();await using var db=Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.xp_credits(id,account_id,profile_id,module,source_kind,source_event_id,amount,entry_kind,policy_revision,evidence_revision,credited_at_utc) VALUES({id},{actor.Account},{actor.Profile},'learning','SYNTHETIC-ONLY',{Guid.NewGuid()},{amount},'Credit','SYNTHETIC-ONLY','SYNTHETIC-ONLY',clock_timestamp())");await tx.CommitAsync();return id;
        }
        public async ValueTask DisposeAsync()
        {
            try{await basis.DisposeAsync();}
            finally{if(roleCreated){await using var db=await SocialLifecycleDatabase.OpenMaintenanceAsync();using var quote=new NpgsqlCommandBuilder();await using var command=new NpgsqlCommand("DROP ROLE "+quote.QuoteIdentifier(Role),db);await command.ExecuteNonQueryAsync();}}
        }
    }
    private static async Task Revoke(Fixture f,SharedGroupsDatabase.Actor actor)
    {
        await using var db=f.Groups.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");await tx.CommitAsync();
    }
    [LocalPostgresFact]
    public async Task CurrentOwnerReadsEmptyAndSeededLedgerWithoutInventingProgressPolicies()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Groups.Database.OpenAccounts();var store=f.Store(db,actor);
        var empty=await store.ReadAsync();Assert.Equal(LearningXpOutcome.Available,empty.Outcome);Assert.Equal(0L,empty.Total);Assert.Empty(empty.Entries!);
        var ids=new[]{await f.Seed(actor),await f.Seed(actor,11)};var read=await store.ReadAsync();Assert.Equal(LearningXpOutcome.Available,read.Outcome);Assert.Equal(18L,read.Total);
        Assert.Equal(ids.Order(),read.Entries!.Select(x=>x.Id));Assert.All(read.Entries!,x=>Assert.NotEqual(default,x.CreditedAtUtc));
        Assert.NotNull(read.Progress);Assert.Null(read.Progress.Level);Assert.Empty(read.Progress.Achievements);Assert.Null(read.Progress.PolicyRevision);
        var first=await store.ReadAsync(limit:1);var second=await store.ReadAsync(first.Entries![0].Id,1);Assert.Single(second.Entries!);Assert.NotEqual(first.Entries[0].Id,second.Entries![0].Id);Assert.Equal(18L,second.Total);
        Assert.Equal(LearningXpOutcome.Conflict,(await store.ReadAsync(Guid.Empty)).Outcome);
    }
    [LocalPostgresFact]
    public async Task ForeignWrongModuleInactiveMinorAndRevokedPoliciesNeverDiscloseOwnerHistory()
    {
        await using var f=await Fixture.Create();var owner=await f.Groups.SeedAsync("learning");await f.Seed(owner);
        var other=await f.Groups.SeedAsync("learning");await using var foreign=f.Groups.Database.OpenAccounts();var empty=await f.Store(foreign,other).ReadAsync();Assert.Equal(LearningXpOutcome.Available,empty.Outcome);Assert.Equal(0L,empty.Total);Assert.Empty(empty.Entries!);
        foreach(var actor in new[]{await f.Groups.SeedAsync("hobbies"),await f.Groups.SeedAsync("learning",active:false),await f.Groups.SeedAsync("learning",minor:true)})
        {await using var db=f.Groups.Database.OpenAccounts();var denied=await f.Store(db,actor).ReadAsync();Assert.Equal(LearningXpOutcome.Denied,denied.Outcome);Assert.Null(denied.Entries);Assert.Null(denied.Total);}
        await using var accounts=f.Groups.Database.OpenAccounts();await accounts.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM andril_identity.account_policy_records WHERE account_id={owner.Account} AND action='privacy_acknowledged'");
        Assert.Equal(LearningXpOutcome.Denied,(await f.Store(accounts,owner).ReadAsync()).Outcome);
    }
    [LocalPostgresFact]
    public async Task RuntimeIsSelectOnlyForceRlsAndCannotReadIdentityOrPublishRewards()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();var original=await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        var store=f.Store(db,actor);Assert.Equal(LearningXpOutcome.Available,(await store.ReadAsync()).Outcome);Assert.Equal(original,await db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync());
        Assert.Equal(LearningXpOutcome.Unavailable,(await store.CreditAsync(new("unapproved",Guid.NewGuid()))).Outcome);Assert.Equal(LearningXpOutcome.Unavailable,(await store.AdjustAsync(null!)).Outcome);Assert.Equal(LearningXpOutcome.Unavailable,(await store.StageApprovedPolicyAsync("unapproved")).Outcome);
        await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true)");
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT row_security_active('andril_learning.xp_credits') AND (SELECT relforcerowsecurity FROM pg_class WHERE oid='andril_learning.xp_credits'::regclass) AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning.xp_credits','INSERT,UPDATE,DELETE') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_schema_privilege(current_user,'andril_identity','USAGE') AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,c.oid,'SELECT') AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='andril_identity' AND c.relname='accounts'").SingleAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{Guid.NewGuid().ToString()},true)");Assert.Equal(0L,await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.xp_credits").SingleAsync());await tx.RollbackAsync();
    }
    [LocalPostgresFact]
    public async Task CancellationAndUnavailableReadRollbackAndReleaseTheSubjectFence()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();using var cancel=new CancellationTokenSource();bool reached=false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Store(db,actor,ct=>{reached=true;cancel.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;}).ReadAsync(cancellationToken:cancel.Token));Assert.True(reached);
        var failed=await f.Store(db,actor,_=>Task.FromException(new IOException("synthetic-private-failure"))).ReadAsync();Assert.Equal(LearningXpOutcome.Unavailable,failed.Outcome);Assert.Null(failed.Total);Assert.Null(failed.Entries);
        await using var independent=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));Assert.Equal(7L,(await f.Store(independent,actor).ReadAsync(cancellationToken:deadline.Token)).Total);
    }
    [LocalPostgresFact]
    public async Task IndependentRevocationOrdersBeforeLaterReadsAndPreservesTheLedger()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await f.Seed(actor);await using var db=f.Groups.Database.OpenAccounts();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=f.Store(db,actor,async ct=>{entered.SetResult();await release.Task.WaitAsync(ct);}).ReadAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var revoking=Revoke(f,actor);
        try
        {
            await using var observer=f.Groups.Database.OpenAccounts();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE andril_identity.account_module_lifecycles%') AS \"Value\"").SingleAsync(deadline.Token))await Task.Delay(20,deadline.Token);
            Assert.False(revoking.IsCompleted);
        }
        finally{release.TrySetResult();}
        Assert.Equal(LearningXpOutcome.Available,(await reading).Outcome);await revoking.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(LearningXpOutcome.Denied,(await f.Store(db,actor).ReadAsync()).Outcome);
        await using var tx=await db.Database.BeginTransactionAsync();await Basis.Bind(db,actor);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='active',revision=revision+1,state_changed_at_utc=clock_timestamp() WHERE account_id={actor.Account} AND module='learning'");Assert.Equal(1L,await db.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_learning.xp_credits").SingleAsync());await tx.RollbackAsync();
    }
    [LocalPostgresFact]
    public async Task MigrationRoundTripPreservesExistingIdentityAndLearningDataAndMatchesSnapshot()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await using var db=f.Open();Assert.False(db.Database.HasPendingModelChanges());Assert.Single(db.Model.GetEntityTypes());
        await db.GetService<IMigrator>().MigrateAsync("0");await db.Database.MigrateAsync();
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT has_table_privilege({f.Role},'andril_learning.xp_credits','SELECT') AS \"Value\"").SingleAsync());
        // A recreated table has a new ACL. Repeat the deployment-time SELECT-only
        // grant, never add identity/schema access or runtime migration privileges.
        await db.Database.ExecuteSqlRawAsync(LearningXpReadRuntimePermissions.GrantSql(f.Role));
        await using var accounts=f.Groups.Database.OpenAccounts();Assert.Equal(LearningXpOutcome.Available,(await f.Store(accounts,actor).ReadAsync()).Outcome);
        Assert.Equal(1L,await accounts.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM andril_identity.accounts WHERE id={actor.Account}").SingleAsync());
    }
    [LocalPostgresFact]
    public async Task ControlledHttpIsDefaultOffConfirmedOwnerOnlyNoStoreAndReadOnly()
    {
        await using var f=await Fixture.Create();var actor=await f.Groups.SeedAsync("learning");await f.Seed(actor);const string route="/api/learning/xp";
        using(var disabled=new SharedGroupsHttpHost(f.Groups))using(var client=disabled.CreateClient(new(){BaseAddress=new Uri("https://localhost")})){client.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(route)).StatusCode);}
        using var host=new SharedGroupsHttpHost(f.Groups,learningXpReadRole:f.Role);using var http=host.CreateClient(new(){BaseAddress=new Uri("https://localhost")});Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync(route)).StatusCode);
        http.DefaultRequestHeaders.Authorization=new("Bearer","unconfirmed-"+actor.Subject);Assert.Equal(HttpStatusCode.Forbidden,(await http.GetAsync(route)).StatusCode);http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);
        using var read=await http.GetAsync(route);Assert.Equal(HttpStatusCode.OK,read.StatusCode);Assert.True(read.Headers.CacheControl!.NoStore);using var json=JsonDocument.Parse(await read.Content.ReadAsStringAsync());Assert.Equal(7L,json.RootElement.GetProperty("total").GetInt64());
        foreach(var query in new[]{"?after=private-invalid","?after=00000000-0000-0000-0000-000000000000","?limit=0","?limit=101","?limit=oops"})Assert.Equal(HttpStatusCode.BadRequest,(await http.GetAsync(route+query)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,(await http.PostAsync(route,null)).StatusCode);var other=await f.Groups.SeedAsync("learning");http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+other.Subject);using var foreign=JsonDocument.Parse(await http.GetStringAsync(route));Assert.Equal(0L,foreign.RootElement.GetProperty("total").GetInt64());
        http.DefaultRequestHeaders.Authorization=new("Bearer","confirmed-"+actor.Subject);await Revoke(f,actor);using var denied=await http.GetAsync(route);Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.DoesNotContain(actor.Profile.ToString(),await denied.Content.ReadAsStringAsync());
    }
}
