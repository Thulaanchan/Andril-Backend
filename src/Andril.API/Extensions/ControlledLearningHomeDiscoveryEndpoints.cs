using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Integrations.Learning;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;

namespace Andril.API.Extensions;

/// <summary>Explicitly uninvoked endpoint composition. Authorities/repositories require separate verified registration.</summary>
public static class ControlledLearningHomeDiscoveryEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Default };
    public static IEndpointRouteBuilder MapControlledLearningHomeDiscovery(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/learning").RequireAuthorization();
        group.MapGet("/home", (LearningHomeApplicationService home, CancellationToken ct) => Safe(async () =>
        {
            var result = await home.ReadAsync(ct);
            return Results.Json(result, Json, statusCode: result.State == LearningSetupReadState.Available ? 200 : result.State == LearningSetupReadState.Denied ? 403 : 503);
        }, ct));
        group.MapGet("/people", (string? query, int? limit, LearningPeopleDiscoveryService people, CancellationToken ct) => Safe(async () =>
        {
            var result = await people.SearchAsync(query, limit ?? 50, ct);
            return Results.Json(result, Json, statusCode: result.Outcome switch { LearningPeopleOutcome.Available => 200, LearningPeopleOutcome.Denied => 403, LearningPeopleOutcome.Invalid => 400, _ => 503 });
        }, ct));
        group.MapGet("/classrooms", (string? query, int? limit, LearningClassroomDiscoveryService classrooms, CancellationToken ct) => Safe(async () =>
        {
            var result = await classrooms.SearchAsync(query, limit ?? 50, ct);
            return Results.Json(result, Json, statusCode: result.Outcome switch { "Available" => 200, "Denied" => 403, "Invalid" => 400, _ => 503 });
        }, ct));
        return endpoints;
    }
    private static async Task<IResult> Safe(Func<Task<IResult>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Json(new { outcome = "Unavailable" }, Json, statusCode: 503); }
    }
}
