using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

/// <summary>Explicit private-owner Portfolio composition. No public sharing, fetching or startup migrations.</summary>
public static class LearningPrivatePortfolioRuntimeExtensions
{
    private sealed record Enabled;
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TemplateRequest(PortfolioTemplate? Template);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ProjectRequest(string? Title,string? Description,string[]? MediaReferences,string[]? ExternalLinks);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record OrderRequest(Guid[]? Order);
    public static IServiceCollection AddVerifiedLearningPrivatePortfolios(this IServiceCollection services,IConfiguration configuration)
    {
        if(!configuration.GetValue<bool>("Learning:PrivatePortfolios:Enabled"))return services;
        var role=configuration["Learning:PrivatePortfolios:RuntimeRole"];
        if(string.IsNullOrWhiteSpace(role))throw new InvalidOperationException("Private Learning Portfolios require a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningPortfolioRepository>(sp=>LearningPortfolioPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        return services;
    }
    public static WebApplication MapVerifiedLearningPrivatePortfolios(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is null)return app;
        var group=app.MapGroup("/api/learning/portfolios").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";context.HttpContext.Response.Headers.Pragma="no-cache";return await next(context);});
        group.MapPost("",(HttpRequest request,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var body=await Read<TemplateRequest>(request,ct);if(body?.Template is not {} template||!Enum.IsDefined(template))return Results.StatusCode(400);
            var result=await store.CreateAsync(template,ct);return result.Portfolio is null?Results.StatusCode(Status(result.State)):Results.Json(new{id=result.Portfolio.Id});
        },ct)).Accepts<TemplateRequest>("application/json");
        group.MapGet("",(ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {var result=await store.ListAsync(ct);return result.Ids is null?Results.StatusCode(Status(result.State)):Results.Json(new{ids=result.Ids});},ct));
        group.MapGet("/{id:guid}",(Guid id,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var result=await store.OpenAsync(id,ct);return result.Portfolio is not {} portfolio?Results.StatusCode(Status(result.State)):Results.Json(new
            {portfolio.Id,portfolio.Template,projects=portfolio.Projects.Select(p=>new{p.Id,p.Title,p.Description,p.MediaReferences,p.ExternalLinks})});
        },ct));
        group.MapGet("/{id:guid}/preview",(Guid id,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {var result=await store.PreviewAsync(id,ct);return result.Preview is null?Results.StatusCode(Status(result.State)):Results.Content(result.Preview.Html,"text/html; charset=utf-8");},ct));
        group.MapPut("/{id:guid}/template",(Guid id,HttpRequest request,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var body=await Read<TemplateRequest>(request,ct);if(body?.Template is not {} template||!Enum.IsDefined(template))return Results.StatusCode(400);
            return Results.StatusCode(Status((await store.SaveAsync(id,PortfolioSaveChange.Template(template),ct)).State));
        },ct)).Accepts<TemplateRequest>("application/json");
        group.MapPost("/{id:guid}/projects",(Guid id,HttpRequest request,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var body=await Read<ProjectRequest>(request,ct);if(body is null)return Results.StatusCode(400);var project=Guid.NewGuid();
            var result=await store.SaveAsync(id,PortfolioSaveChange.Add(new(project,body.Title,body.Description,body.MediaReferences,body.ExternalLinks)),ct);
            return result.State==PortfolioOperationState.Available?Results.Json(new{id=project}):Results.StatusCode(Status(result.State));
        },ct)).Accepts<ProjectRequest>("application/json");
        group.MapPut("/{id:guid}/projects/{projectId:guid}",(Guid id,Guid projectId,HttpRequest request,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var body=await Read<ProjectRequest>(request,ct);if(body is null)return Results.StatusCode(400);
            return Results.StatusCode(Status((await store.SaveAsync(id,PortfolioSaveChange.Edit(projectId,body.Title,body.Description,body.MediaReferences,body.ExternalLinks),ct)).State));
        },ct)).Accepts<ProjectRequest>("application/json");
        group.MapPut("/{id:guid}/project-order",(Guid id,HttpRequest request,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>
        {
            var body=await Read<OrderRequest>(request,ct);if(body?.Order is null)return Results.StatusCode(400);
            return Results.StatusCode(Status((await store.SaveAsync(id,PortfolioSaveChange.Reorder(body.Order),ct)).State));
        },ct)).Accepts<OrderRequest>("application/json");
        group.MapDelete("/{id:guid}",(Guid id,ILearningPortfolioRepository store,CancellationToken ct)=>Safe(async()=>Results.StatusCode(Status((await store.DeleteAsync(id,ct)).State)),ct));
        return app;
    }
    private static async Task<T?> Read<T>(HttpRequest request,CancellationToken ct)=>request.HasJsonContentType()?await request.ReadFromJsonAsync<T>(ct):default;
    private static int Status(PortfolioOperationState state)=>state switch{PortfolioOperationState.Available=>200,PortfolioOperationState.NotFound=>404,PortfolioOperationState.Denied=>403,PortfolioOperationState.Conflict=>409,_=>503};
    private static async Task<IResult> Safe(Func<Task<IResult>> action,CancellationToken ct)
    {
        try{return await action();}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is ArgumentException or JsonException or BadHttpRequestException){return Results.StatusCode(400);}
        catch(Exception){return Results.StatusCode(503);}
    }
}
