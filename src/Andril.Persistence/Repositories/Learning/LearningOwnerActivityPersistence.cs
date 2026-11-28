using System.Data;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Owner=Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Explicit private Growth self-report/mentorship-offer storage. No peer, publication or Circle grants.</summary>
public static class LearningOwnerActivityPersistence
{
    public static ILearningActivitiesRepository Create(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,string role)
    {
        var authority=new OwnerPurposeScope(accounts,user);
        var boundary=new OwnerPurposeBoundary(new LearningOwnerDocumentTransaction(accounts,user,profiles,role,LearningPrivateDocument.OwnerActivities),authority);
        return new IsolatedLearningActivitiesRepository(boundary,authority,connection=>new LearningPrivateActivitiesDbContext(
            new DbContextOptionsBuilder<LearningPrivateActivitiesDbContext>().UseNpgsql(connection).Options));
    }
    private sealed class OwnerPurposeScope(AccountsDbContext accounts,ICurrentUser user):ILearningActivityAuthority
    {
        private sealed record Lease(Owner Owner,LearningActivityMember Actor,IDbContextTransaction Transaction);
        private Lease? lease;
        internal void Enter(Owner owner,Guid account)
        {
            if(lease is not null||account==Guid.Empty||accounts.Database.CurrentTransaction is not {} transaction)
                throw new ProfileAccessDeniedException();
            lease=new(owner,new(account,owner.Profile),transaction);
        }
        internal void Clear()=>lease=null;
        public Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember actor,LearningActivityKind kind,LearningActivityAction action,
            Guid? resourceId,CreateLearningActivityRequest? creation,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(lease is not {} current||!user.IsAuthenticated||user.UserId!=current.Owner.Subject||actor!=current.Actor||
                !ReferenceEquals(accounts.Database.CurrentTransaction,current.Transaction)||current.Transaction.GetDbTransaction().IsolationLevel!=IsolationLevel.Serializable)
                return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Denied,[]));
            if(kind is not (LearningActivityKind.GrowthSelfReport or LearningActivityKind.MentorshipOffer)||
                action is not (LearningActivityAction.Create or LearningActivityAction.Read or LearningActivityAction.List))
                return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Unavailable,[]));
            if(creation?.TargetProfiles is {Length:>0})return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Denied,[]));
            return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Allowed,[current.Actor]));
        }
    }
    private sealed class OwnerPurposeBoundary(ILearningDocumentBoundary inner,OwnerPurposeScope scope):ILearningDocumentBoundary
    {
        public async Task<T> Run<T>(Func<LearningDbContext,Owner,CancellationToken,Task<T>> action,Func<LearningDocumentFailure,T> failure,
            CancellationToken ct,bool write=false,Func<string,CancellationToken,Task>? checkpoint=null,Func<Exception,bool>? additionalConflict=null)
        {
            bool entered=false;
            try
            {
                return await inner.Run(async(db,owner,token)=>
                {
                    var account=await db.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                    if(!Guid.TryParse(account,out var id))throw new ProfileAccessDeniedException();
                    scope.Enter(owner,id);entered=true;return await action(db,owner,token);
                },failure,ct,write,checkpoint,additionalConflict);
            }
            finally{if(entered)scope.Clear();}
        }
    }
}
