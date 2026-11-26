using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiDispatchOperationPostgresTests
{
    private sealed class Policies : IAiPrivacyPolicyVersionSource
    {
        internal string? Version = "synthetic-policy";
        public Task<string?> ReadCurrentVersionAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Version); }
    }
    private static async Task<AiAcknowledgementRevision> Grant(LearningAiConversationPostgresFixture f, Policies p)
    {
        var result = await f.Acknowledgements(p).AcknowledgeAsync(f.Actor(), p.Version!, Guid.NewGuid(), null);
        Assert.Equal(AiAcknowledgementOutcome.Saved, result.Outcome); return result.State!.Revision;
    }

    [LocalPostgresFact]
    public async Task PreparationAndClaimRoundTripWithoutProviderDispatchOrPrivateContentStorage()
    {
        await using var f = await LearningAiConversationPostgresFixture.Create(); var p = new Policies(); var revision = await Grant(f,p); var a = f.Actor(); var op = Guid.NewGuid(); var r = f.DispatchOperations(p);
        Assert.Equal(AiDispatchJournalOutcome.Saved,(await r.PrepareAsync(a,op,revision)).Outcome);
        var claimed=await r.ClaimAsync(a,op); Assert.Equal(AiDispatchJournalOutcome.Saved,claimed.Outcome); Assert.Equal(AiDispatchOperationState.Claimed,claimed.Operation!.State);
        Assert.Equal(claimed.Operation,(await f.DispatchOperations(p).ReconcileAsync(a,op)).Operation);
        Assert.Equal(AiDispatchJournalOutcome.Available,(await r.ValidateForReleaseAsync(a,op)).Outcome);
        await using var db=await f.BoundOwner();var entity=db.Model.FindEntityType(typeof(IsolatedAiDispatchOperationRow))!;
        Assert.DoesNotContain(entity.GetProperties(),x=>x.Name.Contains("Prompt")||x.Name.Contains("Response")||x.Name.Contains("Credential"));
    }

    [LocalPostgresFact]
    public async Task MissingAcknowledgementCannotPrepare()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await f.DispatchOperations(p).PrepareAsync(f.Actor(),Guid.NewGuid(),new(Guid.NewGuid(),1))).Outcome);
    }

    [LocalPostgresFact]
    public async Task ObsoletePolicyAndUnavailablePolicyDoNotPrepare()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var r=f.DispatchOperations(p);var a=f.Actor();
        p.Version="new-policy";Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.PrepareAsync(a,Guid.NewGuid(),rev)).Outcome);
        p.Version=null;Assert.Equal(AiDispatchJournalOutcome.Unavailable,(await r.PrepareAsync(a,Guid.NewGuid(),rev)).Outcome);
    }

    [LocalPostgresFact]
    public async Task RepeatedPreparationDoesNotPermitRepeatedClaims()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var r=f.DispatchOperations(p);var a=f.Actor();var op=Guid.NewGuid();
        var first=await r.PrepareAsync(a,op,rev);Assert.Equal(first,await r.PrepareAsync(a,op,rev));
        Assert.Equal(AiDispatchJournalOutcome.Saved,(await r.ClaimAsync(a,op)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.ClaimAsync(a,op)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.PrepareAsync(a,op,rev)).Outcome);
    }

    [LocalPostgresFact]
    public async Task ConcurrentIndependentConnectionsProduceOnlyOneClaim()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();
        await f.DispatchOperations(p).PrepareAsync(a,op,rev);
        var results=await Task.WhenAll(f.DispatchOperations(p).ClaimAsync(a,op),f.DispatchOperations(p).ClaimAsync(a,op));
        Assert.Single(results,x=>x.Outcome==AiDispatchJournalOutcome.Saved);Assert.Single(results,x=>x.Outcome==AiDispatchJournalOutcome.Conflict);
    }

    [LocalPostgresFact]
    public async Task RevocationTransactionOrderedBeforeClaimDeniesWaitingIndependentConnection()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();await f.DispatchOperations(p).PrepareAsync(a,op,rev);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoke=f.Acknowledgements(p,checkpoint:async(phase,ct)=>{if(phase=="prepared"){entered.TrySetResult();await release.Task.WaitAsync(ct);}}).RevokeAsync(a,Guid.NewGuid(),rev);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var claim=f.DispatchOperations(p).ClaimAsync(a,op);release.TrySetResult();
            Assert.Equal(AiAcknowledgementOutcome.Saved,(await revoke).Outcome);
            Assert.Equal(AiDispatchJournalOutcome.Denied,(await claim).Outcome);
        }
        finally{release.TrySetResult();await revoke;}
    }

    [LocalPostgresFact]
    public async Task ClaimBeforeRevocationDoesNotAuthorizeLaterResultRelease()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();await f.DispatchOperations(p).PrepareAsync(a,op,rev);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim=f.DispatchOperations(p,checkpoint:async(phase,ct)=>{if(phase=="prepared"){entered.TrySetResult();await release.Task.WaitAsync(ct);}}).ClaimAsync(a,op);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var revoke=f.Acknowledgements(p).RevokeAsync(a,Guid.NewGuid(),rev);release.TrySetResult();
            Assert.Equal(AiDispatchJournalOutcome.Saved,(await claim).Outcome);Assert.Equal(AiAcknowledgementOutcome.Saved,(await revoke).Outcome);
            Assert.Equal(AiDispatchJournalOutcome.Denied,(await f.DispatchOperations(p).ValidateForReleaseAsync(a,op)).Outcome);
        }
        finally{release.TrySetResult();await claim;}
    }

    [LocalPostgresFact]
    public async Task ReacknowledgementCannotRevivePreparedOperationAfterRevocationAba()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var r=f.DispatchOperations(p);var a=f.Actor();var op=Guid.NewGuid();await r.PrepareAsync(a,op,rev);
        var revoked=await f.Acknowledgements(p).RevokeAsync(a,Guid.NewGuid(),rev);
        await f.Acknowledgements(p).AcknowledgeAsync(a,p.Version!,Guid.NewGuid(),revoked.State!.Revision);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.ClaimAsync(a,op)).Outcome);
    }

    [LocalPostgresFact]
    public async Task CancellationIsTerminalAndDoesNotRequirePositivePrivacyEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var r=f.DispatchOperations(p);var a=f.Actor();var op=Guid.NewGuid();await r.PrepareAsync(a,op,rev);
        await f.Acknowledgements(p).RevokeAsync(a,Guid.NewGuid(),rev);
        var cancel=await r.CancelAsync(a,op);Assert.Equal(AiDispatchJournalOutcome.Saved,cancel.Outcome);Assert.Equal(AiDispatchOperationState.Cancelled,cancel.Operation!.State);
        Assert.Equal(AiDispatchOperationState.Cancelled,(await r.ReconcileAsync(a,op)).Operation!.State);
    }

    [LocalPostgresFact]
    public async Task IndeterminateStateIsDurableAndNeverAutomaticallyReplayed()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var r=f.DispatchOperations(p);var a=f.Actor();var op=Guid.NewGuid();await r.PrepareAsync(a,op,rev);await r.ClaimAsync(a,op);
        Assert.Equal(AiDispatchJournalOutcome.Saved,(await r.MarkIndeterminateAsync(a,op)).Outcome);
        Assert.Equal(AiDispatchOperationState.Indeterminate,(await f.DispatchOperations(p).ReconcileAsync(a,op)).Operation!.State);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.ClaimAsync(a,op)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.ValidateForReleaseAsync(a,op)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.CancelAsync(a,op)).Outcome);
    }

    [LocalPostgresFact]
    public async Task ForeignIdentityAndModuleCannotReadOrMutateOperation()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();var r=f.DispatchOperations(p);await r.PrepareAsync(a,op,rev);
        foreach(var foreign in new[]{new AiAuthorizedActor(Guid.NewGuid(),a.ModuleProfileId,AiModule.Learning),new AiAuthorizedActor(a.UserId,Guid.NewGuid(),AiModule.Learning),new AiAuthorizedActor(a.UserId,a.ModuleProfileId,AiModule.Hobbies)})
        {Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.ClaimAsync(foreign,op)).Outcome);Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.ReconcileAsync(foreign,op)).Outcome);}
    }

    [LocalPostgresFact]
    public async Task AuthorityRevokedBeforeCommitRollsBackJournalPreparation()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();
        var r=f.DispatchOperations(p,checkpoint:(phase,_)=>{if(phase=="prepared")f.Owner.Denied=true;return Task.CompletedTask;});
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.PrepareAsync(a,op,rev)).Outcome);f.Owner.Denied=false;
        Assert.Null((await f.DispatchOperations(p).ReconcileAsync(a,op)).Operation);
        var unrelatedOperation = Guid.NewGuid();
        var unrelated = f.DispatchOperations(p, checkpoint: (phase, _) =>
        {
            if (phase == "prepared") throw new UnauthorizedAccessException("synthetic checkpoint failure");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unrelated.PrepareAsync(a, unrelatedOperation, rev));
        Assert.Null((await f.DispatchOperations(p).ReconcileAsync(a, unrelatedOperation)).Operation);
    }

    [LocalPostgresFact]
    public async Task CancellationAfterPreparationRollsBackWithoutDeletingAcknowledgement()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);var a=f.Actor();var op=Guid.NewGuid();using var cts=new CancellationTokenSource();
        var r=f.DispatchOperations(p,checkpoint:(phase,ct)=>{if(phase=="prepared")cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>r.PrepareAsync(a,op,rev,cts.Token));
        Assert.Null((await f.DispatchOperations(p).ReconcileAsync(a,op)).Operation);Assert.Equal(rev,(await f.Acknowledgements(p).ReadAsync(a)).State!.Revision);
    }

    [LocalPostgresFact]
    public async Task RuntimeCannotRewriteOperationBindingOrDeleteJournalAndForceRlsIsPreserved()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var rev=await Grant(f,p);await f.DispatchOperations(p).PrepareAsync(f.Actor(),Guid.NewGuid(),rev);
        await using var owner=await f.BoundOwner();var flags=await owner.Database.SqlQueryRaw<string>("SELECT relrowsecurity::text||'|'||relforcerowsecurity::text AS \"Value\" FROM pg_class WHERE oid='andril_ai_isolated.dispatch_operations'::regclass").SingleAsync();Assert.Equal("true|true",flags);
        await using var runtime=f.OpenRuntime();var privileges=await runtime.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai_isolated.dispatch_operations','DELETE')::text||'|'||has_column_privilege(current_user,'andril_ai_isolated.dispatch_operations','account_id','UPDATE')::text||'|'||has_column_privilege(current_user,'andril_ai_isolated.dispatch_operations','acknowledgement_version','UPDATE')::text AS \"Value\"").SingleAsync();Assert.Equal("false|false|false",privileges);
        Assert.Empty(await runtime.Set<IsolatedAiDispatchOperationRow>().ToListAsync());
    }
}
