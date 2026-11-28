using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.Learning.Progress;

namespace Andril.API.Extensions;

/// <summary>Explicitly uninvoked private read endpoint. No public/client XP-award or conversion route.</summary>
public static class ControlledLearningXpEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Default };
    public static IEndpointRouteBuilder MapControlledLearningXp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/learning/xp", async (HttpContext context,ILearningXpRepository repository,CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl="no-store";context.Response.Headers.Pragma="no-cache";
            Guid? after=null;int limit=50;
            if(context.Request.Query.TryGetValue("after",out var cursor))
            {if(!Guid.TryParse(cursor.ToString(),out var id)||id==Guid.Empty)return Results.Json(new{outcome="Invalid"},Json,statusCode:400);after=id;}
            if(context.Request.Query.TryGetValue("limit",out var page)&&!int.TryParse(page.ToString(),out limit)||limit is <1 or >100)
                return Results.Json(new{outcome="Invalid"},Json,statusCode:400);
            try
            {
                var result = await repository.ReadAsync(after,limit,ct);var available=result.Outcome==LearningXpOutcome.Available;
                return Results.Json(new { outcome = result.Outcome.ToString(), Total=available?result.Total:null, Entries=available?result.Entries:null,Progress=available?result.Progress:null },Json,statusCode:result.Outcome switch
                { LearningXpOutcome.Available => 200, LearningXpOutcome.Denied => 403, _ => 503 });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Json(new { outcome = "Unavailable" },Json,statusCode:503); }
        }).RequireAuthorization();
        return endpoints;
    }
}
