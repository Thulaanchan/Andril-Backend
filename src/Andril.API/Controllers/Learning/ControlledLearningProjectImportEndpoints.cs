using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Learning.CV;

namespace Andril.API.Controllers.Learning;

/// <summary>Explicit controlled-host API only. No MVC discovery, Program registration or
/// production authority. Reuses existing api/v1/private problem conventions and server auth.</summary>
public static class ControlledLearningProjectImportEndpoints
{
    public const string Root = "/api/v1/learning/cv-imports";
    public static void MapControlledLearningProjectImport(this WebApplication app)
    {
        if (!app.Environment.IsEnvironment("Testing") || !app.Configuration.GetValue<bool>("Learning:ControlledTestHost"))
            throw new InvalidOperationException("Learning HTTP integration requires an explicitly controlled test host.");
        MapLearningProjectImportRoutes(app);
    }
    internal static void MapLearningProjectImportRoutes(WebApplication app)
    {
        var group = app.MapGroup(Root).RequireAuthorization();
        group.MapPost("/reviews", Review).WithName("ControlledLearningProjectImportReview")
            .Accepts<ProjectImportReviewRequest>("application/json").Produces<ProjectImportReviewResponse>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        group.MapPost("/{reviewId:guid}/confirm", Confirm).WithName("ControlledLearningProjectImportConfirm")
            .Accepts<ProjectImportConfirmRequest>("application/json").Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
    }

    private static async Task<IResult> Review(HttpContext context, LearningProjectImportApplicationService service, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var request = await context.Request.ReadFromJsonAsync<ProjectImportReviewRequest>(context.RequestAborted);
            if (request is null || request.ProjectIds is null) return Failure(400);
            var result = await service.ReviewAsync(request.PortfolioId, request.CvId, request.ProjectIds, context.RequestAborted);
            if (result.State != ProjectImportState.Available || result.Review is null) return Outcome(result.State);
            var review = result.Review;
            return Results.Ok(new ProjectImportReviewResponse(review.Id, review.PortfolioId, review.CvId,
                review.Projects.Select(p => new ProjectImportReviewedProjectResponse(p.Id, p.Title, p.Description)).ToArray()));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or ArgumentException) { return Failure(400); }
        catch (Exception) { logs.CreateLogger("ControlledLearningProjectImport").LogWarning(3901, "Controlled Learning import dependency unavailable."); return Failure(503); }
    }
    private static async Task<IResult> Confirm(Guid reviewId, HttpContext context, LearningProjectImportApplicationService service, ILoggerFactory logs)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var request = await context.Request.ReadFromJsonAsync<ProjectImportConfirmRequest>(context.RequestAborted);
            if (request is null) return Failure(400);
            var result = await service.ConfirmAsync(reviewId, request.Confirmed, context.RequestAborted);
            return result.State == ProjectImportState.Committed ? Results.NoContent() : Outcome(result.State);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or ArgumentException) { return Failure(400); }
        catch (Exception) { logs.CreateLogger("ControlledLearningProjectImport").LogWarning(3901, "Controlled Learning import dependency unavailable."); return Failure(503); }
    }
    private static IResult Outcome(ProjectImportState state) => Failure(state switch
    {
        ProjectImportState.ConfirmationRequired or ProjectImportState.InvalidSelection => 400,
        ProjectImportState.Denied => 403,
        ProjectImportState.NotFound => 404,
        ProjectImportState.StaleReview or ProjectImportState.Duplicate or ProjectImportState.Conflict => 409,
        _ => 503
    });
    private static IResult Failure(int status) => Results.Problem(statusCode: status, title: "Controlled Learning import could not be completed.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectImportReviewRequest(Guid PortfolioId, Guid CvId, Guid[] ProjectIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectImportConfirmRequest(bool Confirmed);
public sealed record ProjectImportReviewedProjectResponse(Guid ProjectId, string? Title, string? Description)
{ public override string ToString() => "Private reviewed project response [content omitted]"; }
public sealed record ProjectImportReviewResponse(Guid ReviewId, Guid PortfolioId, Guid CvId, ProjectImportReviewedProjectResponse[] Projects)
{ public override string ToString() => "Private import review response [identities and content omitted]"; }
