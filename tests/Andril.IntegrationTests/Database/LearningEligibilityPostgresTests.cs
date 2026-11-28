using System.Diagnostics;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningEligibilityPostgresTests
{
    private static readonly GroupModuleScope Scope=new("learning",GroupKind.Classroom);
    private static async Task<(SharedGroupsDatabase.Actor Caller,SharedGroupsDatabase.Actor Host,Guid Group)> Seed(SharedGroupsDatabase f)
    {
        var caller=await f.SeedAsync("learning");var host=await f.SeedAsync("learning");await f.GrantEducatorAsync(host);
        var group=await f.CreateGroupAsync(host,Scope,new("Eligibility Classroom",null,true,"public"));return(caller,host,group.Id);
    }
    private static Task<GroupDiscoverySnapshot?> Read(SharedGroupsDatabase f,SharedGroupsDatabase.Actor actor,
        Func<GroupDiscoverySnapshot,Task>? duringRead=null)
    {
        var query=new GroupDiscoveryQuery(actor.Profile,Scope,"Eligibility",10);
        return f.WithScopeAsync(actor,execution=>
        {
            var repository=new PostgresGroupReadRepository(execution);
            return repository.WithAuthorizedReadAsync<GroupDiscoverySnapshot>(query,async(snapshot,ct)=>
            {
                if(duringRead is not null)await duringRead(snapshot);
                return await repository.RevalidateAsync(query,snapshot,ct)==GroupDiscoveryDecision.Allowed?snapshot:null;
            });
        });
    }
    [LocalPostgresFact]
    public async Task RuntimeCannotInvokeThePrivateReaderReadScopesOrBroadenItsLifecycleAccess()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        Assert.Equal(s.Group,Assert.Single((await Read(f,s.Caller))!.Groups).Group.Id);
        var restricted=await f.WithScopeAsync(s.Caller,e=>e.ExecuteAsync("learning",async(db,_,ct)=>
            await db.Database.SqlQueryRaw<bool>("""
                SELECT (NOT rolsuper AND NOT rolbypassrls
                    AND NOT has_table_privilege(current_user,'andril_groups.learning_eligibility_read_scopes','SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
                    AND NOT has_function_privilege(current_user,'andril_groups.learning_group_target_eligible(uuid,uuid)','EXECUTE')
                    AND current_setting('andril.module')='learning'
                    AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                        WHERE n.nspname='andril_identity' AND c.relname='account_module_lifecycles')
                    AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                        WHERE n.nspname='andril_groups' AND c.relname='learning_eligibility_read_scopes')) AS "Value"
                FROM pg_roles WHERE rolname=current_user
                """).SingleAsync(ct),default));
        Assert.True(restricted);
        await using(var runtime=f.Open())
        await using(var tx=await runtime.Database.BeginTransactionAsync())
        {
            // Observe the actual SQLSTATE before the outer M2 repository deliberately
            // sanitizes database errors into AccountStoreUnavailableException.
            await runtime.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('role',{f.Role},true),set_config('andril.account',{s.Caller.Account.ToString()},true),set_config('andril.subject',{s.Caller.Subject.ToString()},true),set_config('andril.module','learning',true)");
            var denied=await Assert.ThrowsAsync<PostgresException>(()=>runtime.Database.SqlQuery<bool>($"SELECT andril_groups.learning_group_target_eligible({s.Caller.Profile},{s.Group}) AS \"Value\"").SingleAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,denied.SqlState);await tx.RollbackAsync();
        }
        await using var owner=f.Open();Assert.Equal(0,await owner.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.learning_eligibility_read_scopes").SingleAsync());
    }
    [LocalPostgresFact]
    public async Task ScopedReadLockPolicyCannotAuthorizeACrossOwnerLifecycleMutation()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        await using(var owner=f.Database.OpenAccounts())
        await using(var tx=await owner.Database.BeginTransactionAsync())
        {
            await owner.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{s.Caller.Account.ToString()},true),set_config('andril.subject',{s.Caller.Subject.ToString()},true),set_config('andril.module','learning',true)");
            await owner.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_groups.learning_eligibility_read_scopes
                    (id,backend_pid,transaction_id,actor_account,actor_subject,actor_profile,target_account,target_profile,group_id,purpose,module)
                VALUES (gen_random_uuid(),pg_backend_pid(),pg_current_xact_id()::text,{s.Caller.Account},{s.Caller.Subject},{s.Caller.Profile},
                    {s.Host.Account},{s.Host.Profile},{s.Group},'learning-group-interaction','learning')
                """);
            var denied=await Assert.ThrowsAsync<PostgresException>(()=>owner.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={s.Host.Account} AND module='learning'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,denied.SqlState);
            await tx.RollbackAsync();
        }
        Assert.Single((await Read(f,s.Caller))!.Groups);
    }
    [LocalPostgresFact]
    public async Task IndependentLifecycleRevocationCannotCommitBeforeAuthorizedReadRelease()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=Read(f,s.Caller,async snapshot=>{Assert.Single(snapshot.Groups);entered.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(20));});
        Task<int>? revoking=null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var writer=f.Database.OpenAccounts();await using var tx=await writer.Database.BeginTransactionAsync();
            writer.Database.SetCommandTimeout(20);
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{s.Host.Account.ToString()},true),set_config('andril.subject',{s.Host.Subject.ToString()},true),set_config('andril.module','learning',true)");
            var pid=await writer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            revoking=writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={s.Host.Account} AND module='learning'");
            await using var observer=f.Open();var timeout=Stopwatch.StartNew();var blocked=false;
            while(timeout.Elapsed<TimeSpan.FromSeconds(10))
            {
                blocked=await observer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid={pid} AND wait_event_type='Lock' AND state='active') AS \"Value\"").SingleAsync();
                if(blocked||revoking.IsCompleted)break;await Task.Delay(10);
            }
            Assert.True(blocked);Assert.False(revoking.IsCompleted);
            release.TrySetResult();Assert.Single((await reading.WaitAsync(TimeSpan.FromSeconds(20)))!.Groups);
            Assert.Equal(1,await revoking.WaitAsync(TimeSpan.FromSeconds(20)));await tx.CommitAsync();
            Assert.Empty((await Read(f,s.Caller))!.Groups);
        }
        finally{release.TrySetResult();await reading.WaitAsync(TimeSpan.FromSeconds(20));}
    }
    [LocalPostgresFact]
    public async Task RevocationWinningTheExistingFencePreventsAQueuedReadFromReleasingOldEligibility()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        await using var writer=f.Database.OpenAccounts();await using var tx=await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{s.Host.Account.ToString()},true),set_config('andril.subject',{s.Host.Subject.ToString()},true),set_config('andril.module','learning',true)");
        Assert.Equal(1,await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive',revision=revision+1 WHERE account_id={s.Host.Account} AND module='learning'"));
        var queued=Read(f,s.Caller);
        await using var observer=f.Open();var timeout=Stopwatch.StartNew();var blocked=false;
        while(timeout.Elapsed<TimeSpan.FromSeconds(10))
        {
            blocked=await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock' AND state='active') AS \"Value\"").SingleAsync();
            if(blocked||queued.IsCompleted)break;await Task.Delay(10);
        }
        Assert.True(blocked);Assert.False(queued.IsCompleted);
        await tx.CommitAsync();Assert.Empty((await queued.WaitAsync(TimeSpan.FromSeconds(20)))!.Groups);
        Assert.Empty((await Read(f,s.Caller))!.Groups);
        await using var owner=f.Open();Assert.Equal(0,await owner.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM andril_groups.learning_eligibility_read_scopes").SingleAsync());
    }
    [LocalPostgresFact]
    public async Task SamePrivateReaderSupportsAuthorizedHostAdmissionWithoutTreatingApplicantAsTheCaller()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        await f.WithScopeAsync(s.Caller,e=>new PostgresGroupMembershipTransactionRepository(e)
            .SubmitJoinRequestAtomicAsync(s.Group,Scope,s.Caller.Profile,DateTime.UtcNow));
        await f.WithScopeAsync(s.Host,e=>new PostgresGroupMembershipTransactionRepository(e)
            .ProcessAdmissionAtomicAsync(s.Group,Scope,s.Caller.Profile,true,s.Host.Profile,DateTime.UtcNow));
        await using var db=f.Open();
        Assert.Equal(1,await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM andril_groups.memberships WHERE group_id={s.Group} AND module='learning' AND profile_id={s.Caller.Profile} AND departed_at_utc IS NULL").SingleAsync());
        Assert.Equal("approved",await db.Database.SqlQuery<string>($"SELECT state AS \"Value\" FROM andril_groups.join_requests WHERE group_id={s.Group} AND module='learning' AND profile_id={s.Caller.Profile}").SingleAsync());
        Assert.Single((await Read(f,s.Caller))!.Groups);
    }
    [LocalPostgresFact]
    public async Task ProductionReadinessRejectsMissingPurposePolicyWithoutDisclosingConnectionDetails()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;
        await using(var db=f.Open())await db.Database.ExecuteSqlRawAsync("DROP POLICY learning_group_eligibility_scope ON andril_identity.account_module_lifecycles");
        using var host=new SharedGroupsHttpHost(f,learningClassrooms:true);
        var failure=Assert.Throws<InvalidOperationException>(()=>host.CreateClient());
        Assert.Contains("Learning eligibility authority is unavailable",failure.Message);
        Assert.DoesNotContain("Password",failure.Message,StringComparison.OrdinalIgnoreCase);
    }
    [LocalPostgresFact]
    public async Task MigrationRollbackAndReapplicationPreserveExistingGroupAndProfileData()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var s=await Seed(f);
        Assert.Single((await Read(f,s.Caller))!.Groups);
        await using var db=f.Open();var migrator=db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261128015044_UnifiedGroupLimits");
        Assert.Equal(1,await f.CountAsync("groups"));
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT FROM pg_policy WHERE polrelid='andril_identity.account_module_lifecycles'::regclass AND polname='learning_group_eligibility_scope') AS \"Value\"").SingleAsync());
        await migrator.MigrateAsync();Assert.Single((await Read(f,s.Caller))!.Groups);
        Assert.Equal(s.Group,Assert.Single((await Read(f,s.Caller))!.Groups).Group.Id);
    }
}
