using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.Learning.Activities;

namespace Andril.API.Extensions;

/// <summary>Uninvoked controlled endpoint composition. No production service registration or activation.</summary>
public static class ControlledLearningActivitiesEndpointExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Default };

    public static IEndpointRouteBuilder MapControlledLearningActivities(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/learning/activities").RequireAuthorization();
        group.MapPost("/{kind}", (LearningActivityKind kind, CreateLearningActivityRequest request,
            ILearningActivitiesRepository repository, CancellationToken ct) => Safe(() => repository.CreateAsync(kind, request, ct), ct));
        group.MapGet("/{kind}/{id:guid}", (LearningActivityKind kind, Guid id,
            ILearningActivitiesRepository repository, CancellationToken ct) => Safe(() => repository.ReadAsync(kind, id, ct), ct));
        group.MapGet("/{kind}", (LearningActivityKind kind, Guid? after, int? limit,
            ILearningActivitiesRepository repository, CancellationToken ct) => Safe(() => repository.ListAsync(kind, after, limit ?? 50, ct), ct));
        group.MapPost("/{kind}/{id:guid}/commands", (LearningActivityKind kind, Guid id, ChangeLearningActivityRequest request,
            ILearningActivitiesRepository repository, CancellationToken ct) => Safe(() => repository.ChangeAsync(kind, id, request, ct), ct));
        return endpoints;
    }

    private static async Task<IResult> Safe(Func<Task<LearningActivityResult>> action, CancellationToken ct)
    {
        try
        {
            var result = await action();
            var status = result.Outcome switch
            {
                LearningActivityOutcome.Available or LearningActivityOutcome.Saved => StatusCodes.Status200OK,
                LearningActivityOutcome.Denied => StatusCodes.Status403Forbidden,
                LearningActivityOutcome.Invalid => StatusCodes.Status400BadRequest,
                LearningActivityOutcome.NotFound => StatusCodes.Status404NotFound,
                LearningActivityOutcome.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status503ServiceUnavailable
            };
            return Results.Json(new { outcome = result.Outcome.ToString(), activity = status == 200 ? result.Activity : null,
                activities = status == 200 ? result.Activities : null }, Json, statusCode: status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Results.Json(new { outcome = "Unavailable" }, Json, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }
}
