using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Discovery;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

public static class LearningPublicationRuntimeExtensions
{
    private sealed record Enabled;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public static IServiceCollection AddVerifiedLearningPublications(this IServiceCollection services,IConfiguration configuration)
    {
        if(!configuration.GetValue<bool>("Learning:Publications:Enabled"))return services;
        var role=configuration["Learning:Publications:RuntimeRole"];if(string.IsNullOrWhiteSpace(role))throw new InvalidOperationException("Learning publications require a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningPublicationRepository>(sp=>LearningPublicationPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        services.AddScoped<LearningPeopleDiscoveryService>(sp=>LearningPublicationPersistence.CreatePeople(sp.GetRequiredService<AccountsDbContext>(),sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),sp.GetRequiredService<IPolicyCatalogProvider>(),role));return services;
    }
    public static WebApplication MapVerifiedLearningPublications(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is null)return app;
        var group=app.MapGroup("/api/learning").RequireAuthorization();
        group.MapGet("/disclosure",(HttpContext ctx,ILearningPublicationRepository store,CancellationToken ct)=>Safe(ctx,async()=>Publication(await store.ReadAsync(LearningPublicationKind.People,cancellationToken:ct)),ct));
        group.MapPut("/disclosure",(HttpContext ctx,ILearningPublicationRepository store,CancellationToken ct)=>Safe(ctx,async()=>{
            var change=await JsonSerializer.DeserializeAsync<LearningDisclosureChange>(ctx.Request.Body,Json,ct);return change is null?Invalid():Publication(await store.SetDisclosureAsync(change,ct));},ct));
        group.MapGet("/mentorship-offers/{offerId}/publication",(string offerId,HttpContext ctx,ILearningPublicationRepository store,CancellationToken ct)=>Safe(ctx,async()=>Guid.TryParse(offerId,out var id)&&id!=Guid.Empty?Publication(await store.ReadAsync(LearningPublicationKind.MentorshipOffer,id,ct)):Invalid(),ct));
        group.MapPut("/mentorship-offers/{offerId}/publication",(string offerId,HttpContext ctx,ILearningPublicationRepository store,CancellationToken ct)=>Safe(ctx,async()=>{
            if(!Guid.TryParse(offerId,out var id)||id==Guid.Empty)return Invalid();var change=await JsonSerializer.DeserializeAsync<LearningOfferPublicationChange>(ctx.Request.Body,Json,ct);return change is null?Invalid():Publication(await store.SetOfferAsync(id,change,ct));},ct));
        group.MapGet("/people",(HttpContext ctx,LearningPeopleDiscoveryService people,CancellationToken ct)=>Safe(ctx,async()=>{
            int limit=50;if(ctx.Request.Query.TryGetValue("limit",out var value)&&!int.TryParse(value.ToString(),out limit))return Invalid();
            var result=await people.SearchAsync(ctx.Request.Query["query"].ToString(),limit,ct);
            return Results.Json(new{outcome=result.Outcome.ToString(),people=result.Outcome==LearningPeopleOutcome.Available?result.People:null},Json,statusCode:result.Outcome switch{LearningPeopleOutcome.Available=>200,LearningPeopleOutcome.Invalid=>400,LearningPeopleOutcome.Denied=>403,_=>503});},ct));
        return app;
    }
    private static IResult Invalid()=>Results.Json(new{outcome="Invalid"},Json,statusCode:400);
    private static IResult Publication(LearningPublicationResult result)=>Results.Json(new{outcome=result.Outcome.ToString(),publication=result.Publication},Json,statusCode:result.Outcome switch{LearningPublicationOutcome.Available or LearningPublicationOutcome.Saved=>200,LearningPublicationOutcome.Denied=>403,LearningPublicationOutcome.Invalid=>400,LearningPublicationOutcome.Conflict=>409,LearningPublicationOutcome.NotFound=>404,_=>503});
    private static async Task<IResult> Safe(HttpContext context,Func<Task<IResult>> action,CancellationToken ct)
    {
        context.Response.Headers.CacheControl="no-store";context.Response.Headers.Pragma="no-cache";
        try{return await action();}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(JsonException){return Invalid();}catch(Exception){return Results.Json(new{outcome="Unavailable"},Json,statusCode:503);}
    }
}
