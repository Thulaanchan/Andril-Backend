using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Persistence.Repositories.Groups;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Andril.IntegrationTests.Database;

public sealed class LearningClassroomRuntimePostgresTests(ITestOutputHelper output)
{
    private const string Search="/api/learning/classrooms?query=Needle&limit=20";
    private static readonly GroupModuleScope Scope=new("learning",GroupKind.Classroom);
    private static HttpClient Client(SharedGroupsHttpHost server,SharedGroupsDatabase.Actor? actor=null,bool confirmed=true)
    {
        var client=server.CreateClient(new(){BaseAddress=new Uri("https://localhost")});
        if(actor is not null)client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(confirmed?"confirmed-":"unconfirmed-")+actor.Subject);
        return client;
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response,HttpStatusCode expected)
    {
        Assert.Equal(expected,response.StatusCode);
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString());
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return json.RootElement.Clone();
    }
    [LocalPostgresFact]
    public async Task ProductionRouteRequiresExplicitEnablementAndConfirmedCurrentLearningIdentity()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var owner=await f.SeedAsync("learning");
        using(var disabled=new SharedGroupsHttpHost(f))using(var client=Client(disabled,owner))
            Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(Search)).StatusCode);
        using var server=new SharedGroupsHttpHost(f,learningClassrooms:true);
        using(var anonymous=Client(server))Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(Search)).StatusCode);
        using(var unconfirmed=Client(server,owner,false))await Read(await unconfirmed.GetAsync(Search),HttpStatusCode.Forbidden);
        using var active=Client(server,owner);var result=await Read(await active.GetAsync(Search),HttpStatusCode.OK);
        Assert.Equal("Available",result.GetProperty("outcome").GetString());Assert.Empty(result.GetProperty("classrooms").EnumerateArray());
        var inactive=await f.SeedAsync("learning",active:false);using var denied=Client(server,inactive);
        Assert.Equal(HttpStatusCode.Forbidden,(await denied.GetAsync(Search)).StatusCode);
    }
    [LocalPostgresFact]
    public async Task ProductionReaderUsesM5VisibilityAndCannotBeRedirectedToAnotherModuleOrActor()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var caller=await f.SeedAsync("learning");var host=await f.SeedAsync("learning");
        await f.GrantEducatorAsync(host);
        var visible=await f.CreateGroupAsync(host,Scope,new("Needle Classroom",null,true,"public"));
        await f.CreateGroupAsync(host,Scope,new("Needle Private",null,true));
        await f.CreateGroupAsync(host,Scope,new("Needle Unlisted",null,false,"public"));
        var hobby=await f.SeedAsync("hobbies");await f.CreateGroupAsync(hobby,new("hobbies",GroupKind.Club),new("Needle Club",null,true,"public"));
        using var server=new SharedGroupsHttpHost(f,hobbies:true,learningClassrooms:true);using var client=Client(server,caller);
        var count=await f.CountAsync("groups");
        var callerActive=await ProbeLifecycle(f,caller,caller.Profile);
        var targetVisibleToCaller=await ProbeLifecycle(f,caller,host.Profile);
        var targetActiveAsOwner=await ProbeLifecycle(f,host,host.Profile);
        output.WriteLine($"M2_LIFECYCLE_SCOPE: caller_active={callerActive}; target_active_under_caller_scope={targetVisibleToCaller}; target_active_under_owner_scope={targetActiveAsOwner}");
        Assert.True(callerActive);Assert.False(targetVisibleToCaller);Assert.True(targetActiveAsOwner);
        var result=await Read(await client.GetAsync(Search+"&module=hobbies&actorProfileId="+host.Profile),HttpStatusCode.OK);
        var card=Assert.Single(result.GetProperty("classrooms").EnumerateArray());
        Assert.Equal(visible.Id,card.GetProperty("id").GetGuid());Assert.Equal("learning",card.GetProperty("module").GetString());
        Assert.Equal(new[]{"groupKind","id","module","name"},card.EnumerateObject().Select(x=>x.Name).Order().ToArray());
        Assert.Equal(count,await f.CountAsync("groups"));
        await f.WithEntriesAsync(host,repository=>repository.UpdateAsync(visible.Id,Scope,new("Needle Classroom",null,false,"public"),visible.Revision));
        Assert.Empty((await Read(await client.GetAsync(Search),HttpStatusCode.OK)).GetProperty("classrooms").EnumerateArray());
        using var wrongModule=Client(server,hobby);Assert.Equal(HttpStatusCode.Forbidden,(await wrongModule.GetAsync(Search)).StatusCode);
    }
    [LocalPostgresFact]
    public async Task BlockingAndLifecycleRevocationApplyToEveryProductionRead()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var caller=await f.SeedAsync("learning");var teacher=await f.SeedAsync("learning");
        await f.GrantEducatorAsync(teacher);await f.CreateGroupAsync(teacher,Scope,new("Needle Classroom",null,true,"public"));
        using var server=new SharedGroupsHttpHost(f,learningClassrooms:true);using var client=Client(server,caller);
        Assert.Single((await Read(await client.GetAsync(Search),HttpStatusCode.OK)).GetProperty("classrooms").EnumerateArray());
        var writer=new PostgresGroupAuthorityWriter(f.Options(),teacher,teacher,f.Role);
        await writer.BlockAsync("learning",caller.Profile,true);
        Assert.Empty((await Read(await client.GetAsync(Search),HttpStatusCode.OK)).GetProperty("classrooms").EnumerateArray());
        await writer.BlockAsync("learning",caller.Profile,false);
        Assert.Single((await Read(await client.GetAsync(Search),HttpStatusCode.OK)).GetProperty("classrooms").EnumerateArray());
        await using var db=f.Database.OpenAccounts();
        await using(var tx=await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{caller.Account.ToString()},true),set_config('andril.subject',{caller.Subject.ToString()},true),set_config('andril.module','learning',true)");
            Assert.Equal(1,await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_identity.account_module_lifecycles SET state='inactive', revision=revision+1 WHERE account_id={caller.Account} AND module='learning'"));
            await tx.CommitAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(Search)).StatusCode);
        Assert.Equal(1,await f.CountAsync("groups"));
    }
    [LocalPostgresFact]
    public async Task ProductionSearchRejectsInvalidArgumentsAndNeverAcceptsWrites()
    {
        await using var isolated=await LearningEligibilityDatabase.CreateAsync();var f=isolated.Groups;var caller=await f.SeedAsync("learning");
        using var server=new SharedGroupsHttpHost(f,learningClassrooms:true);using var client=Client(server,caller);
        foreach(var suffix in new[]{"?query=","?query=Needle&limit=0","?query=Needle&limit=101","?query="+new string('x',501),"?query=%00"})
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/learning/classrooms"+suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,(await client.PostAsync(Search,null)).StatusCode);
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.GetAsync(Search,cancel.Token));
        Assert.Equal(0,await f.CountAsync("groups"));
    }
    private static async Task<bool> ProbeLifecycle(SharedGroupsDatabase f,SharedGroupsDatabase.Actor actor,Guid candidate)
    {
        // Diagnostic fixture-owner connection: observe the existing function under
        // each legitimate M2 scope; no runtime grants, policies or assertions change.
        await using var db=f.Database.OpenAccounts();await using var tx=await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{actor.Account.ToString()},true),set_config('andril.subject',{actor.Subject.ToString()},true),set_config('andril.module','learning',true)");
        return await db.Database.SqlQuery<bool>($"SELECT andril_groups.profile_is_active({candidate}, 'learning') AS \"Value\"").SingleAsync();
    }
}
