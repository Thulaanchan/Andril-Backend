using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Learning.Discovery;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andril.Persistence.Repositories.Learning;

public static class LearningPublicationPersistence
{
    private static ILearningDocumentBoundary Boundary(AccountsDbContext db,ICurrentUser user,IPrivateProfileService profiles,string role)=>
        LearningPeerActivityPersistence.WithAuthorityFence(db,new LearningOwnerDocumentTransaction(db,user,profiles,role,LearningPrivateDocument.Publications));
    public static ILearningPublicationRepository Create(AccountsDbContext db,ICurrentUser user,IPrivateProfileService profiles,string role)=>new Publications(Boundary(db,user,profiles,role));
    public static LearningPeopleDiscoveryService CreatePeople(AccountsDbContext db,ICurrentUser user,IPrivateProfileService profiles,IPolicyCatalogProvider policies,string role)
        =>CreatePeople(db,user,profiles,policies,role,null);
    internal static LearningPeopleDiscoveryService CreatePeople(AccountsDbContext db,ICurrentUser user,IPrivateProfileService profiles,IPolicyCatalogProvider policies,string role,Func<string,CancellationToken,Task>? checkpoint)
    {
        var gate=new PeopleGate(db,user,policies,Boundary(db,user,profiles,role)){Checkpoint=checkpoint};
        return new(new LearningSetupAccessBoundary(user,gate),gate);
    }
    internal sealed record OwnerFacts(long LifecycleRevision,string? DisplayName,bool StandingAllows);
    private static async Task<OwnerFacts?> Facts(LearningDbContext db,Guid profile,CancellationToken ct)
    {
        var safe=await db.Database.SqlQueryRaw<bool>("SELECT NOT EXISTS(SELECT FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='andril_learning' AND p.proname IN ('publication_owned','publication_owner_facts','published_target_account','read_disclosed_people') AND (r.rolsuper OR r.rolbypassrls)) AS \"Value\"").SingleAsync(ct);
        if(!safe)throw new ProfileContractPendingException();
        return await db.Database.SqlQuery<OwnerFacts>($"SELECT lifecycle_revision AS \"LifecycleRevision\",display_name AS \"DisplayName\",standing_allows AS \"StandingAllows\" FROM andril_learning.publication_owner_facts({profile})").SingleOrDefaultAsync(ct);
    }
    private sealed class Publications(ILearningDocumentBoundary boundary):ILearningPublicationRepository
    {
        internal Func<string,CancellationToken,Task>? Checkpoint;
        public Task<LearningPublicationResult> ReadAsync(LearningPublicationKind kind,Guid? offerId=null,CancellationToken cancellationToken=default)=>Run(kind,offerId,null,null,null,cancellationToken);
        public Task<LearningPublicationResult> SetDisclosureAsync(LearningDisclosureChange change,CancellationToken cancellationToken=default)=>Run(LearningPublicationKind.People,null,change.Visible,change.DisplayNameDisclosure,change.Expected,cancellationToken);
        public Task<LearningPublicationResult> SetOfferAsync(Guid offerId,LearningOfferPublicationChange change,CancellationToken cancellationToken=default)=>Run(LearningPublicationKind.MentorshipOffer,offerId,change.Published,false,change.Expected,cancellationToken);
        private Task<LearningPublicationResult> Run(LearningPublicationKind kind,Guid? offer,bool? publish,bool? name,LearningActivityRevision? expected,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(!Enum.IsDefined(kind)||kind==LearningPublicationKind.People&&offer is not null||kind==LearningPublicationKind.MentorshipOffer&&(offer is null||offer==Guid.Empty))return Task.FromResult(new LearningPublicationResult(LearningPublicationOutcome.Invalid));
            return boundary.Run(async(parent,owner,token)=>
            {
                var resource=offer??owner.Profile;
                var owned=await parent.Database.SqlQuery<bool>($"SELECT andril_learning.publication_owned({(int)kind},{resource},{owner.Profile}) AS \"Value\"").SingleAsync(token);
                if(!owned)return new LearningPublicationResult(LearningPublicationOutcome.NotFound);
                var facts=await Facts(parent,owner.Profile,token);if(facts is null)return new LearningPublicationResult(LearningPublicationOutcome.Denied);
                await using var db=new LearningPublicationsDbContext(new DbContextOptionsBuilder<LearningPublicationsDbContext>().UseNpgsql(parent.Database.GetDbConnection()).Options);await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(),token);
                var row=await db.Set<LearningPublicationRow>().SingleOrDefaultAsync(x=>x.ProfileId==owner.Profile&&x.Kind==(int)kind&&x.ResourceId==resource,token);
                static LearningPublicationView View(LearningPublicationKind kind,Guid resource,LearningPublicationRow? row)=>new(kind,resource,row?.Published??false,row?.NameDisclosure??false,row is null?null:new(row.Generation,row.Version));
                if(publish is null)return new LearningPublicationResult(LearningPublicationOutcome.Available,View(kind,resource,row));
                if(publish.Value&&!facts.StandingAllows)return new LearningPublicationResult(LearningPublicationOutcome.Denied);
                if(name==true&&string.IsNullOrWhiteSpace(facts.DisplayName))return new LearningPublicationResult(LearningPublicationOutcome.Invalid);
                if(row is null&&expected is not null||row is not null&&(expected is null||expected.Generation!=row.Generation||expected.Version!=row.Version))return new LearningPublicationResult(LearningPublicationOutcome.Conflict);
                if(row is null)
                {
                    var account=await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                    row=new(){AccountId=Guid.Parse(account),ProfileId=owner.Profile,Kind=(int)kind,ResourceId=resource,Generation=Guid.NewGuid(),Version=1};db.Add(row);
                }
                else row.Version=checked(row.Version+1);
                row.Published=publish.Value;row.NameDisclosure=name==true;row.LifecycleRevision=facts.LifecycleRevision;row.ReviewedName=kind==LearningPublicationKind.People?facts.DisplayName:null;
                await db.SaveChangesAsync(token);return new LearningPublicationResult(LearningPublicationOutcome.Saved,View(kind,resource,row));
            },failure=>new LearningPublicationResult(failure switch{LearningDocumentFailure.Denied=>LearningPublicationOutcome.Denied,LearningDocumentFailure.Conflict=>LearningPublicationOutcome.Conflict,LearningDocumentFailure.CommitUnknown=>LearningPublicationOutcome.CommitUnknown,_=>LearningPublicationOutcome.Unavailable}),ct,publish is not null,Checkpoint);
        }
    }
    internal static void SetCheckpoint(ILearningPublicationRepository repository,Func<string,CancellationToken,Task> callback)=>((Publications)repository).Checkpoint=callback;
    private sealed class PeopleGate(AccountsDbContext accounts,ICurrentUser user,IPolicyCatalogProvider policies,ILearningDocumentBoundary boundary):ILearningSetupAuthorityReader,ILearningPeopleDiscoveryReader
    {
        internal Func<string,CancellationToken,Task>? Checkpoint;
        private LearningSetupAccessEvidence? lease;private IDbContextTransaction? transaction;
        private async Task<LearningSetupAccessEvidence?> Evidence(LearningDbContext db,Guid subject,Guid profile,CancellationToken ct)
        {
            var facts=await Facts(db,profile,ct);if(facts is null)return null;var account=Guid.Parse(await db.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct));var revision=Guid.NewGuid();
            // Real M2 callback established session, ownership, age, membership, current
            // Terms/Privacy and lifecycle. The nonce identifies only this read lease.
            return new(subject,account,profile,account,"learning",LearningSetupOperation.ReadSetup,true,true,facts.StandingAllows,true,true,false,false,revision,revision);
        }
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid verifiedSubject,LearningSetupOperation operation,CancellationToken cancellationToken=default)
        {
            var ct=cancellationToken;ct.ThrowIfCancellationRequested();if(operation!=LearningSetupOperation.ReadSetup||!user.IsAuthenticated||user.UserId!=verifiedSubject)return Task.FromResult<LearningSetupAccessEvidence?>(null);
            if(lease is not null&&ReferenceEquals(accounts.Database.CurrentTransaction,transaction))return Task.FromResult<LearningSetupAccessEvidence?>(lease);
            return boundary.Run((db,owner,token)=>Evidence(db,owner.Subject,owner.Profile,token),failure=>failure==LearningDocumentFailure.Denied
                ?new LearningSetupAccessEvidence(verifiedSubject,Guid.Empty,Guid.Empty,Guid.Empty,"learning",operation,false,false,false,false,false,true,false,Guid.Empty,Guid.Empty):null,ct);
        }
        private sealed record Person(Guid ProfileId,string DisplayName);
        public async Task<T?> WithAuthorizedSearchAsync<T>(LearningSetupScope scope,string query,int limit,Func<LearningPeopleSnapshot,CancellationToken,Task<T>> project,CancellationToken cancellationToken=default)where T:class
        {
            var ct=cancellationToken;ct.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(query)||query.Length>500||query.Any(char.IsControl)||limit is <1 or >100)return null;
            try
            {
                return await boundary.Run(async(db,owner,token)=>
                {
                    lease=await Evidence(db,owner.Subject,owner.Profile,token);transaction=accounts.Database.CurrentTransaction;
                    if(lease is null||!lease.StandingAllows||lease.AccountId!=scope.AccountId||lease.LearningProfileId!=scope.LearningProfileId)throw new ProfileAccessDeniedException();
                    var current=policies.GetCurrent();var rows=await db.Database.SqlQuery<Person>($"SELECT profile_id AS \"ProfileId\",display_name AS \"DisplayName\" FROM andril_learning.read_disclosed_people({owner.Profile},{query},{limit},{current.Terms.Version},{current.Privacy.Version})").ToArrayAsync(token);
                    return await project(new(scope.AccountId,scope.LearningProfileId,lease.ObservedVersion.ToString("N"),rows.Select(x=>new LearningPeopleEvidence(x.ProfileId,"learning",x.DisplayName,LearningDiscoveryLifecycle.Active,LearningDiscoveryDecision.Allowed,LearningDiscoveryDecision.Allowed,LearningDiscoveryDecision.Allowed,LearningDiscoveryDecision.Allowed)).ToArray()),token);
                },_ => (T?)null,ct,checkpoint:Checkpoint);
            }
            finally{lease=null;transaction=null;}
        }
    }
}
