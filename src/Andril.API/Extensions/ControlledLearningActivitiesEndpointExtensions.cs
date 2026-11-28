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
        group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";context.HttpContext.Response.Headers.Pragma="no-cache";return await next(context);});
        group.MapPost("/{kind}", (string kind,HttpRequest request,ILearningActivitiesRepository repository,CancellationToken ct) => Safe(async()=>
            await repository.CreateAsync(Kind(kind),await Read<CreateLearningActivityRequest>(request,ct),ct),ct)).Accepts<CreateLearningActivityRequest>("application/json");
        group.MapGet("/{kind}/{id:guid}", (string kind,Guid id,ILearningActivitiesRepository repository,CancellationToken ct) => Safe(()=>repository.ReadAsync(Kind(kind),id,ct),ct));
        group.MapGet("/{kind}", (string kind,HttpRequest request,ILearningActivitiesRepository repository,CancellationToken ct) => Safe(()=>
        {
            Guid? after=null;int limit=50;
            if(request.Query.TryGetValue("after",out var cursor)){if(!Guid.TryParse(cursor.ToString(),out var parsed))throw new ArgumentException();after=parsed;}
            if(request.Query.TryGetValue("limit",out var count)&&!int.TryParse(count.ToString(),out limit))throw new ArgumentException();
            return repository.ListAsync(Kind(kind),after,limit,ct);
        },ct));
        group.MapPost("/{kind}/{id:guid}/commands", (string kind,Guid id,HttpRequest request,ILearningActivitiesRepository repository,CancellationToken ct) => Safe(async()=>
            await repository.ChangeAsync(Kind(kind),id,await Read<ChangeLearningActivityRequest>(request,ct),ct),ct)).Accepts<ChangeLearningActivityRequest>("application/json");
        return endpoints;
    }

    private static LearningActivityKind Kind(string value)=>Enum.TryParse<LearningActivityKind>(value,true,out var kind)&&Enum.IsDefined(kind)
        ?kind:throw new ArgumentException();
    private static async Task<T> Read<T>(HttpRequest request,CancellationToken ct)
    {
        if(!request.HasJsonContentType())throw new ArgumentException();
        return await request.ReadFromJsonAsync<T>(ct)??throw new ArgumentException();
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
        catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException)
        { return Results.Json(new { outcome = "Invalid" }, Json, statusCode: StatusCodes.Status400BadRequest); }
        catch (Exception) { return Results.Json(new { outcome = "Unavailable" }, Json, statusCode: StatusCodes.Status503ServiceUnavailable); }
    }
}
