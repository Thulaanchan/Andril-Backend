using System.Data;
using System.Text.Json;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Owner=Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>P11 explicit peer intent only. No discovery disclosure, publication, communication or Circle grants.</summary>
public static class LearningPeerActivityPersistence
{
    public static ILearningActivitiesRepository Create(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,IPolicyCatalogProvider policies,string role)
    {
        var scope=new PeerScope(accounts,user,policies);
        var boundary=new PeerBoundary(accounts,new LearningOwnerDocumentTransaction(accounts,user,profiles,role,LearningPrivateDocument.PeerActivities),scope);
        return new IsolatedLearningActivitiesRepository(boundary,scope,c=>new LearningPeerActivitiesDbContext(new DbContextOptionsBuilder<LearningPeerActivitiesDbContext>().UseNpgsql(c).Options));
    }
    private sealed class PeerScope(AccountsDbContext accounts,ICurrentUser user,IPolicyCatalogProvider policies):ILearningActivityAuthority
    {
        private Owner? owner;private LearningActivityMember? actor;private IDbContextTransaction? transaction;
        internal async Task Enter(Owner current,CancellationToken ct)
        {
            if(owner is not null||accounts.Database.CurrentTransaction is not {} tx||tx.GetDbTransaction().IsolationLevel!=IsolationLevel.Serializable)throw new ProfileAccessDeniedException();
            var account=await accounts.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
            if(!Guid.TryParse(account,out var id))throw new ProfileAccessDeniedException();owner=current;actor=new(id,current.Profile);transaction=tx;
        }
        internal void Clear(){owner=null;actor=null;transaction=null;}
        public async Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember requester,LearningActivityKind kind,LearningActivityAction action,
            Guid? resourceId,CreateLearningActivityRequest? creation,CancellationToken cancellationToken=default)
        {
            var ct=cancellationToken;ct.ThrowIfCancellationRequested();
            if(owner is null||requester!=actor||!user.IsAuthenticated||user.UserId!=owner.Subject||!ReferenceEquals(transaction,accounts.Database.CurrentTransaction))return new(LearningActivityPermissionOutcome.Denied,[]);
            if(kind is not (LearningActivityKind.SkillExchange or LearningActivityKind.Pact or LearningActivityKind.Connection))return new(LearningActivityPermissionOutcome.Unavailable,[]);
            var current=policies.GetCurrent();
            await accounts.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.peer_terms',{current.Terms.Version},true),set_config('andril.peer_privacy',{current.Privacy.Version},true)",ct);
            var safe=await accounts.Database.SqlQueryRaw<bool>("SELECT NOT EXISTS(SELECT FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner WHERE p.oid IN ('andril_learning.peer_account(uuid,uuid,integer,text,text)'::regprocedure,'andril_learning.peer_row_visible(jsonb,integer)'::regprocedure) AND (r.rolsuper OR r.rolbypassrls)) AS \"Value\"").SingleAsync(ct);
            if(!safe)return new(LearningActivityPermissionOutcome.Unavailable,[]);
            Guid[] targets;
            if(creation is not null)
            {
                targets=creation.TargetProfiles??[];
                if(targets.Length==0||(kind!=LearningActivityKind.Pact&&targets.Length!=1)||targets.Any(x=>x==Guid.Empty||x==requester.ProfileId)||targets.Distinct().Count()!=targets.Length)return new(LearningActivityPermissionOutcome.Denied,[]);
            }
            else if(resourceId is {} id)
            {
                await using var db=new LearningPeerActivitiesDbContext(new DbContextOptionsBuilder<LearningPeerActivitiesDbContext>().UseNpgsql(accounts.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(transaction!.GetDbTransaction(),ct);
                var row=await db.Set<LearningActivityRow>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.Kind==kind,ct);
                if(row is null)return new(LearningActivityPermissionOutcome.Allowed,[requester]); // Opaque NotFound; no foreign/blocked row is released.
                var members=JsonSerializer.Deserialize<LearningActivityMember[]>(row.Members);
                if(members is null||members.Length<2||!members.Contains(requester))return new(LearningActivityPermissionOutcome.Denied,[]);
                targets=members.Where(x=>x!=requester).Select(x=>x.ProfileId).ToArray();
            }
            else if(action==LearningActivityAction.List)return new(LearningActivityPermissionOutcome.Allowed,[requester]);
            else return new(LearningActivityPermissionOutcome.Denied,[]);
            var participants=new List<LearningActivityMember>{requester};
            foreach(var target in targets.Order())
            {
                var targetAccount=await accounts.Database.SqlQuery<Guid?>($"SELECT andril_learning.peer_account({requester.ProfileId},{target},{(int)kind},{current.Terms.Version},{current.Privacy.Version}) AS \"Value\"").SingleAsync(ct);
                if(targetAccount is not {} other)return new(LearningActivityPermissionOutcome.Denied,[]);
                participants.Add(new(other,target));
            }
            var serialized=JsonSerializer.Serialize(participants);
            var visible=await accounts.Database.SqlQuery<bool>($"SELECT andril_learning.peer_row_visible({serialized}::jsonb,{(int)kind}) AS \"Value\"").SingleAsync(ct);
            return visible?new(LearningActivityPermissionOutcome.Allowed,participants.AsReadOnly()):new(LearningActivityPermissionOutcome.Denied,[]);
        }
    }
    private sealed class PeerBoundary(AccountsDbContext accounts,ILearningDocumentBoundary inner,PeerScope scope):ILearningDocumentBoundary
    {
        public async Task<T> Run<T>(Func<LearningDbContext,Owner,CancellationToken,Task<T>> action,Func<LearningDocumentFailure,T> failure,CancellationToken ct,
            bool write=false,Func<string,CancellationToken,Task>? checkpoint=null,Func<Exception,bool>? additionalConflict=null)
        {
            var connection=(NpgsqlConnection)accounts.Database.GetDbConnection();bool opened=connection.State!=ConnectionState.Open,fenced=false;string? timeout=null;
            try
            {
                if(opened)await accounts.Database.OpenConnectionAsync(ct);
                // Same M5 key as block/restriction writers, before the serializable snapshot.
                await accounts.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('andril.groups.authority',0))",ct);fenced=true;
                timeout=await accounts.Database.SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync(ct);
                // Never wait in reverse order behind an M2/M5 writer holding an account row.
                await accounts.Database.ExecuteSqlRawAsync("SELECT set_config('lock_timeout','250ms',false)",ct);
                return await inner.Run(async(db,owner,token)=>{await scope.Enter(owner,token);return await action(db,owner,token);},failure,ct,write,checkpoint,additionalConflict);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(NpgsqlException){return failure(LearningDocumentFailure.Unavailable);}
            finally
            {
                scope.Clear();
                if(connection.State==ConnectionState.Open)
                {
                    try
                    {
                        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        if(timeout is not null)await accounts.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('lock_timeout',{timeout},false)",cleanup.Token);
                        if(fenced)await accounts.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtextextended('andril.groups.authority',0))",cleanup.Token);
                    }
                    catch{NpgsqlConnection.ClearPool(connection);await connection.CloseAsync();}
                }
                if(opened)await accounts.Database.CloseConnectionAsync();
            }
        }
    }
}
