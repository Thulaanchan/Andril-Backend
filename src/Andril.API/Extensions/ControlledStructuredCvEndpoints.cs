using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Learning.CV;

namespace Andril.API.Extensions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedCvEntryRequest(Guid Id, Dictionary<string,string> Fields);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedCvSectionRequest(ReviewedCvEntryRequest[] Entries);
/// <summary>Uninvoked private CV composition; repository owns current identity/consent release coordination.</summary>
public static class ControlledStructuredCvEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Default };
    public static IEndpointRouteBuilder MapControlledStructuredCv(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/learning/cvs").RequireAuthorization();
        g.MapPost("/{id:guid}/sections/{category}/structured",(Guid id,CvSectionCategory category,ReviewedCvSectionRequest request,ILearningCvRepository repository,CancellationToken ct) => Safe(async () =>
        {
            if (!Enum.IsDefined(category) || request.Entries is null || request.Entries.Any(x => x is null || x.Fields is null)) return Results.StatusCode(400);
            var current = await repository.OpenAsync(id,ct); if (current.Cv is null) return Results.StatusCode(Status(current.State));
            var section = current.Cv.GetSection(category);
            // Explicit reviewed edits do not discard existing legacy/imported entries or fields.
            var replacements = request.Entries.Select(e => CvStructuredFields.ReviewedEntry(e.Id,section.Id,category,e.Fields)).ToArray();
            if (replacements.Select(x => x.Id).Distinct().Count() != replacements.Length) return Results.StatusCode(400);
            // Merge is performed against the current persisted document inside SaveAsync.
            var saved = await repository.SaveAsync(id,CvSaveChange.ReviewedFields(category,replacements),ct);
            return Results.Json(new { outcome = saved.State.ToString() },Json,statusCode:Status(saved.State));
        },ct));
        g.MapGet("/{id:guid}/preview",(Guid id,ILearningCvRepository repository,CancellationToken ct) => Safe(async () =>
        {
            var result = await repository.PreviewAsync(id,ct);
            return result.Preview is null ? Results.StatusCode(Status(result.State)) : Results.Content(result.Preview.Html,"text/html; charset=utf-8");
        },ct));
        g.MapGet("/{id:guid}/pdf",(Guid id,ILearningCvRepository repository,CancellationToken ct) => Safe(async () =>
        {
            var result = await repository.ExportPdfAsync(id,ct);
            return result.Document is null ? Results.StatusCode(Status(result.State)) : Results.File(result.Document.Bytes,"application/pdf");
        },ct));
        return endpoints;
    }
    private static int Status(CvOperationState state) => state switch { CvOperationState.Available => 200, CvOperationState.Denied => 403, CvOperationState.NotFound => 404, CvOperationState.Conflict => 409, _ => 503 };
    private static async Task<IResult> Safe(Func<Task<IResult>> action,CancellationToken ct)
    {
        try { return await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException) { return Results.StatusCode(400); }
        catch (Exception) { return Results.StatusCode(503); }
    }
}
