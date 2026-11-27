using System.Net;
using System.Net.Http.Json;
using Andril.AI.Interfaces;
using Andril.Persistence.Repositories.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiAtomicResultPostgresTests
{
    private sealed class Policies : IAiPrivacyPolicyVersionSource
    {
        internal string? Version="synthetic-policy";
        public Task<string?> ReadCurrentVersionAsync(AiAuthorizedActor actor,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(Version);}
    }
    private static async Task<(Guid Operation,Guid Conversation,AiAcknowledgementRevision Revision)> Prepare(
        LearningAiConversationPostgresFixture f,Policies p)
    {
        var a=f.Actor();var acknowledgement=f.Acknowledgements(p);var existing=await acknowledgement.ReadAsync(a);
        var grant=await acknowledgement.AcknowledgeAsync(a,p.Version!,Guid.NewGuid(),existing.State?.Revision);Assert.Equal(AiAcknowledgementOutcome.Saved,grant.Outcome);
        var op=Guid.NewGuid();var c=await f.Repository().CreateWithUserMessageAsync(a,op,"synthetic user input");
        var journal=f.DispatchOperations(p);Assert.Equal(AiDispatchJournalOutcome.Saved,(await journal.PrepareAsync(a,op,grant.State!.Revision)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Saved,(await journal.ClaimAsync(a,op)).Outcome);return(op,c.ConversationId,grant.State.Revision);
    }

    [LocalPostgresFact]
    public async Task AssistantMessageAndCompletedJournalCommitTogetherWithoutRenewingExpiry()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var a=f.Actor();var before=await f.Repository().FindAsync(a,v.Conversation);
        var result=await f.DispatchOperations(p).CompleteConversationResultAsync(a,v.Operation,v.Conversation,"private synthetic reply");
        Assert.Equal(AiDispatchJournalOutcome.Saved,result.Outcome);Assert.Equal(AiDispatchOperationState.Completed,result.Operation!.State);
        Assert.DoesNotContain("reply",result.ToString());Assert.Equal(before,await f.Repository().FindAsync(a,v.Conversation));
        await using var db=await f.BoundOwner();var messages=await db.Set<Andril.AI.Persistence.AiMessageEntity>().Where(x=>x.OperationId==v.Operation).ToListAsync();
        Assert.Equal(2,messages.Count);Assert.Equal("private synthetic reply",Assert.Single(messages,x=>x.Role==AiStoredMessageRole.Assistant).Text);
        Assert.Equal(AiDispatchOperationState.Completed,(await db.Set<IsolatedAiDispatchOperationRow>().SingleAsync()).State);
    }

    [LocalPostgresFact]
    public async Task CompletedOperationCannotReplayOrBecomeCancelledOrIndeterminate()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var r=f.DispatchOperations(p);var a=f.Actor();
        await r.CompleteConversationResultAsync(a,v.Operation,v.Conversation,"once");
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.CompleteConversationResultAsync(a,v.Operation,v.Conversation,"once")).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.CancelAsync(a,v.Operation)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.MarkIndeterminateAsync(a,v.Operation)).Outcome);
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await r.ClaimAsync(a,v.Operation)).Outcome);
        Assert.Equal(AiDispatchOperationState.Completed,(await r.ReconcileAsync(a,v.Operation)).Operation!.State);
        Assert.Equal(2,(await f.Repository().ReadRecentMessagesAsync(a,v.Conversation,100)).Count);
    }

    [LocalPostgresFact]
    public async Task SameOperationConcurrentCompletionUsingIndependentConnectionsCommitsOneReply()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var a=f.Actor();
        var results=await Task.WhenAll(f.DispatchOperations(p).CompleteConversationResultAsync(a,v.Operation,v.Conversation,"first"),f.DispatchOperations(p).CompleteConversationResultAsync(a,v.Operation,v.Conversation,"second"));
        Assert.Single(results,x=>x.Outcome==AiDispatchJournalOutcome.Saved);Assert.Single(results,x=>x.Outcome==AiDispatchJournalOutcome.Conflict);
        Assert.Equal(2,(await f.Repository().ReadRecentMessagesAsync(a,v.Conversation,100)).Count);
    }

    [LocalPostgresFact]
    public async Task FailureAfterDatabaseSaveRollsBackReplyAndCompletedStateTogether()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var a=f.Actor();
        var r=f.DispatchOperations(p,checkpoint:(phase,_)=>{if(phase=="prepared")throw new IOException("PRIVATE_FAILURE");return Task.CompletedTask;});
        var result=await r.CompleteConversationResultAsync(a,v.Operation,v.Conversation,"PRIVATE_REPLY");Assert.Equal(AiDispatchJournalOutcome.Unavailable,result.Outcome);Assert.Null(result.Operation);
        Assert.DoesNotContain("PRIVATE",result.ToString());await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task CancellationAfterSaveRollsBackBothWritesAndPropagates()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);using var cts=new CancellationTokenSource();
        var r=f.DispatchOperations(p,checkpoint:(phase,ct)=>{if(phase=="prepared")cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>r.CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"reply",cts.Token));
        await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task LifecycleRevocationAtCommitDeniesAndRollsBackBothWrites()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);
        var r=f.DispatchOperations(p,checkpoint:(phase,_)=>{if(phase=="prepared")f.Owner.Denied=true;return Task.CompletedTask;});
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"PRIVATE_REPLY")).Outcome);
        f.Owner.Denied=false;await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task AcknowledgementRevokeBeforeCompletionPreventsAnyResultPersistence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);
        await f.Acknowledgements(p).RevokeAsync(f.Actor(),Guid.NewGuid(),v.Revision);
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await f.DispatchOperations(p).CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"reply")).Outcome);
        await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task PolicyChangeAtCommitRollsBackCompletedStateAndReply()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);
        var r=f.DispatchOperations(p,checkpoint:(phase,_)=>{if(phase=="prepared")p.Version="next-policy";return Task.CompletedTask;});
        Assert.Equal(AiDispatchJournalOutcome.Unavailable,(await r.CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"reply")).Outcome);
        await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task ConversationWithoutMatchingAcceptedUserOperationIsNotModified()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var a=f.Actor();
        var other=await f.Repository().CreateWithUserMessageAsync(a,Guid.NewGuid(),"different user operation");
        Assert.Equal(AiDispatchJournalOutcome.Conflict,(await f.DispatchOperations(p).CompleteConversationResultAsync(a,v.Operation,other.ConversationId,"reply")).Outcome);
        Assert.Single(await f.Repository().ReadRecentMessagesAsync(a,other.ConversationId,100));await AssertClaimedWithOnlyUser(f,p,v.Operation,v.Conversation);
    }

    [LocalPostgresFact]
    public async Task ExpiredDeletedAndForeignConversationBindingsAreDenied()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var a=f.Actor();var r=f.DispatchOperations(p);
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.CompleteConversationResultAsync(a,v.Operation,Guid.NewGuid(),"reply")).Outcome);
        await f.Repository().DeleteAsync(a,v.Conversation);
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.CompleteConversationResultAsync(a,v.Operation,v.Conversation,"reply")).Outcome);
        var w=await Prepare(f,p);f.Time.Now=f.Time.Now.AddDays(15);
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.CompleteConversationResultAsync(a,w.Operation,w.Conversation,"reply")).Outcome);
    }

    [LocalPostgresFact]
    public async Task ExpiryDuringPreparationDeniesCommitAndRollsBackAssistant()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);
        var r=f.DispatchOperations(p,checkpoint:(phase,_)=>{if(phase=="prepared")f.Time.Now=f.Time.Now.AddDays(15);return Task.CompletedTask;});
        Assert.Equal(AiDispatchJournalOutcome.Denied,(await r.CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"reply")).Outcome);
        await using var db=await f.BoundOwner();Assert.Single(await db.Set<Andril.AI.Persistence.AiMessageEntity>().Where(x=>x.ConversationId==v.Conversation).ToListAsync());
        Assert.Equal(AiDispatchOperationState.Claimed,(await db.Set<IsolatedAiDispatchOperationRow>().SingleAsync()).State);
    }

    [LocalPostgresFact]
    public async Task RestrictedRuntimeCannotRewriteResultOrAuthorityBindingsAndRlsRemainsForced()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);await f.DispatchOperations(p).CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"reply");
        await using var db=f.OpenRuntime();var grants=await db.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai_isolated.messages','UPDATE')::text||'|'||has_column_privilege(current_user,'andril_ai_isolated.dispatch_operations','acknowledgement_version','UPDATE')::text AS \"Value\"").SingleAsync();Assert.Equal("false|false",grants);
        Assert.Empty(await db.Set<Andril.AI.Persistence.AiMessageEntity>().ToListAsync());Assert.Empty(await db.Set<IsolatedAiDispatchOperationRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task ControlledHttpCommitsRealPostgresResultBeforeReturningSanitizedStatus()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var journal=f.DispatchOperations(p);
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().Configure(app=>app.Run(async context=>
        {
            // Server-owned synthetic result: request fields never establish ownership or provider execution.
            var result=await journal.CompleteConversationResultAsync(f.Actor(),v.Operation,v.Conversation,"synthetic server result",context.RequestAborted);
            context.Response.StatusCode=result.Outcome switch{AiDispatchJournalOutcome.Saved=>200,AiDispatchJournalOutcome.Denied=>403,AiDispatchJournalOutcome.Conflict=>409,_=>503};
            await context.Response.WriteAsJsonAsync(new{outcome=result.Outcome.ToString()},context.RequestAborted);
        }))).StartAsync();using var client=host.GetTestClient();using var response=await client.PostAsJsonAsync("/isolated-atomic-ai-result",new{accountId="spoof"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.DoesNotContain("synthetic server result",await response.Content.ReadAsStringAsync());Assert.Equal(AiDispatchOperationState.Completed,(await journal.ReconcileAsync(f.Actor(),v.Operation)).Operation!.State);
        Assert.Equal("synthetic server result",(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100))[1].Text);
    }

    private static async Task AssertClaimedWithOnlyUser(LearningAiConversationPostgresFixture f,Policies p,Guid op,Guid conversation)
    {
        Assert.Equal(AiDispatchOperationState.Claimed,(await f.DispatchOperations(p).ReconcileAsync(f.Actor(),op)).Operation!.State);
        await using var db=await f.BoundOwner();var messages=await db.Set<Andril.AI.Persistence.AiMessageEntity>().Where(x=>x.ConversationId==conversation).ToListAsync();Assert.Single(messages);Assert.Equal(AiStoredMessageRole.User,messages[0].Role);
    }
}
