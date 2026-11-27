using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Services;
using Andril.API.Integrations.AI;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.SecurityTests.AI;

public sealed class LearningClassroomAiSearchAdapterTests
{
    private sealed class Authorization : IAiActorAuthorization
    {
        internal AiAuthorizedActor? Actor = new(Guid.NewGuid(), Guid.NewGuid(), AiModule.Learning);
        internal int Calls;
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Actor); }
    }
    private sealed class Privacy : IAiPrivacyNoticeGate
    {
        internal AiPrivacyNoticeGateOutcome Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Outcome); }
    }
    private sealed class Reader : ILearningClassroomDiscoveryReader
    {
        internal int Calls;
        internal LearningClassroomDiscoverySnapshot? Snapshot;
        internal string? Query;
        internal Guid RequestedAccount;
        internal Guid RequestedProfile;
        internal Exception? Failure;
        internal Func<Task>? BeforeRelease;
        internal bool SkipCallback;
        public async Task<T?> WithAuthorizedSearchAsync<T>(Guid accountId, Guid learningProfileId, string query,
            Func<LearningClassroomDiscoverySnapshot, CancellationToken, Task<T>> project, CancellationToken cancellationToken = default) where T : class
        {
            Calls++; cancellationToken.ThrowIfCancellationRequested(); Query = query;
            if (Failure is not null) throw Failure;
            if (Snapshot is null || SkipCallback) return null;
            RequestedAccount=accountId;RequestedProfile=learningProfileId;
            var result = await project(Snapshot, cancellationToken);
            if (BeforeRelease is not null) await BeforeRelease();
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
    }
    private sealed class Fixture
    {
        internal Authorization Authorization { get; } = new();
        internal Privacy Privacy { get; } = new();
        internal Reader Reader { get; } = new();
        internal LearningClassroomAiSearchAdapter Adapter { get; }
        internal Fixture(params LearningClassroomDiscoveryEvidence[] evidence)
        {
            var a = Authorization.Actor!; Reader.Snapshot = new(a.UserId,a.ModuleProfileId,evidence);
            Adapter = new(new AiPrivacyNoticeGuard(Authorization,Privacy),Reader);
        }
    }
    private static LearningClassroomDiscoveryEvidence Eligible(string name = "Calculus")
        => new(new GroupEntry(Guid.NewGuid(),new GroupModuleScope("learning",GroupKind.Classroom),name),
            ClassroomDiscoveryLifecycle.Active,ClassroomDiscoveryDecision.Allowed,false,ClassroomDiscoveryDecision.Unknown,
            ClassroomDiscoveryDecision.Allowed,ClassroomDiscoveryDecision.Allowed,ClassroomDiscoveryDecision.Allowed,ClassroomDiscoveryDecision.Allowed);
    private const string Query = "{\"query\":\"calculus\"}";

    [Fact]
    public async Task AuthorizedSearchProjectsOnlyExplicitlyClassifiedSourceName()
    {
        var f=new Fixture(Eligible());var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);
        Assert.Equal(LearningClassroomAiSearchOutcome.Available,result.Outcome);Assert.Equal("Calculus",Assert.Single(result.Classrooms).DisplayName);
        Assert.Equal("calculus",f.Reader.Query);Assert.Equal(1,f.Reader.Calls);Assert.True(f.Authorization.Calls>=3);
        Assert.Equal(f.Authorization.Actor!.UserId,f.Reader.RequestedAccount);Assert.Equal(f.Authorization.Actor.ModuleProfileId,f.Reader.RequestedProfile);
        var json=JsonSerializer.Serialize(result);
        Assert.DoesNotContain(f.Authorization.Actor!.UserId.ToString(),json);Assert.DoesNotContain(f.Authorization.Actor.ModuleProfileId.ToString(),json);
        Assert.DoesNotContain(f.Reader.Snapshot!.Classrooms[0].Group.Id.ToString(),json);
        Assert.DoesNotContain("Calculus",result.ToString());Assert.DoesNotContain("Calculus",result.Classrooms[0].ToString());
    }

    [Theory]
    [InlineData(ClassroomDiscoveryLifecycle.Unknown)]
    [InlineData(ClassroomDiscoveryLifecycle.Inactive)]
    [InlineData(ClassroomDiscoveryLifecycle.Closed)]
    public async Task NonactiveOrUnknownClassroomLifecycleIsExcluded(ClassroomDiscoveryLifecycle lifecycle)
    {
        var f=new Fixture(Eligible() with {Lifecycle=lifecycle});Assert.Empty((await f.Adapter.SearchAsync(AiModule.Learning,Query)).Classrooms);
    }

    [Fact]
    public async Task PrivateClassroomRequiresExplicitCurrentDiscoveryAuthorization()
    {
        var hidden=Eligible("PRIVATE_CLASSROOM") with {IsPrivate=true};var allowed=Eligible("Authorized private class") with {IsPrivate=true,PrivateDiscoveryAuthorization=ClassroomDiscoveryDecision.Allowed};
        var f=new Fixture(hidden,allowed);var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);
        Assert.Equal("Authorized private class",Assert.Single(result.Classrooms).DisplayName);Assert.DoesNotContain("PRIVATE_CLASSROOM",JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task VisibilityMembershipBlockAndDiscoveryDenialsAndUnknownsAreExcluded()
    {
        foreach(var decision in new[]{ClassroomDiscoveryDecision.Unknown,ClassroomDiscoveryDecision.Denied})
        {
            var e=Eligible();var f=new Fixture(e with {Visibility=decision},e with {Group=new(Guid.NewGuid(),e.Group.Scope,"membership"),MembershipRestrictions=decision},
                e with {Group=new(Guid.NewGuid(),e.Group.Scope,"blocked"),BlockingRestrictions=decision},e with {Group=new(Guid.NewGuid(),e.Group.Scope,"hidden"),Discoverability=decision});
            Assert.Empty((await f.Adapter.SearchAsync(AiModule.Learning,Query)).Classrooms);
        }
    }

    [Fact]
    public async Task UnclassifiedLegacyNamesAndContactNamesRemainWithheld()
    {
        var f=new Fixture(Eligible("UNCLASSIFIED_PRIVATE_TEXT") with {NameAiDisclosure=ClassroomDiscoveryDecision.Unknown},
            Eligible("contact@example.test") with {NameAiDisclosure=ClassroomDiscoveryDecision.Denied});
        var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);Assert.Empty(result.Classrooms);
        var json=JsonSerializer.Serialize(result);Assert.DoesNotContain("UNCLASSIFIED_PRIVATE_TEXT",json);Assert.DoesNotContain("contact@example.test",json);
    }

    [Fact]
    public async Task OtherModuleGroupsAndRequesterModulesCannotDiscloseData()
    {
        var f=new Fixture(Eligible() with {Group=new(Guid.NewGuid(),new GroupModuleScope("hobbies",GroupKind.Club),"PRIVATE_CLUB")});
        Assert.Empty((await f.Adapter.SearchAsync(AiModule.Learning,Query)).Classrooms);
        var reads=f.Reader.Calls;var denied=await f.Adapter.SearchAsync(AiModule.Hobbies,Query);
        Assert.Equal(LearningClassroomAiSearchOutcome.Denied,denied.Outcome);Assert.Equal(reads,f.Reader.Calls);
    }

    [Theory]
    [InlineData("{\"query\":\"x\",\"accountId\":\"spoof\"}")]
    [InlineData("{\"query\":\"x\",\"query\":\"y\"}")]
    [InlineData("{\"query\":\"x\",\"join\":true}")]
    [InlineData("{\"query\":4}")]
    [InlineData("not-json")]
    public async Task MalformedOrIdentityBearingToolArgumentsNeverReachAuthority(string arguments)
    {
        var f=new Fixture(Eligible());var result=await f.Adapter.SearchAsync(AiModule.Learning,arguments);
        Assert.Equal(LearningClassroomAiSearchOutcome.InvalidRequest,result.Outcome);Assert.Equal(0,f.Reader.Calls);Assert.Equal(0,f.Authorization.Calls);
    }

    [Fact]
    public async Task ExistingFiveHundredCharacterLimitIsEnforcedWithoutNewTierRules()
    {
        var f=new Fixture(Eligible());
        Assert.Equal(LearningClassroomAiSearchOutcome.InvalidRequest,(await f.Adapter.SearchAsync(AiModule.Learning,JsonSerializer.Serialize(new{query=new string('x',501)}))).Outcome);
        Assert.Equal(0,f.Reader.Calls);
    }

    [Fact]
    public async Task UnauthenticatedOrUnavailablePrivacyNeverExecutesSearch()
    {
        var f=new Fixture(Eligible());f.Authorization.Actor=null;
        Assert.Equal(LearningClassroomAiSearchOutcome.Denied,(await f.Adapter.SearchAsync(AiModule.Learning,Query)).Outcome);Assert.Equal(0,f.Reader.Calls);
        var g=new Fixture(Eligible());g.Privacy.Outcome=AiPrivacyNoticeGateOutcome.PolicyNotVerified;
        Assert.NotEqual(LearningClassroomAiSearchOutcome.Available,(await g.Adapter.SearchAsync(AiModule.Learning,Query)).Outcome);Assert.Equal(0,g.Reader.Calls);
    }

    [Fact]
    public async Task RevocationOrIdentitySwitchBeforeReleaseDiscardsProjectedData()
    {
        foreach(var changeIdentity in new[]{false,true})
        {
            var f=new Fixture(Eligible("PRIVATE_AFTER_REVOKE"));f.Reader.BeforeRelease=()=>{f.Authorization.Actor=changeIdentity?new(Guid.NewGuid(),Guid.NewGuid(),AiModule.Learning):null;return Task.CompletedTask;};
            var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);
            Assert.Equal(LearningClassroomAiSearchOutcome.Denied,result.Outcome);Assert.Empty(result.Classrooms);
        }
    }

    [Fact]
    public async Task SourceSnapshotBoundToForeignAccountOrProfileIsDenied()
    {
        var f=new Fixture(Eligible("PRIVATE_FOREIGN"));f.Reader.Snapshot=f.Reader.Snapshot! with{AccountId=Guid.NewGuid()};
        var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);Assert.Equal(LearningClassroomAiSearchOutcome.Denied,result.Outcome);Assert.Empty(result.Classrooms);
        var g=new Fixture(Eligible("PRIVATE_PROFILE"));g.Reader.Snapshot=g.Reader.Snapshot! with{LearningProfileId=Guid.NewGuid()};
        Assert.Equal(LearningClassroomAiSearchOutcome.Denied,(await g.Adapter.SearchAsync(AiModule.Learning,Query)).Outcome);
    }

    [Fact]
    public async Task SourceWithoutAuthorizedCallbackAndDuplicateEvidenceFailClosed()
    {
        var e=Eligible();var f=new Fixture(e,e);
        Assert.Equal(LearningClassroomAiSearchOutcome.Unavailable,(await f.Adapter.SearchAsync(AiModule.Learning,Query)).Outcome);
        var g=new Fixture(Eligible());g.Reader.SkipCallback=true;
        Assert.Equal(LearningClassroomAiSearchOutcome.Unavailable,(await g.Adapter.SearchAsync(AiModule.Learning,Query)).Outcome);
    }

    [Fact]
    public async Task InfrastructureErrorsHaveNoPrivateExceptionPayloadInResult()
    {
        var f=new Fixture(Eligible());f.Reader.Failure=new IOException("PRIVATE_SOURCE_SECRET",new Exception("PRIVATE_INNER"));
        var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);Assert.Equal(LearningClassroomAiSearchOutcome.Unavailable,result.Outcome);
        Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(result));Assert.DoesNotContain("PRIVATE",result.ToString());
    }

    [Fact]
    public async Task CancellationBeforeReadAndAtReleasePropagates()
    {
        using var cts=new CancellationTokenSource();var f=new Fixture(Eligible());cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Adapter.SearchAsync(AiModule.Learning,Query,cts.Token));Assert.Equal(0,f.Reader.Calls);
        using var release=new CancellationTokenSource();var g=new Fixture(Eligible());g.Reader.BeforeRelease=()=>{release.Cancel();return Task.CompletedTask;};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>g.Adapter.SearchAsync(AiModule.Learning,Query,release.Token));
    }

    [Fact]
    public async Task ControlledHttpSerializesOnlyInertAllowlistedFieldsAndSanitizedDenials()
    {
        var f=new Fixture(Eligible("<script>untrusted title</script>"));
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().Configure(app=>app.Run(async context=>
        {
            var arguments=await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            var result=await f.Adapter.SearchAsync(AiModule.Learning,arguments,context.RequestAborted);
            context.Response.StatusCode=result.Outcome switch{LearningClassroomAiSearchOutcome.Available=>200,LearningClassroomAiSearchOutcome.InvalidRequest=>400,LearningClassroomAiSearchOutcome.Denied=>403,_=>503};
            context.Response.ContentType="application/json; charset=utf-8";
            await context.Response.WriteAsync(result.ToToolJson(),context.RequestAborted);
        }))).StartAsync();
        using var client=host.GetTestClient();using var response=await client.PostAsJsonAsync("/controlled-classroom-search",new{query="calculus"});
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var json=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("<script>",json);
        Assert.DoesNotContain(f.Authorization.Actor!.UserId.ToString(),json);Assert.DoesNotContain("groupId",json,StringComparison.OrdinalIgnoreCase);
        f.Authorization.Actor=null;using var denied=await client.PostAsJsonAsync("/controlled-classroom-search",new{query="calculus"});
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);Assert.DoesNotContain("untrusted title",await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ToolJsonEscapesReservedTextWithoutChangingAuthorizedSourceContent()
    {
        const string name="<script>& untrusted \"title\"</script>";
        var f=new Fixture(Eligible(name));var result=await f.Adapter.SearchAsync(AiModule.Learning,Query);
        var json=result.ToToolJson();Assert.DoesNotContain("<script>",json);Assert.DoesNotContain("&",json);
        using var document=JsonDocument.Parse(json);
        Assert.Equal(name,document.RootElement.GetProperty("classrooms")[0].GetProperty("displayName").GetString());
        Assert.Equal(name,f.Reader.Snapshot!.Classrooms[0].Group.Name);
    }
}
