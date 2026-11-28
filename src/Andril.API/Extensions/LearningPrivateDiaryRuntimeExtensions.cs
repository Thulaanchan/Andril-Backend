using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Diary;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

/// <summary>Opt-in Learning-only private Diary. No Hobbies/Social activation, publication or retention worker.</summary>
public static class LearningPrivateDiaryRuntimeExtensions
{
    private sealed record Enabled;
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ReflectionRequest(string? Title,string Body);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record PlanRequest(Guid Id,string Title,string? Description);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ContentRequest([property:JsonRequired] ReflectionRequest? Reflection,[property:JsonRequired] PlanRequest[] Plans);
    public static IServiceCollection AddVerifiedLearningPrivateDiary(this IServiceCollection services,IConfiguration configuration)
    {
        if(!configuration.GetValue<bool>("Learning:PrivateDiary:Enabled"))return services;
        var role=configuration["Learning:PrivateDiary:RuntimeRole"];
        if(string.IsNullOrWhiteSpace(role))throw new InvalidOperationException("Private Learning Diary requires a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<IModuleDiaryRepository>(sp=>LearningDiaryPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        return services;
    }
    public static WebApplication MapVerifiedLearningPrivateDiary(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is null)return app;
        var group=app.MapGroup("/api/learning/diary").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";context.HttpContext.Response.Headers.Pragma="no-cache";return await next(context);});
        group.MapGet("",(HttpRequest request,IModuleDiaryRepository store,CancellationToken ct)=>Safe(async()=>
        {var result=await store.CalendarAsync("learning",Date(request.Query["from"].ToString()),Date(request.Query["through"].ToString()),ct);return result.Dates is null?Results.StatusCode(Status(result.State)):Results.Json(new{dates=result.Dates});},ct));
        group.MapGet("/{date}",(string date,IModuleDiaryRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var result=await store.ReadAsync("learning",Date(date),ct);if(result.Entry is not {} entry)return Results.StatusCode(Status(result.State));
            return Results.Json(new{date=entry.Date,reflection=entry.Reflection is {} r?new ReflectionRequest(r.Title,r.Body):null,plans=entry.Plans.Select(p=>new PlanRequest(p.Id,p.Title,p.Description))});
        },ct));
        group.MapPost("/{date}",(string date,HttpRequest request,IModuleDiaryRepository store,IPrivateProfileService profiles,CancellationToken ct)=>Write(date,request,store,profiles,false,ct)).Accepts<ContentRequest>("application/json");
        group.MapPut("/{date}",(string date,HttpRequest request,IModuleDiaryRepository store,IPrivateProfileService profiles,CancellationToken ct)=>Write(date,request,store,profiles,true,ct)).Accepts<ContentRequest>("application/json");
        group.MapDelete("/{date}",(string date,IModuleDiaryRepository store,CancellationToken ct)=>Safe(async()=>Results.StatusCode(Status(await store.DeleteAsync("learning",Date(date),ct))),ct));
        return app;
    }
    private static Task<IResult> Write(string date,HttpRequest request,IModuleDiaryRepository store,IPrivateProfileService profiles,bool edit,CancellationToken ct)=>Safe(async()=>
    {
        if(!request.HasJsonContentType())return Results.StatusCode(400);
        var body=await request.ReadFromJsonAsync<ContentRequest>(ct);if(body?.Plans is null||body.Plans.Any(p=>p is null))return Results.StatusCode(400);
        // This preliminary owner reference is server-derived, never request-bound. The repository
        // freshly verifies that exact owner and lifecycle again in the shared write transaction.
        var owner=await profiles.GetAsync("learning",ct);if(owner.Module!="learning"||owner.ProfileId==Guid.Empty)return Results.StatusCode(403);
        var entry=new ModuleDiaryEntry(owner.ProfileId,"learning",Date(date));
        if(body.Reflection is {} reflection)entry.SetReflection(reflection.Title,reflection.Body);
        foreach(var plan in body.Plans)entry.AddPlan(new(plan.Id,plan.Title,plan.Description));
        return Results.StatusCode(Status(edit?await store.EditAsync(entry,ct):await store.CreateAsync(entry,ct)));
    },ct);
    private static DateOnly Date(string value)=>DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
        ?date:throw new ArgumentException("A literal ISO calendar date is required.");
    private static int Status(DiaryReadState state)=>state switch{DiaryReadState.Available=>200,DiaryReadState.NotFound=>404,DiaryReadState.Denied=>403,_=>503};
    private static int Status(DiaryWriteOutcome state)=>state switch{DiaryWriteOutcome.Committed=>200,DiaryWriteOutcome.NotFound=>404,DiaryWriteOutcome.Denied=>403,DiaryWriteOutcome.Conflict=>409,_=>503};
    private static async Task<IResult> Safe(Func<Task<IResult>> action,CancellationToken ct)
    {
        try{return await action();}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is ProfileAccessDeniedException or ProfileMissingException){return Results.StatusCode(403);}
        catch(Exception e)when(e is ArgumentException or JsonException or BadHttpRequestException){return Results.StatusCode(400);}
        catch(Exception){return Results.StatusCode(503);}
    }
}
