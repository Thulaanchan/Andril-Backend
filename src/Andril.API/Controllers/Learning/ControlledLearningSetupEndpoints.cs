using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Microsoft.OpenApi;

namespace Andril.API.Controllers.Learning;

// Deliberately not a controller: no automatic MVC discovery or production startup registration.
// Transport follows M2's api/v1, expectedRevision, Idempotency-Key, 204/400/403/409/503 and
// sanitized problem conventions. No account/profile/module actor fields are accepted.
public static class ControlledLearningSetupEndpoints
{
    public const string Root = "/api/v1/learning/setup";

    /// <summary>API-owned registration keeps OpenAPI source generation in the Web project.
    /// Explicit controlled-host composition only; production Program remains unchanged.</summary>
    public static IServiceCollection AddControlledLearningSetupOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                const string scheme = "supabaseBearer";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[scheme] = new OpenApiSecurityScheme
                { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] }];
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (context.Description.RelativePath?.StartsWith(Root.TrimStart('/'), StringComparison.Ordinal) == true
                    && context.Description.HttpMethod != "GET")
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                        Description = "Mutation intent identity only; never ownership or authorization. No automatic replay.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                    });
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }
    public static void MapControlledLearningSetup(this WebApplication app)
    {
        if (!app.Environment.IsEnvironment("Testing") || !app.Configuration.GetValue<bool>("Learning:ControlledTestHost"))
            throw new InvalidOperationException("Learning HTTP integration requires an explicitly controlled test host.");
        var group = app.MapGroup(Root).RequireAuthorization();
        group.MapGet("", Read).WithName("ControlledLearningSetupRead")
            .Produces<LearningSetupResponse>().ProducesProblem(403).ProducesProblem(503);
        group.MapPut("/selections", UpdateSelections).WithName("ControlledLearningSetupSelections")
            .Accepts<LearningSelectionUpdateRequest>("application/json").Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapPost("/goals", CreateGoal).WithName("ControlledLearningSetupGoalCreate")
            .Accepts<LearningGoalUpdateRequest>("application/json").Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapPut("/goals/{goalId:guid}", EditGoal).WithName("ControlledLearningSetupGoalEdit")
            .Accepts<LearningGoalUpdateRequest>("application/json").Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
        group.MapDelete("/goals/{goalId:guid}", RemoveGoal).WithName("ControlledLearningSetupGoalRemove")
            .Accepts<LearningRevisionRequest>("application/json").Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409).ProducesProblem(503);
    }

    private static async Task<IResult> Read(HttpContext context, LearningPreferencesApplicationService service, CancellationToken ct) =>
        await Safe(context, async () =>
        {
            var read = await service.ReadAsync(ct);
            if (read.State != LearningSetupReadState.Available) return Problem(read.State == LearningSetupReadState.Denied ? 403 : 503);
            if (read.Snapshot is null || !LearningSetupRevision.TryParse(read.StorageRevision, out _)) return Problem(503);
            return Results.Ok(new LearningSetupResponse(read.StorageRevision!,
                read.Snapshot.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Teaching).Select(s => s.SkillId).ToArray(),
                read.Snapshot.Selections.Where(s => s.Kind == LearningSkillSelectionKind.Learning).Select(s => s.SkillId).ToArray(),
                read.Snapshot.Goals.Select(g => new LearningGoalResponse(g.Id, g.Text)).ToArray()));
        });

    private static async Task<IResult> UpdateSelections(HttpContext context, LearningPreferencesApplicationService service, CancellationToken ct) =>
        await Safe(context, async () =>
        {
            var operation = Operation(context);
            var fields = await Fields(context, ["expectedRevision", "teachingSkills", "learningSkills"], ct);
            var expected = Revision(fields);
            return Outcome(await service.UpdateSelectionsWithOperationAsync(operation,
                Skills(fields["teachingSkills"]), Skills(fields["learningSkills"]), expected, ct));
        });

    private static Task<IResult> CreateGoal(HttpContext context, LearningPreferencesApplicationService service, CancellationToken ct) =>
        Goal(context, service, LearningGoalChangeKind.Create, null, ct);
    private static Task<IResult> EditGoal(Guid goalId, HttpContext context, LearningPreferencesApplicationService service, CancellationToken ct) =>
        Goal(context, service, LearningGoalChangeKind.Edit, goalId, ct);
    private static Task<IResult> RemoveGoal(Guid goalId, HttpContext context, LearningPreferencesApplicationService service, CancellationToken ct) =>
        Goal(context, service, LearningGoalChangeKind.Remove, goalId, ct);

    private static async Task<IResult> Goal(HttpContext context, LearningPreferencesApplicationService service,
        LearningGoalChangeKind kind, Guid? id, CancellationToken ct) => await Safe(context, async () =>
    {
        var operation = Operation(context);
        var fields = await Fields(context, kind == LearningGoalChangeKind.Remove ? ["expectedRevision"] : ["expectedRevision", "text"], ct);
        var expected = Revision(fields);
        if (kind != LearningGoalChangeKind.Remove && fields["text"].ValueKind != JsonValueKind.String)
            throw new ArgumentException("Invalid request.");
        var text = kind == LearningGoalChangeKind.Remove ? null : fields["text"].GetString();
        var change = kind switch
        {
            LearningGoalChangeKind.Create => LearningGoalChange.Create(text!),
            LearningGoalChangeKind.Edit => LearningGoalChange.Edit(id!.Value, text!),
            _ => LearningGoalChange.Remove(id!.Value)
        };
        return Outcome(await service.ChangeGoalAsync(operation, change, expected, ct));
    });

    private static async Task<Dictionary<string, JsonElement>> Fields(HttpContext context, string[] allowed, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType()) throw new ArgumentException("Invalid request.");
        // Use M2's existing infrastructure body bound, not a selection/text business limit.
        const int limit = 16384;
        if (context.Request.ContentLength > limit) throw new ArgumentException("Invalid request.");
        using var body = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + count > limit) throw new ArgumentException("Invalid request.");
            await body.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        using var document = JsonDocument.Parse(body.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid request.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !fields.TryAdd(field.Name, field.Value.Clone()))
                throw new ArgumentException("Invalid request.");
        if (fields.Count != allowed.Length) throw new ArgumentException("Invalid request.");
        return fields;
    }

    private static Guid Operation(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        return values.Count == 1 && Guid.TryParseExact(values[0], "D", out var value) && value != Guid.Empty
            ? value : throw new ArgumentException("Invalid request.");
    }
    private static string Revision(Dictionary<string, JsonElement> fields) =>
        fields["expectedRevision"].ValueKind == JsonValueKind.String && LearningSetupRevision.TryParse(fields["expectedRevision"].GetString(), out var r)
            ? r!.ToOpaqueToken() : throw new ArgumentException("Invalid request.");
    private static Guid[] Skills(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Invalid request.");
        return value.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String && element.TryGetGuid(out var id)
            ? id : throw new ArgumentException("Invalid request.")).ToArray();
    }
    private static IResult Outcome(LearningSetupWriteOutcome outcome) => outcome switch
    {
        LearningSetupWriteOutcome.Committed => Results.NoContent(),
        LearningSetupWriteOutcome.Denied => Problem(403),
        LearningSetupWriteOutcome.Conflict => Problem(409),
        _ => Problem(503) // Includes CommitUnknown: never claim success or replay a mutation.
    };
    private static IResult Problem(int status) => Results.Problem(statusCode: status, title: status switch
    { 400 => "Invalid request", 403 => "Access denied", 409 => "Conflict", _ => "Service unavailable" });
    private static async Task<IResult> Safe(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { context.Abort(); return Results.Empty; }
        catch (Exception error) when (error is ArgumentException or JsonException or FormatException)
        { return Problem(400); }
        catch (Exception) { return Problem(503); } // Never serialize/log private exception payloads.
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningSelectionUpdateRequest(string ExpectedRevision, Guid[] TeachingSkills, Guid[] LearningSkills);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningGoalUpdateRequest(string ExpectedRevision, string Text);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningRevisionRequest(string ExpectedRevision);
public sealed record LearningSetupResponse(string Revision, Guid[] TeachingSkills, Guid[] LearningSkills, LearningGoalResponse[] Goals);
public sealed record LearningGoalResponse(Guid GoalId, string Text);
