using System.Net;
using System.Net.Http.Json;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningAiCoordinatedDispatchPostgresTests
{
    private sealed class Policies : IAiPrivacyPolicyVersionSource
    {
        public Task<string?> ReadCurrentVersionAsync(AiAuthorizedActor actor,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<string?>("synthetic-policy");}
    }
    private sealed class Provider : IAiResponseProvider
    {
        internal int Sends;
        internal Func<CancellationToken,Task>? DuringSend;
        public async Task<AiProviderResponse> GenerateAsync(AiProviderPrompt prompt,CancellationToken cancellationToken=default)
        {Interlocked.Increment(ref Sends);if(DuringSend is not null)await DuringSend(cancellationToken);cancellationToken.ThrowIfCancellationRequested();return new("PRIVATE_SYNTHETIC_REPLY",true);}
    }
    // Synthetic transport authority only. These tests exercise integration decisions, not proof
    // of production M2/provider claim-to-send linearization or an external transport fence.
    private sealed class Transport : IAiProviderDispatchBoundary
    {
        internal Func<AiProviderDispatchBinding,Func<CancellationToken,Task>,CancellationToken,Task<AiProviderDispatchReceipt>>? Execute;
        public Task<AiProviderDispatchReceipt> DispatchAsync(AiProviderDispatchBinding binding,Func<CancellationToken,Task> send,CancellationToken cancellationToken=default)
            => Execute is null?Run(send,cancellationToken):Execute(binding,send,cancellationToken);
        private static async Task<AiProviderDispatchReceipt> Run(Func<CancellationToken,Task> send,CancellationToken ct)
        {await send(ct);return new(AiProviderDispatchOutcome.Sent);}
    }
    private static async Task<(Guid Op,Guid Conversation,AiAcknowledgementRevision Revision)> Prepare(LearningAiConversationPostgresFixture f,Policies p)
    {
        var a=f.Actor();var grant=await f.Acknowledgements(p).AcknowledgeAsync(a,"synthetic-policy",Guid.NewGuid(),null);
        Assert.Equal(AiAcknowledgementOutcome.Saved,grant.Outcome);var op=Guid.NewGuid();var c=await f.Repository().CreateWithUserMessageAsync(a,op,"synthetic request");return(op,c.ConversationId,grant.State!.Revision);
    }
    private static AiCoordinatedConversationResultService Service(LearningAiConversationPostgresFixture f,Policies p,IAiProviderDispatchBoundary transport,Provider provider)
        => new(f.ClassroomPrivacy(),f.Acknowledgements(p),f.DispatchOperations(p),f.Repository(),transport,provider);

    [LocalPostgresFact]
    public async Task SyntheticDispatchPersistsReplyAndCompletedJournalThroughActualAtomicBoundary()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var result=await Service(f,p,new Transport(),provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request");
        Assert.Equal(AiCoordinatedResultOutcome.Persisted,result.Outcome);Assert.Equal(1,provider.Sends);Assert.DoesNotContain("PRIVATE",result.ToString());
        Assert.Equal(AiDispatchOperationState.Completed,(await f.DispatchOperations(p).ReconcileAsync(f.Actor(),v.Op)).Operation!.State);
        Assert.Equal("PRIVATE_SYNTHETIC_REPLY",(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100))[1].Text);
    }

    [LocalPostgresFact]
    public async Task DefaultUnavailableTransportNeverInvokesProviderAndCancelsClaim()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var result=await Service(f,p,new UnavailableAiProviderDispatchBoundary(),provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request");
        Assert.Equal(AiCoordinatedResultOutcome.Unavailable,result.Outcome);Assert.Equal(0,provider.Sends);
        Assert.Equal(AiDispatchOperationState.Cancelled,(await f.DispatchOperations(p).ReconcileAsync(f.Actor(),v.Op)).Operation!.State);Assert.Single(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100));
    }

    [LocalPostgresFact]
    public async Task RevocationBeforeTransportSendIsDeniedBySyntheticAuthorityWithoutProviderCall()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var transport=new Transport{Execute=async(binding,_,ct)=>
        {Assert.Equal(v.Op,binding.OperationId);await f.Acknowledgements(p).RevokeAsync(f.Actor(),Guid.NewGuid(),v.Revision,ct);return new(AiProviderDispatchOutcome.Denied);}};
        Assert.Equal(AiCoordinatedResultOutcome.Denied,(await Service(f,p,transport,provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request")).Outcome);Assert.Equal(0,provider.Sends);
    }

    [LocalPostgresFact]
    public async Task RevocationDuringSyntheticProviderCallPreventsResultPersistence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider{DuringSend=async ct=>{await f.Acknowledgements(p).RevokeAsync(f.Actor(),Guid.NewGuid(),v.Revision,ct);}};
        Assert.Equal(AiCoordinatedResultOutcome.Denied,(await Service(f,p,new Transport(),provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request")).Outcome);Assert.Equal(1,provider.Sends);Assert.Single(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100));
    }

    [LocalPostgresFact]
    public async Task ProviderFailureMarksIndeterminateAndNeverAutomaticallyReplays()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider{DuringSend=_=>throw new IOException("PRIVATE_FAILURE",new Exception("PRIVATE_INNER"))};var service=Service(f,p,new Transport(),provider);
        var result=await service.ExecuteAsync(v.Conversation,v.Op,"synthetic request");Assert.Equal(AiCoordinatedResultOutcome.ReconciliationRequired,result.Outcome);Assert.DoesNotContain("PRIVATE",result.ToString());
        Assert.Equal(AiDispatchOperationState.Indeterminate,(await f.DispatchOperations(p).ReconcileAsync(f.Actor(),v.Op)).Operation!.State);
        Assert.Equal(AiCoordinatedResultOutcome.Conflict,(await service.ExecuteAsync(v.Conversation,v.Op,"synthetic request")).Outcome);Assert.Equal(1,provider.Sends);
    }

    [LocalPostgresFact]
    public async Task TransportCannotFabricateSuccessfulSendWithoutExecutingCallback()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var transport=new Transport{Execute=(_,_,_)=>Task.FromResult(new AiProviderDispatchReceipt(AiProviderDispatchOutcome.Sent))};
        Assert.Equal(AiCoordinatedResultOutcome.ReconciliationRequired,(await Service(f,p,transport,provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request")).Outcome);Assert.Equal(0,provider.Sends);Assert.Single(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100));
    }

    [LocalPostgresFact]
    public async Task TransportSecondCallbackCannotCauseSecondProviderExecution()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var transport=new Transport{Execute=async(_,send,ct)=>{await send(ct);await send(ct);return new(AiProviderDispatchOutcome.Sent);}};
        Assert.Equal(AiCoordinatedResultOutcome.ReconciliationRequired,(await Service(f,p,transport,provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request")).Outcome);Assert.Equal(1,provider.Sends);Assert.Single(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100));
    }

    [LocalPostgresFact]
    public async Task CancellationDuringProviderIsPropagatedWithDurableNonReplayState()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);using var cts=new CancellationTokenSource();var provider=new Provider{DuringSend=ct=>{cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service(f,p,new Transport(),provider).ExecuteAsync(v.Conversation,v.Op,"synthetic request",cts.Token));
        Assert.Equal(AiDispatchOperationState.Indeterminate,(await f.DispatchOperations(p).ReconcileAsync(f.Actor(),v.Op)).Operation!.State);Assert.Single(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100));
    }

    [LocalPostgresFact]
    public async Task UnauthorizedConversationAndOversizedInputNeverReachProvider()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var service=Service(f,p,new Transport(),provider);
        Assert.Equal(AiCoordinatedResultOutcome.Denied,(await service.ExecuteAsync(Guid.NewGuid(),v.Op,"synthetic request")).Outcome);
        Assert.Equal(AiCoordinatedResultOutcome.InvalidRequest,(await service.ExecuteAsync(v.Conversation,v.Op,new string('x',501))).Outcome);Assert.Equal(0,provider.Sends);
    }

    [LocalPostgresFact]
    public async Task ControlledHttpUsesRealAtomicPersistenceAndReturnsNoPrivateProviderText()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var p=new Policies();var v=await Prepare(f,p);var provider=new Provider();var service=Service(f,p,new Transport(),provider);
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().Configure(app=>app.Run(async context=>
        {
            var result=await service.ExecuteAsync(v.Conversation,v.Op,"synthetic request",context.RequestAborted);
            context.Response.StatusCode=result.Outcome==AiCoordinatedResultOutcome.Persisted?200:503;
            await context.Response.WriteAsJsonAsync(new{outcome=result.Outcome.ToString()},context.RequestAborted);
        }))).StartAsync();using var client=host.GetTestClient();using var response=await client.PostAsJsonAsync("/isolated-coordinated-dispatch",new{accountId="untrusted"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.DoesNotContain("PRIVATE",await response.Content.ReadAsStringAsync());Assert.Equal(1,provider.Sends);Assert.Equal(2,(await f.Repository().ReadRecentMessagesAsync(f.Actor(),v.Conversation,100)).Count);
    }
}
