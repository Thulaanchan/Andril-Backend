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
        endpoints.MapGet("/api/learning/xp", async (Guid? after,int? limit,ILearningXpRepository repository,CancellationToken ct) =>
        {
            if (after == Guid.Empty || limit is < 1 or > 100) return Results.Json(new { outcome = "Invalid" },Json,statusCode:400);
            try
            {
                var result = await repository.ReadAsync(after,limit ?? 50,ct);
                return Results.Json(new { outcome = result.Outcome.ToString(), result.Total, result.Entries,result.Progress },Json,statusCode:result.Outcome switch
                { LearningXpOutcome.Available => 200, LearningXpOutcome.Denied => 403, _ => 503 });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return Results.Json(new { outcome = "Unavailable" },Json,statusCode:503); }
        }).RequireAuthorization();
        return endpoints;
    }
}
