using System.Net;
using System.Net.Http.Json;
using Andril.AI.ChatBox;
using Andril.API.Integrations.AI;
using Andril.Learning.Discovery;
using Andril.Persistence.Repositories.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningPartnerDiscoveryPostgresTests
{
    private static IsolatedLearningPartnerDiscoveryRow Eligible(LearningAiConversationPostgresFixture f,string name="Math learner")
        => new(){AccountId=f.Actor().UserId,ModuleProfileId=f.Actor().ModuleProfileId,CandidateProfileId=Guid.NewGuid(),
            Lifecycle=LearningDiscoveryLifecycle.Active,Eligibility=LearningDiscoveryDecision.Allowed,Visibility=LearningDiscoveryDecision.Allowed,Blocking=LearningDiscoveryDecision.Allowed,
            DisplayName=name,NameDisclosure=LearningDiscoveryDecision.Allowed,Interests="math",InterestsDisclosure=LearningDiscoveryDecision.Allowed,
            Skills="algebra",SkillsDisclosure=LearningDiscoveryDecision.Allowed,Goals="learn proofs",GoalsDisclosure=LearningDiscoveryDecision.Allowed,
            ProfessionalBiography="Teacher",BiographyDisclosure=LearningDiscoveryDecision.Allowed};
    private static async Task Seed(LearningAiConversationPostgresFixture f,params IsolatedLearningPartnerDiscoveryRow[] rows)
    {await using var db=await f.BoundOwner();db.AddRange(rows);await db.SaveChangesAsync();}
    private static Task<LearningPartnerAiSearchResult> Search(LearningAiConversationPostgresFixture f,string query="math")
        => new LearningPartnerAiSearchAdapter(f.ClassroomPrivacy(),f.Partners()).SearchAsync(AiModule.Learning,System.Text.Json.JsonSerializer.Serialize(new{query}));

    [LocalPostgresFact]
    public async Task EligiblePartnerFiveFieldRoundTripExcludesAllInternalIdentifiers()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var row=Eligible(f);await Seed(f,row);var result=await Search(f);var card=Assert.Single(result.Partners);
        Assert.Equal(LearningPartnerAiSearchOutcome.Available,result.Outcome);Assert.Equal(row.DisplayName,card.DisplayName);Assert.Equal(row.Interests,card.Interests);Assert.Equal(row.Skills,card.Skills);Assert.Equal(row.Goals,card.Goals);Assert.Equal(row.ProfessionalBiography,card.ProfessionalBiography);
        var json=result.ToToolJson();Assert.DoesNotContain(row.CandidateProfileId.ToString(),json);Assert.DoesNotContain(row.AccountId.ToString(),json);Assert.DoesNotContain(row.ModuleProfileId.ToString(),json);
    }

    [LocalPostgresFact]
    public async Task DatabaseExcludesSelfHiddenInactiveBlockedAndUnknownEligibility()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var rows=Enumerable.Range(0,6).Select(_=>Eligible(f)).ToArray();
        rows[0].CandidateProfileId=f.Actor().ModuleProfileId;rows[1].Lifecycle=LearningDiscoveryLifecycle.Inactive;rows[2].Visibility=LearningDiscoveryDecision.Denied;
        rows[3].Blocking=LearningDiscoveryDecision.Denied;rows[4].Eligibility=LearningDiscoveryDecision.Unknown;rows[5].Lifecycle=LearningDiscoveryLifecycle.Unknown;
        await Seed(f,rows);Assert.Empty((await Search(f)).Partners);
    }

    [LocalPostgresFact]
    public async Task UnclassifiedFieldsNeitherReachProjectionNorAffectSearchMatching()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var row=Eligible(f);row.DisplayName="PRIVATE_NAME";row.NameDisclosure=LearningDiscoveryDecision.Unknown;
        row.ProfessionalBiography="SECRET_NEEDLE private@example.test";row.BiographyDisclosure=LearningDiscoveryDecision.Unknown;row.Goals="PRIVATE_GOAL";row.GoalsDisclosure=LearningDiscoveryDecision.Denied;await Seed(f,row);
        var result=await Search(f);var card=Assert.Single(result.Partners);Assert.Null(card.DisplayName);Assert.Null(card.ProfessionalBiography);Assert.Null(card.Goals);
        Assert.DoesNotContain("PRIVATE",result.ToToolJson());Assert.DoesNotContain("example.test",result.ToToolJson());Assert.Empty((await Search(f,"SECRET_NEEDLE")).Partners);
        await using var db=await f.BoundOwner();Assert.Equal(row.ProfessionalBiography,(await db.Set<IsolatedLearningPartnerDiscoveryRow>().SingleAsync()).ProfessionalBiography);
    }

    [LocalPostgresFact]
    public async Task ParameterizedMaliciousQueryCannotBroadenSearchOrMutateEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f));Assert.Empty((await Search(f,"' OR 1=1; DROP TABLE x; --")).Partners);
        await using var db=await f.BoundOwner();Assert.Single(await db.Set<IsolatedLearningPartnerDiscoveryRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task ForeignRequesterAccountOrLearningProfileCannotReadEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f));var actor=f.Actor();var reader=f.Partners();
        Assert.Null(await reader.WithAuthorizedSearchAsync<string>(Guid.NewGuid(),actor.ModuleProfileId,"math",(_,_)=>Task.FromResult("LEAK")));
        Assert.Null(await reader.WithAuthorizedSearchAsync<string>(actor.UserId,Guid.NewGuid(),"math",(_,_)=>Task.FromResult("LEAK")));
        Assert.Equal(LearningPartnerAiSearchOutcome.Denied,(await new LearningPartnerAiSearchAdapter(f.ClassroomPrivacy(),reader).SearchAsync(AiModule.Hobbies,"{\"query\":\"math\"}")).Outcome);
    }

    [LocalPostgresFact]
    public async Task RevocationAtReadReleaseDiscardsDataAndPreservesEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f));
        var reader=f.Partners((phase,_)=>{if(phase=="read-release")f.Owner.Denied=true;return Task.CompletedTask;});
        var result=await new LearningPartnerAiSearchAdapter(f.ClassroomPrivacy(),reader).SearchAsync(AiModule.Learning,"{\"query\":\"math\"}");Assert.NotEqual(LearningPartnerAiSearchOutcome.Available,result.Outcome);Assert.Empty(result.Partners);
        f.Owner.Denied=false;await using var db=await f.BoundOwner();Assert.Single(await db.Set<IsolatedLearningPartnerDiscoveryRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task RuntimeIsReadOnlyAndUnboundRlsScopeCannotReadAnyCandidate()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f));await using var db=f.OpenRuntime();
        var flags=await db.Database.SqlQueryRaw<string>("SELECT relrowsecurity::text||'|'||relforcerowsecurity::text AS \"Value\" FROM pg_class WHERE oid='andril_ai_isolated.learning_partner_discovery'::regclass").SingleAsync();Assert.Equal("true|true",flags);
        var permissions=await db.Database.SqlQueryRaw<string>("SELECT has_table_privilege(current_user,'andril_ai_isolated.learning_partner_discovery','SELECT')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.learning_partner_discovery','INSERT')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.learning_partner_discovery','UPDATE')::text||'|'||has_table_privilege(current_user,'andril_ai_isolated.learning_partner_discovery','DELETE')::text AS \"Value\"").SingleAsync();Assert.Equal("true|false|false|false",permissions);Assert.Empty(await db.Set<IsolatedLearningPartnerDiscoveryRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task CancellationAtReleasePropagatesAndNeverChangesEvidence()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();await Seed(f,Eligible(f));using var cts=new CancellationTokenSource();
        var reader=f.Partners((phase,ct)=>{if(phase=="read-release")cts.Cancel();ct.ThrowIfCancellationRequested();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new LearningPartnerAiSearchAdapter(f.ClassroomPrivacy(),reader).SearchAsync(AiModule.Learning,"{\"query\":\"math\"}",cts.Token));
        await using var db=await f.BoundOwner();Assert.Single(await db.Set<IsolatedLearningPartnerDiscoveryRow>().ToListAsync());
    }

    [LocalPostgresFact]
    public async Task OtherModuleEvidenceIsRejectedByDatabaseConstraint()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var row=Eligible(f);row.Module="social";await using var db=await f.BoundOwner();db.Add(row);
        var error=await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());var pg=Assert.IsType<Npgsql.PostgresException>(error.InnerException);Assert.Equal("23514",pg.SqlState);
    }

    [LocalPostgresFact]
    public async Task ControlledHttpUsesRealPostgresReaderAndEscapedPrivateProjection()
    {
        await using var f=await LearningAiConversationPostgresFixture.Create();var row=Eligible(f,"Math <script>inert</script>");row.ProfessionalBiography="PRIVATE_BIO";row.BiographyDisclosure=LearningDiscoveryDecision.Unknown;await Seed(f,row);
        var adapter=new LearningPartnerAiSearchAdapter(f.ClassroomPrivacy(),f.Partners());
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().Configure(app=>app.Run(async context=>
        {
            var input=await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);var result=await adapter.SearchAsync(AiModule.Learning,input,context.RequestAborted);
            context.Response.StatusCode=result.Outcome switch{LearningPartnerAiSearchOutcome.Available=>200,LearningPartnerAiSearchOutcome.InvalidRequest=>400,LearningPartnerAiSearchOutcome.Denied=>403,_=>503};
            context.Response.ContentType="application/json; charset=utf-8";await context.Response.WriteAsync(result.ToToolJson(),context.RequestAborted);
        }))).StartAsync();using var client=host.GetTestClient();using var response=await client.PostAsJsonAsync("/isolated-learning-partners",new{query="math"});Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var json=await response.Content.ReadAsStringAsync();Assert.Contains("inert",json);Assert.DoesNotContain("<script>",json);Assert.DoesNotContain("PRIVATE_BIO",json);Assert.DoesNotContain(row.CandidateProfileId.ToString(),json);
        f.Owner.Denied=true;using var denied=await client.PostAsJsonAsync("/isolated-learning-partners",new{query="math"});Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.DoesNotContain("inert",await denied.Content.ReadAsStringAsync());
    }
}
