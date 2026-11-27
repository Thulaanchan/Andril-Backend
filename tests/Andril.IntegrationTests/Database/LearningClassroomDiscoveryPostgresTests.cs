using System.Net;
using System.Net.Http.Json;
using Andril.API.Integrations.AI;
using Andril.Groups.Interfaces;
using Andril.Persistence.Repositories.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningClassroomDiscoveryPostgresTests
{
    private static IsolatedClassroomDiscoveryRow Eligible(LearningAiConversationPostgresFixture f, string name)
        => new() { AccountId=f.Actor().UserId, ModuleProfileId=f.Actor().ModuleProfileId, ClassroomId=Guid.NewGuid(),Name=name,
            Lifecycle=ClassroomDiscoveryLifecycle.Active, Discoverability=ClassroomDiscoveryDecision.Allowed,
            Visibility=ClassroomDiscoveryDecision.Allowed,Membership=ClassroomDiscoveryDecision.Allowed,
            Blocking=ClassroomDiscoveryDecision.Allowed,NameAiDisclosure=ClassroomDiscoveryDecision.Allowed };
    private static async Task Seed(LearningAiConversationPostgresFixture f,params IsolatedClassroomDiscoveryRow[] rows)
    {await using var db=await f.BoundOwner();db.AddRange(rows);await db.SaveChangesAsync();}

    [LocalPostgresFact]
    public async Task ParameterizedSearchReturnsOnlyMatchingAuthorizedSourceNameWithoutIdentifiers()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class Algebra"),Eligible(f,"Geometry"));
        var adapter=new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),f.Classrooms());
        var result=await adapter.SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"Algebra\"}");
        Assert.Equal(LearningClassroomAiSearchOutcome.Available,result.Outcome);Assert.Equal("Class Algebra",Assert.Single(result.Classrooms).DisplayName);
        Assert.DoesNotContain(f.Actor().UserId.ToString(),result.ToToolJson());Assert.DoesNotContain(f.Actor().ModuleProfileId.ToString(),result.ToToolJson());
        var malicious=await adapter.SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"' OR 1=1 --\"}");Assert.Empty(malicious.Classrooms);
    }

    [LocalPostgresFact]
    public async Task ActiveVisibilityMembershipBlockingAndAiClassificationAreDatabasePredicates()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var rows=new List<IsolatedClassroomDiscoveryRow>();
        for(var i=0;i<7;i++)rows.Add(Eligible(f,"Class private "+i));
        rows[0].Lifecycle=ClassroomDiscoveryLifecycle.Inactive;rows[1].Lifecycle=ClassroomDiscoveryLifecycle.Closed;
        rows[2].Discoverability=ClassroomDiscoveryDecision.Unknown;rows[3].Visibility=ClassroomDiscoveryDecision.Denied;
        rows[4].Membership=ClassroomDiscoveryDecision.Unknown;rows[5].Blocking=ClassroomDiscoveryDecision.Denied;rows[6].NameAiDisclosure=ClassroomDiscoveryDecision.Unknown;
        await Seed(f,rows.ToArray());var result=await new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),f.Classrooms()).SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"Class\"}");
        Assert.Equal(LearningClassroomAiSearchOutcome.Available,result.Outcome);Assert.Empty(result.Classrooms);
    }

    [LocalPostgresFact]
    public async Task PrivateClassroomRequiresSpecificDiscoveryAuthorization()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var hidden=Eligible(f,"Class hidden");hidden.IsPrivate=true;
        var allowed=Eligible(f,"Class member-visible");allowed.IsPrivate=true;allowed.PrivateAuthorization=ClassroomDiscoveryDecision.Allowed;await Seed(f,hidden,allowed);
        var result=await new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),f.Classrooms()).SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"Class\"}");
        Assert.Equal("Class member-visible",Assert.Single(result.Classrooms).DisplayName);
    }

    [LocalPostgresFact]
    public async Task ForeignAccountAndProfileClaimsCannotReadSourceEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class private"));var actor=f.Actor();var reader=f.Classrooms();
        Assert.Null(await reader.WithAuthorizedSearchAsync<string>(Guid.NewGuid(),actor.ModuleProfileId,"Class",(_,_)=>Task.FromResult("LEAK")));
        Assert.Null(await reader.WithAuthorizedSearchAsync<string>(actor.UserId,Guid.NewGuid(),"Class",(_,_)=>Task.FromResult("LEAK")));
    }

    [LocalPostgresFact]
    public async Task SourceAuthorityRevokedAtReadReleaseDiscardsProjectedDataAndPreservesRows()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class retained"));
        var reader=f.Classrooms((phase,_)=>{if(phase=="read-release")f.Owner.Denied=true;return Task.CompletedTask;});
        var result=await new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),reader).SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"Class\"}");
        Assert.NotEqual(LearningClassroomAiSearchOutcome.Available,result.Outcome);Assert.Empty(result.Classrooms);
        f.Owner.Denied=false;await using var db=await f.BoundOwner();Assert.Single(await db.Set<IsolatedClassroomDiscoveryRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RestrictedRuntimeHasSelectOnlyAndForceRlsHidesUnboundEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class restricted"));await using var db=f.OpenRuntime();
        var permissions=await db.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai_isolated.classroom_discovery','SELECT')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.classroom_discovery','INSERT')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.classroom_discovery','UPDATE')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.classroom_discovery','DELETE')::text AS \"Value\"").SingleAsync();
        Assert.Equal("true|false|false|false",permissions);Assert.Empty(await db.Set<IsolatedClassroomDiscoveryRow>().ToListAsync());
        var flags=await db.Database.SqlQueryRaw<string>("SELECT relrowsecurity::text||'|'||relforcerowsecurity::text AS \"Value\" FROM pg_class WHERE oid='andril_ai_isolated.classroom_discovery'::regclass").SingleAsync();Assert.Equal("true|true",flags);
    }

    [LocalPostgresFact]
    public async Task CancellationAtDatabaseReleasePropagatesWithoutWriteSideEffects()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class unchanged"));using var cts=new CancellationTokenSource();
        var reader=f.Classrooms((phase,ct)=>{if(phase=="read-release")cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),reader).SearchAsync(Andril.AI.ChatBox.AiModule.Learning,"{\"query\":\"Class\"}",cts.Token));
        await using var db=await f.BoundOwner();Assert.Equal("Class unchanged",Assert.Single(await db.Set<IsolatedClassroomDiscoveryRow>().ToListAsync()).Name);
    }

    [LocalPostgresFact]
    public async Task ControlledHttpReadsActualPostgresThroughAdapterAndEscapesClassroomText()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f,"Class <script>inert</script>"));var adapter=new LearningClassroomAiSearchAdapter(f.ClassroomPrivacy(),f.Classrooms());
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().Configure(app=>app.Run(async context=>
        {
            var input=await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            var result=await adapter.SearchAsync(Andril.AI.ChatBox.AiModule.Learning,input,context.RequestAborted);
            context.Response.StatusCode=result.Outcome switch{LearningClassroomAiSearchOutcome.Available=>200,LearningClassroomAiSearchOutcome.InvalidRequest=>400,LearningClassroomAiSearchOutcome.Denied=>403,_=>503};
            context.Response.ContentType="application/json; charset=utf-8";await context.Response.WriteAsync(result.ToToolJson(),context.RequestAborted);
        }))).StartAsync();
        using var client=host.GetTestClient();using var response=await client.PostAsJsonAsync("/isolated-classrooms",new{query="Class"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var json=await response.Content.ReadAsStringAsync();Assert.Contains("inert",json);Assert.DoesNotContain("<script>",json);Assert.DoesNotContain(f.Actor().UserId.ToString(),json);
        f.Owner.Denied=true;using var denied=await client.PostAsJsonAsync("/isolated-classrooms",new{query="Class"});Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.DoesNotContain("inert",await denied.Content.ReadAsStringAsync());
    }
}
